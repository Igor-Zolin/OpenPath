using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SshEnv.Core;

public sealed record DependencyStatus(
    bool HasSsh, string? SshPath,
    bool HasTun2Socks, string? Tun2SocksPath,
    bool HasWintun, string? WintunPath,
    bool HasSingBox, string? SingBoxPath,
    string ToolsDirectory);

public sealed record DependencyInstallResult(
    string Tun2SocksPath, string WintunPath, string? SingBoxPath, string ToolsDirectory);

public sealed record SingBoxInstallResult(string SingBoxPath, string ToolsDirectory);

public sealed class DependencyInstaller(HttpClient? httpClient = null, string? toolsDirectory = null)
{
    private static readonly HttpClient SharedClient = CreateHttpClient();
    private static readonly string DefaultToolsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SshEnv", "tools");
    private readonly DependencyDownloader downloader = new(httpClient ?? SharedClient);
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public string ToolsDirectory { get; } = Path.GetFullPath(toolsDirectory ?? DefaultToolsDirectory);

    public DependencyStatus Check()
    {
        var ssh = TunnelSession.ResolveExecutable("ssh");
        var tun = FindInstalledExecutable("tun2socks");
        var sing = FindInstalledExecutable("sing-box");
        var wintun = FindInstalledWintun(tun);
        return new DependencyStatus(ssh is not null, ssh, tun is not null, tun,
            wintun is not null, wintun, sing is not null, sing, ToolsDirectory);
    }

    public async Task<DependencyInstallResult> InstallAllAsync(
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await InstallPackagesAsync(["tun2socks", "wintun", "sing-box"], progress, cancellationToken);
        return new DependencyInstallResult(Path.Combine(ToolsDirectory, "tun2socks.exe"),
            Path.Combine(ToolsDirectory, "wintun.dll"), Path.Combine(ToolsDirectory, "sing-box.exe"), ToolsDirectory);
    }

    public async Task<DependencyInstallResult> InstallTunStackAsync(
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await InstallPackagesAsync(["tun2socks", "wintun"], progress, cancellationToken);
        return new DependencyInstallResult(Path.Combine(ToolsDirectory, "tun2socks.exe"),
            Path.Combine(ToolsDirectory, "wintun.dll"), null, ToolsDirectory);
    }

    public async Task<SingBoxInstallResult> InstallSingBoxAsync(
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await InstallPackagesAsync(["sing-box"], progress, cancellationToken);
        return new SingBoxInstallResult(Path.Combine(ToolsDirectory, "sing-box.exe"), ToolsDirectory);
    }

