using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using SshEnv.Core;

if (args.Contains("--catalog-child"))
{
    Console.WriteLine("READY");
    await Console.In.ReadLineAsync();
    return;
}

await WindowsReleaseTests.RunAsync();
await SubscriptionImportTests.RunAsync();
if (args.Contains("--vless-import-only"))
{
    return;
}

LogTests.Run();
await ApplicationCatalogTests.RunAsync();
await LatencyTesterTests.RunAsync();
if (args.Contains("--catalog-only"))
{
    return;
}

var root = Path.Combine(Path.GetTempPath(), "ssh-env-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var originalPath = Environment.GetEnvironmentVariable("PATH");
Environment.SetEnvironmentVariable("PATH", "");
try
{
    using var blocked = new HttpClient(new Handler((_, _) =>
        throw new InvalidOperationException("Network access is forbidden in the offline installation test.")));
    var installer = new DependencyInstaller(blocked, Path.Combine(root, "offline"));
    await installer.InstallAllAsync();
    RequireReady(installer);
    var before = HashFiles(installer.ToolsDirectory);
    await installer.InstallAllAsync();
    Assert(before.SequenceEqual(HashFiles(installer.ToolsDirectory)), "Repeated install changed valid binaries.");
    Console.WriteLine("PASS: offline installation and repeat install with empty PATH and forbidden HTTP");

    // Verify these are executable release binaries, not merely files with the expected names.
    await RunVersionAsync(Path.Combine(installer.ToolsDirectory, "sing-box.exe"), "version");
    await RunVersionAsync(Path.Combine(installer.ToolsDirectory, "tun2socks.exe"), "-version");
    FragmentationTests.Run(root);

    File.WriteAllBytes(Path.Combine(installer.ToolsDirectory, "sing-box.exe"), []);
    Assert(!installer.Check().HasSingBox, "Empty EXE was considered installed.");
    await installer.InstallAllAsync();
    RequireReady(installer);
    Console.WriteLine("PASS: repairs a partial/empty executable");

    using (var file = File.OpenWrite(Path.Combine(installer.ToolsDirectory, "sing-box.exe"))) { file.SetLength(1024); }
    Assert(!installer.Check().HasSingBox, "Truncated PE executable was considered installed.");
    await installer.InstallAllAsync();
    RequireReady(installer);
    Console.WriteLine("PASS: repairs a truncated executable with a valid PE header");

    if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
    {
        using var stream = typeof(DependencyInstaller).Assembly.GetManifestResourceStream("SshEnv.Dependencies.wintun-0.14.1.zip")!;
        using var zip = new ZipArchive(stream);
        zip.GetEntry("wintun/bin/x86/wintun.dll")!.ExtractToFile(Path.Combine(installer.ToolsDirectory, "wintun.dll"), true);
        Assert(!installer.Check().HasWintun, "Wrong-architecture DLL was considered installed.");
        await installer.InstallAllAsync();
        RequireReady(installer);
        Console.WriteLine("PASS: repairs an incompatible DLL");
    }

    var partial = new DependencyInstaller(blocked, Path.Combine(root, "partial"));
    Directory.CreateDirectory(Path.Combine(partial.ToolsDirectory, "wintun.dll"));
    await ThrowsAsync<AggregateException>(() => partial.InstallAllAsync());
    Assert(partial.Check().HasSingBox && partial.Check().HasTun2Socks, "Wintun failure prevented other packages installing.");
    Assert(!Directory.EnumerateDirectories(partial.ToolsDirectory, ".install-*").Any(), "Staging files leaked.");
    Console.WriteLine("PASS: independent packages install after a Wintun write failure");

    var external = Path.Combine(root, "external");
    Directory.CreateDirectory(external);
    File.Copy(Path.Combine(installer.ToolsDirectory, "sing-box.exe"), Path.Combine(external, "sing-box.exe"));
    File.WriteAllText(Path.Combine(installer.ToolsDirectory, "custom.txt"), "keep");
    File.WriteAllText(Path.Combine(installer.ToolsDirectory, "licenses", "custom.txt"), "keep");
    using (var locked = File.OpenRead(Path.Combine(installer.ToolsDirectory, "sing-box.exe")))
    {
        await ThrowsAsync<AggregateException>(() => installer.RemoveAllAsync());
        Assert(installer.Check() is { HasSingBox: true, HasTun2Socks: false, HasWintun: false },
            "Removal should report the locked executable and remove the other packages.");
    }
    await installer.RemoveAllAsync();
    await installer.RemoveAllAsync();
    Assert(installer.Check() is { HasSingBox: false, HasTun2Socks: false, HasWintun: false }, "Managed dependencies remain.");
    Assert(File.Exists(Path.Combine(installer.ToolsDirectory, "custom.txt")) &&
        Directory.GetFiles(Path.Combine(installer.ToolsDirectory, "licenses")).Single().EndsWith("custom.txt"),
        "Removal changed unrelated files or retained a managed notice.");
    Environment.SetEnvironmentVariable("PATH", external);
    Assert(string.Equals(installer.Check().SingBoxPath, Path.Combine(external, "sing-box.exe"), StringComparison.OrdinalIgnoreCase),
        "Removal changed the external executable.");
    Environment.SetEnvironmentVariable("PATH", "");
    await installer.InstallAllAsync();
    Assert(installer.Check() is { HasSingBox: true, HasTun2Socks: true, HasWintun: true }, "Offline reinstall after removal failed.");
    Console.WriteLine("PASS: managed dependency removal, retry, external file preservation, and offline reinstall");

    using (var cancel = new CancellationTokenSource())
    {
        cancel.Cancel();
        var cancelled = new DependencyInstaller(blocked, Path.Combine(root, "cancelled"));
        await ThrowsAsync<OperationCanceledException>(() => cancelled.InstallAllAsync(cancellationToken: cancel.Token));
        Assert(!Directory.Exists(cancelled.ToolsDirectory), "Cancelled install wrote files.");
    }
    Console.WriteLine("PASS: cancelled installation has no side effects");

    var downloadPath = Path.Combine(root, "download.zip");
    using (var stalled = new HttpClient(new Handler((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }))))
    {
        var clock = Stopwatch.StartNew();
        await ThrowsAsync<TimeoutException>(() =>
            new DependencyDownloader(stalled, TimeSpan.FromMilliseconds(150)).DownloadAsync("https://example.invalid/file", downloadPath, default));
        Assert(clock.Elapsed < TimeSpan.FromSeconds(3), "Response-body deadline was ignored.");
        Assert(!File.Exists(downloadPath), "Timed-out download left a partial file.");
        using var cancel = new CancellationTokenSource(150);
        await ThrowsAsync<OperationCanceledException>(() =>
            new DependencyDownloader(stalled).DownloadAsync("https://example.invalid/file", downloadPath, cancel.Token));
        Assert(!File.Exists(downloadPath), "Cancelled download left a partial file.");
    }
    Console.WriteLine("PASS: stalled response body times out; manual cancellation also cleans partial files");

    using (var stalledHeaders = new HttpClient(new Handler(async (_, token) =>
    {
        await Task.Delay(Timeout.Infinite, token);
        return new HttpResponseMessage(HttpStatusCode.OK);
    })))
    {
        await ThrowsAsync<TimeoutException>(() =>
            new DependencyDownloader(stalledHeaders, TimeSpan.FromMilliseconds(150)).DownloadAsync("https://example.invalid/file", downloadPath, default));
    }
    using (var failed = new HttpClient(new Handler((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))))
    {
        await ThrowsAsync<HttpRequestException>(() =>
            new DependencyDownloader(failed).DownloadAsync("https://example.invalid/file", downloadPath, default));
    }
    Console.WriteLine("PASS: header timeout and HTTP error finish predictably");

    if (args.Contains("--online"))
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ssh-env-regression");
        foreach (var id in new[] { "tun2socks", "wintun", "sing-box" })
        {
            var package = DependencyPackage.Get(id);
            await new DependencyDownloader(client).DownloadAsync(package.Url, downloadPath, default);
            using var archive = File.OpenRead(downloadPath);
            Assert(Convert.ToHexString(SHA256.HashData(archive)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase),
                "Official archive checksum mismatch: " + id);
            Console.WriteLine("PASS: real HTTPS download and SHA-256 verification for " + id);
        }
    }
    Console.WriteLine("All dependency regression checks passed.");
}
finally
{
    Environment.SetEnvironmentVariable("PATH", originalPath);
    Directory.Delete(root, recursive: true);
}

static void RequireReady(DependencyInstaller installer)
{
    var status = installer.Check();
    Assert(status.HasTun2Socks && status.HasWintun && status.HasSingBox, "Installed files failed the dependency check.");
    Assert(new[] { status.Tun2SocksPath, status.WintunPath, status.SingBoxPath }.All(path =>
        Path.GetDirectoryName(path) == installer.ToolsDirectory), "Dependencies were not colocated.");
    Assert(Directory.GetFiles(Path.Combine(installer.ToolsDirectory, "licenses")).Length == 4, "Missing third-party notices.");
}

static string[] HashFiles(string directory) => Directory.GetFiles(directory).Order()
    .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

static void Assert(bool condition, string message)
{
    if (!condition) { throw new InvalidOperationException(message); }
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

static async Task RunVersionAsync(string file, string argument)
{
    using var process = Process.Start(new ProcessStartInfo(file, argument)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
    })!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert(process.ExitCode == 0, Path.GetFileName(file) + " failed: " + await error);
        Console.WriteLine("PASS: " + Path.GetFileName(file) + " starts: " + (await output).Trim().Split('\n')[0]);
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => send(request, cancellationToken);
}

sealed class StalledStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
