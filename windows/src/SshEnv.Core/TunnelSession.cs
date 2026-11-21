using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace SshEnv.Core;

public sealed class TunnelSession : IDisposable
{
    static TunnelSession()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private readonly List<Process> processes = [];
    private readonly object gate = new();
    private readonly SessionRegistry sessionRegistry = new();
    private WindowsJobObject? jobObject;
    private CancellationTokenSource? shutdown;
    private bool disposed;
    private static readonly string[] ImportantProcessOutputMarkers =
    [
        "auth",
        "could not",
        "denied",
        "error",
        "failed",
        "fatal",
        "invalid",
        "not found",
        "panic",
        "permission",
        "refused",
        "reset",
        "timed out",
        "timeout",
        "unreachable",
        "warn"
    ];
    private static readonly string[] ActionableProcessOutputMarkers =
    [
        "auth",
        "could not",
        "denied",
        "error",
        "failed",
        "fatal",
        "invalid",
        "not found",
        "panic",
        "permission",
        "refused",
        "reset",
        "timed out",
        "timeout",
        "unreachable"
    ];

    public event Action<string>? Log;

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return shutdown is not null && !shutdown.IsCancellationRequested;
            }
        }
    }

    public static string? ResolveExecutable(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var executableExtensions = Environment.GetEnvironmentVariable("PATHEXT")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [".exe", ".cmd", ".bat"];

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in executableExtensions)
            {
                var candidate = Path.Combine(directory, fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static async Task<string> GetSshVersionAsync(CancellationToken cancellationToken = default)
    {
        var sshPath = ResolveExecutable("ssh") ?? throw new InvalidOperationException("OpenSSH client was not found in PATH.");
        using var process = CreateProcess(sshPath, ["-V"]);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ssh -V failed with code {process.ExitCode}: {(string.IsNullOrWhiteSpace(error) ? output : error).Trim()}");
        }

        return $"ssh: {sshPath}{Environment.NewLine}{(string.IsNullOrWhiteSpace(output) ? error : output).Trim()}";
    }

    public async Task<int> RunSocksAsync(ConnectConfig config, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var sessionLease = sessionRegistry.Acquire();
        using var linkedShutdown = StartSession(cancellationToken);

        try
        {
            var sshPath = config.SshPath ?? ResolveExecutable("ssh");
            if (sshPath is null)
            {
                throw new InvalidOperationException("OpenSSH client was not found. Pass ssh.exe path or install OpenSSH Client.");
            }

            await CleanupOrRejectOccupiedSocksPortAsync(config, linkedShutdown.Token);

            using var sshProcess = CreateSshProcess(sshPath, config);
            WriteLog($"Starting SSH SOCKS tunnel on {config.BindAddress}:{config.LocalPort}.");

            StartTrackedProcess(sshProcess);
            var sshOutput = PumpOutput(sshProcess, "ssh", linkedShutdown.Token);
            await sshProcess.WaitForExitAsync(linkedShutdown.Token);
            if (sshProcess.ExitCode != 0)
            {
                WriteProcessExitLog("SSH SOCKS tunnel", sshProcess.ExitCode, sshOutput);
            }

            return sshProcess.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            CompleteSession();
        }
    }

    public async Task<int> RunRouteAsync(RouteConfig config, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var sessionLease = sessionRegistry.Acquire();
        using var linkedShutdown = StartSession(cancellationToken);
        var rollbackCommands = new Stack<string[]>();

        try
        {
            var sshPath = config.Connect.SshPath ?? ResolveExecutable("ssh");
            if (sshPath is null)
            {
                throw new InvalidOperationException("OpenSSH client was not found. Pass ssh.exe path or install OpenSSH Client.");
            }

            var tun2SocksPath = config.Tun2SocksPath ?? ResolveExecutable("tun2socks");
            if (tun2SocksPath is null)
            {
                throw new InvalidOperationException("tun2socks was not found. Pass tun2socks.exe path or add it to PATH.");
            }

            await WarnAboutWinDivertBypassAsync(linkedShutdown.Token);
            await CleanupOrRejectOccupiedSocksPortAsync(config.Connect, linkedShutdown.Token);

            if (config.ApplyNetwork)
            {
                ValidateNetworkApplyPreconditions(config);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !IsWindowsAdministrator())
            {
                throw new InvalidOperationException("Route mode requires running OpenPath as Administrator on Windows because tun2socks must create or open a Wintun adapter.");
            }

            var conflictingTunRoute = await DetectConflictingTunRouteAsync(linkedShutdown.Token);
            if (conflictingTunRoute is not null && config.ApplyNetwork)
            {
                throw new InvalidOperationException($"Another TUN/default route appears active ({conflictingTunRoute}). Stop Hiddify/sing-box or other VPN clients before full route mode.");
            }

            if (conflictingTunRoute is not null)
            {
                WriteLog($"Warning: another TUN/default route appears active ({conflictingTunRoute}). Stop it before full route mode to avoid route/DNS conflicts.");
            }

            using var sshProcess = CreateSshProcess(sshPath, config.Connect);
            using var tun2SocksProcess = CreateProcess(tun2SocksPath, BuildTun2SocksArguments(config));

            WriteLog($"Starting route mode on SOCKS {config.Connect.BindAddress}:{config.Connect.LocalPort}.");
            StartTrackedProcess(sshProcess);
            var sshOutput = PumpOutput(sshProcess, "ssh", linkedShutdown.Token);

            await WaitForTcpPortAsync(config.Connect.BindAddress, config.Connect.LocalPort, TimeSpan.FromSeconds(10), linkedShutdown.Token);

            WriteLog("Starting tun2socks.");
            StartTrackedProcess(tun2SocksProcess);
            var tun2SocksOutput = PumpOutput(tun2SocksProcess, "tun2socks", linkedShutdown.Token);

            if (config.ApplyNetwork)
            {
                await ApplyWindowsNetworkAsync(config, rollbackCommands, linkedShutdown.Token);
            }
            else
            {
                PrintManualNetworkCommands();
            }

            WriteLog("Route mode is running.");
            var completedProcess = await Task.WhenAny(sshProcess.WaitForExitAsync(linkedShutdown.Token), tun2SocksProcess.WaitForExitAsync(linkedShutdown.Token));
            await completedProcess;

            if (sshProcess.HasExited)
            {
                WriteProcessExitLog("ssh", sshProcess.ExitCode, sshOutput, unexpected: true);
                return sshProcess.ExitCode == 0 ? 1 : sshProcess.ExitCode;
            }

            WriteProcessExitLog("tun2socks", tun2SocksProcess.ExitCode, tun2SocksOutput, unexpected: true);
            return tun2SocksProcess.ExitCode == 0 ? 1 : tun2SocksProcess.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            try
            {
                if (rollbackCommands.Count > 0)
                {
                    await RollbackWindowsNetworkAsync(rollbackCommands);
                }
            }
            finally
            {
                CompleteSession();
            }
        }
    }

    public async Task<int> RunSingBoxAsync(SingBoxConfig config, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var sessionLease = sessionRegistry.Acquire();
        using var linkedShutdown = StartSession(cancellationToken);
        SingBoxRuntimeConfig? runtimeConfig = null;

        try
        {
            var singBoxPath = config.SingBoxPath ?? ResolveExecutable("sing-box");
            if (singBoxPath is null)
            {
                throw new InvalidOperationException("sing-box was not found. Pass sing-box.exe path, install dependencies, or add it to PATH.");
            }

            if (!File.Exists(config.ConfigPath))
            {
                throw new InvalidOperationException($"sing-box config file does not exist: {config.ConfigPath}");
            }

            var directRules = config.DirectRules?
                .Where(rule => !string.IsNullOrWhiteSpace(rule.Type) && !string.IsNullOrWhiteSpace(rule.Value))
                .ToArray()
                ?? [];
            var effectiveConfigPath = config.ConfigPath;
            if (directRules.Length > 0 || config.TlsFragmentation is not null)
            {
                runtimeConfig = SingBoxConfigDirectRules.CreateRuntimeConfig(config.ConfigPath, directRules, config.TlsFragmentation);
                effectiveConfigPath = runtimeConfig.ConfigPath;
            }

            if (directRules.Length > 0)
            {
                WriteLog($"TUN exclusions/options enabled: {string.Join(", ", directRules.Select(SingBoxConfigDirectRules.Format))}.");
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !IsWindowsAdministrator())
            {
                WriteLog("Warning: sing-box TUN configs usually require Administrator rights on Windows.");
            }

            await WarnAboutWinDivertBypassAsync(linkedShutdown.Token);

            var conflictingTunRoute = await DetectConflictingTunRouteAsync(linkedShutdown.Token);
            if (conflictingTunRoute is not null)
            {
                throw new InvalidOperationException($"Another TUN/default route appears active ({conflictingTunRoute}). Stop Hiddify/sing-box or other VPN clients before starting sing-box mode.");
            }

            using var singBoxProcess = CreateProcess(
                singBoxPath,
                BuildSingBoxArguments(new SingBoxConfig(singBoxPath, effectiveConfigPath)));
            WriteLog("Starting sing-box TUN mode.");
            StartTrackedProcess(singBoxProcess);
            var singBoxOutput = PumpOutput(singBoxProcess, "sing-box", linkedShutdown.Token);

            WriteLog("sing-box mode is running.");
            await singBoxProcess.WaitForExitAsync(linkedShutdown.Token);
            WriteProcessExitLog("sing-box", singBoxProcess.ExitCode, singBoxOutput);
            return singBoxProcess.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            try
            {
                runtimeConfig?.Dispose();
            }
            finally
            {
                CompleteSession();
            }
        }
    }

    public async Task<int> RunSingBoxSystemProxyAsync(SingBoxSystemProxyConfig config, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var sessionLease = sessionRegistry.Acquire();
        using var linkedShutdown = StartSession(cancellationToken);
        WindowsSystemProxy? systemProxy = null;

        try
        {
            var singBoxPath = config.SingBoxPath ?? ResolveExecutable("sing-box");
            if (singBoxPath is null)
            {
                throw new InvalidOperationException("sing-box was not found. Pass sing-box.exe path, install dependencies, or add it to PATH.");
            }

            if (!File.Exists(config.ConfigPath))
            {
                throw new InvalidOperationException($"sing-box config file does not exist: {config.ConfigPath}");
            }

            using var runtimeConfig = config.TlsFragmentation is null ? null :
                SingBoxConfigDirectRules.CreateRuntimeConfig(config.ConfigPath, [], config.TlsFragmentation);
            var effectiveConfigPath = runtimeConfig?.ConfigPath ?? config.ConfigPath;
            ValidateSingBoxSystemProxyConfig(effectiveConfigPath);
            await WarnAboutWinDivertBypassAsync(linkedShutdown.Token);

            using var singBoxProcess = CreateProcess(singBoxPath, BuildSingBoxArguments(new SingBoxConfig(singBoxPath, effectiveConfigPath)));
            WriteLog("Starting sing-box system proxy.");
            StartTrackedProcess(singBoxProcess);
            var singBoxOutput = PumpOutput(singBoxProcess, "sing-box", linkedShutdown.Token);

            await WaitForTcpPortAsync(IPAddress.Loopback, config.LocalProxyPort, TimeSpan.FromSeconds(10), linkedShutdown.Token);
            systemProxy = WindowsSystemProxy.Enable($"127.0.0.1:{config.LocalProxyPort}");
            WriteLog($"Windows system proxy enabled: 127.0.0.1:{config.LocalProxyPort}");

            WriteLog("sing-box system proxy mode is running.");
            await singBoxProcess.WaitForExitAsync(linkedShutdown.Token);
            WriteProcessExitLog("sing-box", singBoxProcess.ExitCode, singBoxOutput);
            return singBoxProcess.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            try
            {
                if (systemProxy is not null)
                {
                    systemProxy.Dispose();
                    WriteLog("Windows system proxy was restored.");
                }
            }
            finally
            {
                CompleteSession();
            }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (shutdown is not null)
            {
                StopTrackedProcesses();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (shutdown is not null)
            {
                StopTrackedProcesses();
            }
        }
    }

    private void CompleteSession()
    {
        lock (gate)
        {
            try
            {
                StopTrackedProcesses();
            }
            finally
            {
                shutdown = null;
            }
        }
    }

    private void StopTrackedProcesses()
    {
        shutdown?.Cancel();
        foreach (var process in processes)
        {
            StopProcess(process);
        }

        processes.Clear();
        sessionRegistry.Clear();
        jobObject?.Dispose();
        jobObject = null;
    }

    public static IEnumerable<string> BuildSshArguments(ConnectConfig config)
    {
        yield return "-N";
        yield return "-T";
        yield return "-D";
        yield return $"{config.BindAddress}:{config.LocalPort}";
        yield return "-p";
        yield return config.SshPort.ToString();
        yield return "-o";
        yield return "ExitOnForwardFailure=yes";
        yield return "-o";
        yield return "ServerAliveInterval=30";
        yield return "-o";
        yield return "ServerAliveCountMax=3";
        yield return "-o";
        yield return "StrictHostKeyChecking=accept-new";

        if (config.IdentityFile is not null)
        {
            yield return "-i";
            yield return config.IdentityFile;
        }

        foreach (var sshOption in config.SshOptions)
        {
            yield return "-o";
            yield return sshOption;
        }

        yield return $"{config.User}@{config.Host}";
    }

    public static IEnumerable<string> BuildTun2SocksArguments(RouteConfig config)
    {
        yield return "--device";
        yield return config.TunName;
        yield return "--proxy";
        yield return $"socks5://{config.Connect.BindAddress}:{config.Connect.LocalPort}";

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !string.IsNullOrWhiteSpace(config.PrimaryInterface))
        {
            yield return "--interface";
            yield return config.PrimaryInterface;
        }
    }

    public static IEnumerable<string> BuildSingBoxArguments(SingBoxConfig config)
    {
        yield return "run";
        yield return "-c";
        yield return config.ConfigPath;
    }

    private CancellationTokenSource StartSession(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (shutdown is not null)
            {
                throw new InvalidOperationException("A tunnel session is already running.");
            }

            sessionRegistry.CleanupPreviousSession(message => WriteLog(message));
            jobObject?.Dispose();
            jobObject = OperatingSystem.IsWindows() ? new WindowsJobObject() : null;
            shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return shutdown;
        }
    }

    private void StartTrackedProcess(Process process)
    {
        lock (gate)
        {
            shutdown!.Token.ThrowIfCancellationRequested();
            process.Start();
            processes.Add(process);
            try
            {
                jobObject?.Add(process);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                WriteLog($"Could not attach child process to cleanup job object: {exception.Message}");
            }

            sessionRegistry.Save(processes.Where(process => !process.HasExited).Select(process => process.Id).ToArray());
        }
    }

    private static Process CreateProcess(string fileName, IEnumerable<string> arguments)
    {
        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = GetProcessOutputEncoding(),
            StandardOutputEncoding = GetProcessOutputEncoding(),
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return process;
    }

    private Process CreateSshProcess(string sshPath, ConnectConfig config)
    {
        var process = CreateProcess(sshPath, BuildSshArguments(config));
        if (string.IsNullOrEmpty(config.Password))
        {
            return process;
        }

        var askPassPath = ResolveAskPassPath();
        if (askPassPath is null)
        {
            throw new InvalidOperationException("Password auth requires SshEnv.AskPass.exe next to the application binaries.");
        }

        process.StartInfo.Environment["SSH_ASKPASS"] = askPassPath;
        process.StartInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        process.StartInfo.Environment["SSH_ENV_ASKPASS_MODE"] = "1";
        process.StartInfo.Environment["SSH_ENV_PASSWORD"] = config.Password;
        process.StartInfo.Environment["DISPLAY"] = "ssh-env";
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("BatchMode=no");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("PreferredAuthentications=publickey,password,keyboard-interactive");
        WriteLog("SSH password auth is enabled for this session.");
        return process;
    }

    private static string? ResolveAskPassPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SSH_ENV_ASKPASS_EXECUTABLE"),
            Path.Combine(baseDirectory, "SshEnv.AskPass.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "SshEnv.AskPass", "bin", "Debug", "net10.0-windows", "SshEnv.AskPass.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "SshEnv.AskPass", "bin", "Release", "net10.0-windows", "SshEnv.AskPass.exe"))
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private ProcessOutputSummary PumpOutput(Process process, string processName, CancellationToken cancellationToken)
    {
        var output = new ProcessOutputSummary();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    RecordProcessOutput(processName, line, output);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }, CancellationToken.None);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    RecordProcessOutput(processName, line, output);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }, CancellationToken.None);
        return output;
    }

    private static async Task WaitForTcpPortAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(TimeSpan.FromMilliseconds(500));
                await client.ConnectAsync(address, port, attemptTimeout.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                continue;
            }
            catch (SocketException)
            {
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for TCP endpoint {address}:{port}.");
    }

    private static void ValidateSingBoxSystemProxyConfig(string configPath)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("inbounds", out var inbounds) || inbounds.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new InvalidOperationException("System Proxy mode requires a sing-box config with a mixed inbound.");
            }

            foreach (var inbound in inbounds.EnumerateArray())
            {
                if (inbound.TryGetProperty("type", out var type) &&
                    type.GetString()?.Equals("mixed", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return;
                }
            }

            throw new InvalidOperationException("Selected config is not a System Proxy config. Use `Import VLESS Proxy`, not `Import VLESS TUN`, then start System Proxy.");
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidOperationException($"Could not parse sing-box config: {exception.Message}", exception);
        }
    }

    private static void ValidateNetworkApplyPreconditions(RouteConfig config)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new InvalidOperationException("Network apply is currently implemented only for Windows.");
        }

        if (!IsAdministrator())
        {
            throw new InvalidOperationException("Network apply requires an elevated Administrator terminal.");
        }

        if (string.IsNullOrWhiteSpace(config.PrimaryInterface))
        {
            throw new ArgumentException("Primary interface is required when applying Windows routes.");
        }

        if (config.PrimaryGateway is null)
        {
            throw new ArgumentException("Primary gateway is required to keep the SSH server route outside the TUN default route.");
        }

        if (config.SshServerAddress is null)
        {
            throw new ArgumentException("SSH server IP is required when host is not an IP address.");
        }
    }

    public static bool IsWindowsAdministrator()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && IsAdministrator();
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private async Task ApplyWindowsNetworkAsync(RouteConfig config, Stack<string[]> rollbackCommands, CancellationToken cancellationToken)
    {
        var sshServerRoute = $"{config.SshServerAddress}/32";
        var tunAddress = await EnsureTunAddressAsync(config, cancellationToken);

        await RunNetshAsync(
            [
                "interface", "ipv4", "set", "dnsservers",
                $"name={config.TunName}",
                "static",
                $"address={config.DnsServer}",
                "register=none",
                "validate=no"
            ],
            cancellationToken);

        await AddRouteReplacingExistingAsync(
            sshServerRoute,
            config.PrimaryInterface,
            config.PrimaryGateway!.ToString(),
            cancellationToken);
        rollbackCommands.Push(["interface", "ipv4", "delete", "route", sshServerRoute, config.PrimaryInterface, config.PrimaryGateway!.ToString()]);

        foreach (var routePrefix in new[] { "0.0.0.0/1", "128.0.0.0/1" })
        {
            await AddRouteReplacingExistingAsync(routePrefix, config.TunName, tunAddress.ToString(), cancellationToken);
            rollbackCommands.Push(["interface", "ipv4", "delete", "route", routePrefix, config.TunName, tunAddress.ToString()]);
        }

        WriteLog("Windows routes/DNS were applied.");
    }

    private async Task<IPAddress> EnsureTunAddressAsync(RouteConfig config, CancellationToken cancellationToken)
    {
        await WaitForInterfaceAsync(config.TunName, TimeSpan.FromSeconds(20), cancellationToken);
        await Task.Delay(750, cancellationToken);

        if (await AnyInterfaceHasAddressAsync(config.TunAddress, cancellationToken))
        {
            if (await InterfaceHasAddressAsync(config.TunName, config.TunAddress, cancellationToken))
            {
                return config.TunAddress;
            }

            var replacementAddress = await FindAvailableTunAddressAsync(config.TunAddress, cancellationToken);
            WriteLog($"TUN address {config.TunAddress} is busy; using {replacementAddress}.");
            return await SetInterfaceAddressWithRetryAsync(config.TunName, replacementAddress, config.TunMask, TimeSpan.FromSeconds(20), cancellationToken);
        }

        return await SetInterfaceAddressWithRetryAsync(config.TunName, config.TunAddress, config.TunMask, TimeSpan.FromSeconds(20), cancellationToken);
    }

    private async Task<IPAddress> SetInterfaceAddressWithRetryAsync(
        string interfaceName,
        IPAddress address,
        IPAddress mask,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        CommandResult? lastResult = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastResult = await SetInterfaceAddressAsync(interfaceName, address, mask, cancellationToken);
            if (lastResult.ExitCode == 0)
            {
                return address;
            }

            if (await InterfaceHasAddressAsync(interfaceName, address, cancellationToken))
            {
                return address;
            }

            await Task.Delay(750, cancellationToken);
        }

        throw new InvalidOperationException($"netsh failed while setting TUN address: {lastResult?.CombinedOutput.Trim()}");
    }

    private async Task<CommandResult> SetInterfaceAddressAsync(string interfaceName, IPAddress address, IPAddress mask, CancellationToken cancellationToken)
    {
        return await RunNetshAsync(
            [
                "interface", "ipv4", "set", "address",
                $"name={interfaceName}",
                "source=static",
                $"addr={address}",
                $"mask={mask}"
            ],
            cancellationToken,
            throwOnError: false);
    }

    private async Task WaitForInterfaceAsync(string interfaceName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? lastOutput = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RunNetshAsync(
                ["interface", "ipv4", "show", "addresses", $"name={interfaceName}"],
                cancellationToken,
                throwOnError: false);

            if (result.ExitCode == 0 && HasNetworkInterface(interfaceName))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.CombinedOutput))
            {
                lastOutput = result.CombinedOutput.Trim();
            }

            await Task.Delay(500, cancellationToken);
        }

        var reason = string.IsNullOrWhiteSpace(lastOutput) ? string.Empty : $" Last response: {lastOutput}";
        throw new TimeoutException($"Timed out waiting for interface {interfaceName}.{reason}");
    }

    private static bool HasNetworkInterface(string interfaceName)
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Any(networkInterface => networkInterface.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> InterfaceHasAddressAsync(string interfaceName, IPAddress address, CancellationToken cancellationToken)
    {
        var result = await RunNetshAsync(
            ["interface", "ipv4", "show", "addresses", $"name={interfaceName}"],
            cancellationToken,
            throwOnError: false);

        if (result.ExitCode != 0)
        {
            return false;
        }

        return result.CombinedOutput.Contains(address.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> AnyInterfaceHasAddressAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var result = await RunNetshAsync(["interface", "ipv4", "show", "addresses"], cancellationToken, throwOnError: false);
        return result.CombinedOutput.Contains(address.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IPAddress> FindAvailableTunAddressAsync(IPAddress preferredAddress, CancellationToken cancellationToken)
    {
        var preferredBytes = preferredAddress.GetAddressBytes();
        if (preferredBytes.Length != 4)
        {
            throw new InvalidOperationException("Only IPv4 TUN addresses are supported.");
        }

        for (var subnet = preferredBytes[2] + 1; subnet <= 223; subnet++)
        {
            var candidate = new IPAddress([preferredBytes[0], preferredBytes[1], (byte)subnet, preferredBytes[3]]);
            if (!await AnyInterfaceHasAddressAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        for (var subnet = 18; subnet <= 31; subnet++)
        {
            var candidate = new IPAddress([198, (byte)subnet, 0, 1]);
            if (!await AnyInterfaceHasAddressAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find an available IPv4 address for the TUN adapter.");
    }

    private async Task AddRouteReplacingExistingAsync(string routePrefix, string interfaceName, string nextHop, CancellationToken cancellationToken)
    {
        await RunNetshAsync(
            ["interface", "ipv4", "delete", "route", routePrefix, interfaceName, nextHop],
            cancellationToken,
            throwOnError: false);

        await RunNetshAsync(
            [
                "interface", "ipv4", "add", "route",
                routePrefix,
                interfaceName,
                nextHop,
                "metric=1"
            ],
            cancellationToken);
    }

    private async Task RollbackWindowsNetworkAsync(Stack<string[]> rollbackCommands)
    {
        WriteLog("Restoring Windows routes.");
        while (rollbackCommands.TryPop(out var command))
        {
            try
            {
                await RunNetshAsync(command, CancellationToken.None);
            }
            catch (InvalidOperationException exception)
            {
                WriteLog($"Error: could not restore Windows route: {exception.Message}");
            }
        }
    }

    private async Task<CommandResult> RunNetshAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool throwOnError = true)
    {
        using var process = CreateProcess("netsh", arguments);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var combinedOutput = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
        if (process.ExitCode != 0 && throwOnError)
        {
            throw new InvalidOperationException($"netsh failed with code {process.ExitCode}: {combinedOutput}");
        }

        return new CommandResult(process.ExitCode, output, error);
    }

    private static async Task<string?> DetectConflictingTunRouteAsync(CancellationToken cancellationToken)
    {
        var routeResult = await RunCommandAsync("route", ["print", "-4"], cancellationToken);
        if (routeResult.ExitCode != 0)
        {
            return null;
        }

        var routeOutput = routeResult.CombinedOutput;
        if (routeOutput.Contains("sing-tun", StringComparison.OrdinalIgnoreCase))
        {
            return "sing-tun";
        }

        if (routeOutput.Contains("tun0", StringComparison.OrdinalIgnoreCase))
        {
            return "tun0";
        }

        if (routeOutput.Contains("172.19.0.2", StringComparison.OrdinalIgnoreCase))
        {
            return "172.19.0.2";
        }

        return null;
    }

    private async Task WarnAboutWinDivertBypassAsync(CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var detections = new List<string>();
        if (Process.GetProcessesByName("winws").Length > 0)
        {
            detections.Add("winws.exe process");
        }

        foreach (var serviceName in new[] { "zapret", "WinDivert", "WinDivert14" })
        {
            var serviceState = await TryGetServiceStateAsync(serviceName, cancellationToken);
            if (serviceState is not null)
            {
                detections.Add($"{serviceName} service is {serviceState}");
            }
        }

        if (detections.Count == 0)
        {
            return;
        }

        WriteLog($"Warning: zapret/WinDivert appears active ({string.Join(", ", detections)}); route and sing-box modes may conflict.");
    }

    private static async Task<string?> TryGetServiceStateAsync(string serviceName, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("sc", ["query", serviceName], cancellationToken);
        if (result.ExitCode != 0)
        {
            return null;
        }

        foreach (var line in result.CombinedOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmedLine = line.Trim();
            if (!trimmedLine.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length >= 4 ? parts[3] : "present";
        }

        return "present";
    }

    private async Task CleanupOrRejectOccupiedSocksPortAsync(ConnectConfig config, CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var processIds = await FindListeningProcessIdsAsync(config.BindAddress, config.LocalPort, cancellationToken);
        foreach (var processId in processIds)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                if (process.ProcessName.Equals("ssh", StringComparison.OrdinalIgnoreCase))
                {
                    WriteLog($"Stopping stale ssh.exe already listening on {config.BindAddress}:{config.LocalPort}.");
                    StopProcess(process);
                    continue;
                }

                throw new InvalidOperationException($"SOCKS port {config.BindAddress}:{config.LocalPort} is already used by {process.ProcessName} ({processId}). Stop that process or choose another local port.");
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private static async Task<IReadOnlyList<int>> FindListeningProcessIdsAsync(IPAddress bindAddress, int port, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("netstat", ["-ano", "-p", "TCP"], cancellationToken);
        if (result.ExitCode != 0)
        {
            return [];
        }

        var processIds = new HashSet<int>();
        foreach (var line in result.CombinedOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!parts[^2].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[^1], out var processId))
            {
                continue;
            }

            if (IsMatchingEndpoint(parts[1], bindAddress, port))
            {
                processIds.Add(processId);
            }
        }

        return processIds.ToArray();
    }

    private static bool IsMatchingEndpoint(string endpoint, IPAddress bindAddress, int port)
    {
        var normalizedEndpoint = endpoint.Trim('[', ']');
        if (!normalizedEndpoint.EndsWith($":{port}", StringComparison.Ordinal))
        {
            return false;
        }

        var addressPart = normalizedEndpoint[..^($":{port}".Length)].Trim('[', ']');
        return addressPart is "0.0.0.0" or "::" ||
            addressPart.Equals(bindAddress.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CommandResult> RunCommandAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = CreateProcess(fileName, arguments);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new CommandResult(process.ExitCode, output, error);
    }

    private void PrintManualNetworkCommands()
    {
        WriteLog("Diagnostic route mode is running without applying Windows routes/DNS.");
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Encoding GetProcessOutputEncoding()
    {
        return Encoding.UTF8;
    }

    private void WriteLog(string message)
    {
        Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void RecordProcessOutput(string processName, string line, ProcessOutputSummary output)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var trimmedLine = line.Trim();
        if (ShouldSuppressProcessOutput(processName, trimmedLine))
        {
            return;
        }

        output.Record(trimmedLine);
        if (ShouldLogProcessOutput(trimmedLine))
        {
            WriteLog($"{processName}: {trimmedLine}");
        }
    }

    private void WriteProcessExitLog(string processName, int exitCode, ProcessOutputSummary output, bool unexpected = false)
    {
        var reason = output.TryGetReason();
        var status = unexpected ? "exited unexpectedly" : "stopped";
        var message = string.IsNullOrWhiteSpace(reason)
            ? $"{processName} {status} with code {exitCode}."
            : $"{processName} {status} with code {exitCode}: {reason}";
        WriteLog(message);
    }

    private static bool ShouldLogProcessOutput(string line)
    {
        return ImportantProcessOutputMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldSuppressProcessOutput(string processName, string line)
    {
        return processName.Equals("tun2socks", StringComparison.OrdinalIgnoreCase) &&
            IsStructuredWarning(line) &&
            !ActionableProcessOutputMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStructuredWarning(string line)
    {
        return line.StartsWith("{", StringComparison.Ordinal) &&
            line.Contains("\"level\"", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains("\"warn\"", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("\"warning\"", StringComparison.OrdinalIgnoreCase));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error)
    {
        public string CombinedOutput => string.IsNullOrWhiteSpace(Error) ? Output : Error;
    }

    private sealed class ProcessOutputSummary
    {
        private const int MaxRecentLines = 8;
        private readonly object gate = new();
        private readonly Queue<string> recentLines = [];

        public void Record(string line)
        {
            lock (gate)
            {
                if (recentLines.Count >= MaxRecentLines)
                {
                    recentLines.Dequeue();
                }

                recentLines.Enqueue(line);
            }
        }

        public string? TryGetReason()
        {
            lock (gate)
            {
                return recentLines.LastOrDefault(ShouldLogProcessOutput)
                    ?? recentLines.LastOrDefault();
            }
        }
    }
}