    public async Task RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            using var toolsLock = AcquireToolsLock();
            // The portable, installed and CLI editions share the default tools and tunnel session.
            using var sessionLock = ToolsDirectory.Equals(DefaultToolsDirectory, StringComparison.OrdinalIgnoreCase)
                ? new SessionRegistry().Acquire()
                : null;
            DeleteManagedFiles(["tun2socks.exe", "wintun.dll", "sing-box.exe"]);
            DeleteManagedFiles([
                @"licenses\tun2socks-LICENSE.txt", @"licenses\wintun-LICENSE.txt",
                @"licenses\sing-box-LICENSE.txt", @"licenses\THIRD-PARTY-NOTICES.txt"
            ]);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void DeleteManagedFiles(string[] files)
    {
        var errors = new List<Exception>();
        foreach (var file in files)
        {
            try
            {
                File.Delete(Path.Combine(ToolsDirectory, file));
            }
            catch (DirectoryNotFoundException)
            {
                // The licenses directory is only present when notices have been bundled.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add(new IOException($"{file}: {exception.Message}", exception));
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException("Some dependency files could not be removed.", errors);
        }
    }

    private async Task InstallPackagesAsync(
        string[] ids, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            using var toolsLock = AcquireToolsLock();
            await InstallNoticesAsync(cancellationToken);
            var errors = new List<Exception>();
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await InstallPackageAsync(DependencyPackage.Get(id), progress, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                    or InvalidOperationException or HttpRequestException or TimeoutException)
                {
                    var error = new InvalidOperationException($"{id}: {exception.Message}", exception);
                    errors.Add(error);
                    progress?.Report(error.Message);
                }
            }

            if (errors.Count > 0)
            {
                throw new AggregateException("Some dependencies could not be installed. See the errors below.", errors);
            }

            progress?.Report("Dependency installation complete.");
        }
        finally
        {
            operationGate.Release();
        }
    }

    private FileStream AcquireToolsLock()
    {
        Directory.CreateDirectory(ToolsDirectory);
        try
        {
            return File.Open(Path.Combine(ToolsDirectory, ".dependencies.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
        {
            throw new InvalidOperationException("Another SshEnv instance is already managing dependencies. Try again when it finishes.", exception);
        }
    }

    private async Task InstallPackageAsync(
        DependencyPackage package, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(ToolsDirectory, package.Binary);
        if (IsCompatibleBinary(destination))
        {
            progress?.Report($"{package.Binary} is already installed.");
            return;
        }

        // Stage on the destination volume so cancellation or a failed copy never leaves a partial executable.
        var staging = Path.Combine(ToolsDirectory, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var archivePath = Path.Combine(staging, package.ArchiveName);
            await using (var bundled = typeof(DependencyInstaller).Assembly
                .GetManifestResourceStream("SshEnv.Dependencies." + package.ArchiveName))
            {
                if (bundled is not null)
                {
                    progress?.Report($"Installing {package.Binary} from the application bundle.");
                    await using var output = File.Create(archivePath);
                    await bundled.CopyToAsync(output, cancellationToken);
                }
                else
                {
                    progress?.Report($"Downloading {package.Binary} (timeout: 90 seconds).");
                    await downloader.DownloadAsync(package.Url, archivePath, cancellationToken);
                }
            }

            await using (var file = File.OpenRead(archivePath))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
                if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SHA-256 verification failed for {package.ArchiveName}.");
                }
            }

            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry(package.ArchiveEntry)
                ?? throw new InvalidDataException($"Archive does not contain {package.ArchiveEntry}.");
            var stagedBinary = Path.Combine(staging, package.Binary);
            await using (var input = entry.Open())
            await using (var output = File.Create(stagedBinary))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            if (!IsCompatibleBinary(stagedBinary))
            {
                throw new InvalidDataException($"{package.Binary} is not a valid {RuntimeInformation.ProcessArchitecture} Windows binary.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedBinary, destination, overwrite: true);
            progress?.Report($"{package.Binary} installed.");
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task InstallNoticesAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(DependencyInstaller).Assembly;
        const string prefix = "SshEnv.Dependencies.";
        foreach (var name in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix) && name.EndsWith(".txt", StringComparison.Ordinal)))
        {
            var directory = Path.Combine(ToolsDirectory, "licenses");
            Directory.CreateDirectory(directory);
            await using var input = assembly.GetManifestResourceStream(name)!;
            await using var output = File.Create(Path.Combine(directory, name[prefix.Length..]));
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    public bool StartOpenSshClientInstall()
    {
        var powershell = TunnelSession.ResolveExecutable("powershell") ?? TunnelSession.ResolveExecutable("pwsh");
        if (powershell is null) { return false; }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0\"",
                UseShellExecute = true,
                Verb = "runas"
            }
        };
        return process.Start();
    }

    private string? FindInstalledExecutable(string name)
    {
        var local = Path.Combine(ToolsDirectory, name + ".exe");
        if (IsCompatibleBinary(local)) { return local; }
        var external = TunnelSession.ResolveExecutable(name);
        return external is not null && IsCompatibleBinary(external) ? external : null;
    }

    private string? FindInstalledWintun(string? tun2SocksPath)
    {
        // A DLL in another application's directory cannot satisfy this executable's loader.
        var path = Path.Combine(tun2SocksPath is null ? ToolsDirectory : Path.GetDirectoryName(tun2SocksPath)!, "wintun.dll");
        return IsCompatibleBinary(path) ? path : null;
    }

    private static bool IsCompatibleBinary(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var reader = new PEReader(file);
            var machine = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => Machine.Amd64,
                Architecture.Arm64 => Machine.Arm64,
                Architecture.X86 => Machine.I386,
                _ => throw new PlatformNotSupportedException()
            };
            var headers = reader.PEHeaders;
            return headers.PEHeader is not null && headers.CoffHeader.Machine == machine &&
                headers.PEHeader.SizeOfHeaders <= file.Length && headers.SectionHeaders.Length > 0 &&
                headers.SectionHeaders.All(section => section.PointerToRawData >= 0 && section.SizeOfRawData >= 0 &&
                    (long)section.PointerToRawData + section.SizeOfRawData <= file.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ssh-env", "0.1"));
        return client;
    }
}
