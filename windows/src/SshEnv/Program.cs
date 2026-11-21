using System.Net;
using SshEnv.Core;

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "check" => await RunCheckAsync(),
                "connect" => await RunConnectAsync(args.Skip(1).ToArray()),
                "deps" => await RunDepsAsync(args.Skip(1).ToArray()),
                "import-vless" => await RunImportVlessAsync(args.Skip(1).ToArray()),
                "plan" => RunPlan(),
                "route" => await RunRouteAsync(args.Skip(1).ToArray()),
                "sing-box" => await RunSingBoxAsync(args.Skip(1).ToArray()),
                _ => Fail($"Unknown command: {args[0]}")
            };
        }
        catch (ArgumentException exception)
        {
            return Fail(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Fail(exception.Message);
        }
        catch (TimeoutException exception)
        {
            return Fail(exception.Message);
        }
        catch (Exception exception)
        {
            return Fail(exception.Message);
        }
    }

    private static async Task<int> RunCheckAsync()
    {
        Console.WriteLine(await TunnelSession.GetSshVersionAsync());
        return 0;
    }

    private static async Task<int> RunConnectAsync(string[] args)
    {
        var config = ParseConnect(args);
        using var session = CreateConsoleSession();
        Console.WriteLine("Press Ctrl+C to stop.");
        return await RunWithCtrlCAsync(session, cancellationToken => session.RunSocksAsync(config, cancellationToken));
    }

    private static async Task<int> RunRouteAsync(string[] args)
    {
        var config = ParseRoute(args);
        using var session = CreateConsoleSession();
        Console.WriteLine("Press Ctrl+C to stop.");
        return await RunWithCtrlCAsync(session, cancellationToken => session.RunRouteAsync(config, cancellationToken));
    }

    private static async Task<int> RunSingBoxAsync(string[] args)
    {
        var config = ParseSingBox(args);
        using var session = CreateConsoleSession();
        Console.WriteLine("Press Ctrl+C to stop.");
        return await RunWithCtrlCAsync(session, cancellationToken => session.RunSingBoxAsync(config, cancellationToken));
    }

    private static async Task<int> RunImportVlessAsync(string[] args)
    {
        var parser = new ArgumentReader(args);
        string? link = null;
        var systemProxyPort = 2080;
        var outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SshEnv",
            "configs");

        while (parser.TryRead(out var option, out var value))
        {
            switch (option)
            {
                case "--link":
                    link = RequireNonEmpty(value, option);
                    break;
                case "--out-dir":
                    outputDirectory = RequireNonEmpty(value, option);
                    break;
                case "--system-proxy-port":
                    systemProxyPort = ParsePort(value, option);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {option}");
            }
        }

        var result = await SingBoxProfileImporter.ImportLinkAsync(RequireNonEmpty(link, "--link"), outputDirectory, systemProxyPort);
        foreach (var importedProfile in result.Profiles)
        {
            Console.WriteLine($"Imported VLESS profile: {importedProfile.Name}");
            Console.WriteLine($"TUN config: {importedProfile.TunConfigPath}");
            Console.WriteLine($"System proxy config: {importedProfile.SystemProxyConfigPath}");
        }

        if (result.SkippedEntries > 0)
        {
            Console.WriteLine($"Skipped subscription entries: {result.SkippedEntries}");
        }
        return 0;
    }

    private static async Task<int> RunDepsAsync(string[] args)
    {
        var command = args.FirstOrDefault() ?? "check";
        var installer = new DependencyInstaller();

        switch (command)
        {
            case "check":
                PrintDependencyStatus(installer.Check());
                return 0;
            case "list-wintun":
                await ListWintunAdaptersAsync();
                return 0;
            case "repair-wintun":
                await RepairWintunAdaptersAsync();
                return 0;
            case "install-sing-box":
                {
                    var progress = new Progress<string>(message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"));
                    var result = await installer.InstallSingBoxAsync(progress);
                    Console.WriteLine($"Installed tools to: {result.ToolsDirectory}");
                    Console.WriteLine($"sing-box: {result.SingBoxPath}");
                    return 0;
                }
            case "install":
                {
                    if (args.Contains("--include-openssh", StringComparer.OrdinalIgnoreCase))
                    {
                        var status = installer.Check();
                        if (!status.HasSsh)
                        {
                            Console.WriteLine("Starting elevated OpenSSH Client installer.");
                            if (!installer.StartOpenSshClientInstall())
                            {
                                throw new InvalidOperationException("Could not start PowerShell for OpenSSH Client installation.");
                            }
                        }
                    }

                    var progress = new Progress<string>(message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"));
                    var result = await installer.InstallAllAsync(progress);
                    Console.WriteLine($"Installed tools to: {result.ToolsDirectory}");
                    Console.WriteLine($"tun2socks: {result.Tun2SocksPath}");
                    Console.WriteLine($"wintun.dll: {result.WintunPath}");
                    Console.WriteLine($"sing-box: {result.SingBoxPath}");
                    return 0;
                }
            default:
                throw new ArgumentException($"Unknown deps command: {command}");
        }
    }

    private static async Task ListWintunAdaptersAsync()
    {
        var repair = new WintunRepairService();
        var adapters = await repair.FindTun2SocksAdaptersAsync();
        if (adapters.Count == 0)
        {
            Console.WriteLine("No tun2socks Wintun adapters found.");
            return;
        }

        foreach (var adapter in adapters)
        {
            Console.WriteLine($"{adapter.Description} [{adapter.Status}]: {adapter.InstanceId}");
        }
    }

    private static async Task RepairWintunAdaptersAsync()
    {
        var repair = new WintunRepairService();
        var progress = new Progress<string>(Console.WriteLine);
        await repair.RemoveTun2SocksAdaptersAsync(progress);
    }

    private static int RunPlan()
    {
        Console.WriteLine("Practical implementation layers:");
        Console.WriteLine("1. SSH layer: run OpenSSH dynamic forwarding, equivalent to ssh -N -D 127.0.0.1:8000 user@host.");
        Console.WriteLine("2. Routing layer: add TUN adapter plus tun2socks to turn the local SOCKS endpoint into a VPN-like route.");
        Console.WriteLine("3. Control layer: GUI/service manages credentials, processes, routes, DNS, and rollback.");
        Console.WriteLine();
        Console.WriteLine("Current app implements SSH, tun2socks, sing-box, a WinUI 3 desktop UI, and this CLI.");
        return 0;
    }

    private static TunnelSession CreateConsoleSession()
    {
        var session = new TunnelSession();
        session.Log += Console.WriteLine;
        return session;
    }

    private static void PrintDependencyStatus(DependencyStatus status)
    {
        Console.WriteLine($"Tools directory: {status.ToolsDirectory}");
        Console.WriteLine($"ssh: {(status.HasSsh ? status.SshPath : "missing")}");
        Console.WriteLine($"tun2socks: {(status.HasTun2Socks ? status.Tun2SocksPath : "missing")}");
        Console.WriteLine($"wintun.dll: {(status.HasWintun ? status.WintunPath : "missing")}");
        Console.WriteLine($"sing-box: {(status.HasSingBox ? status.SingBoxPath : "missing")}");
    }

    private static async Task<int> RunWithCtrlCAsync(TunnelSession session, Func<CancellationToken, Task<int>> run)
    {
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
            session.Stop();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await run(shutdown.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static ConnectConfig ParseConnect(string[] args)
    {
        var parser = new ArgumentReader(args);
        string? user = null;
        string? host = null;
        var localPort = 8000;
        var bindAddress = IPAddress.Loopback;
        var sshPort = 22;
        string? identityFile = null;
        string? sshPath = null;
        string? password = null;
        var sshOptions = new List<string>();

        while (parser.TryRead(out var option, out var value))
        {
            switch (option)
            {
                case "--user":
                    user = RequireNonEmpty(value, option);
                    break;
                case "--host":
                    host = RequireNonEmpty(value, option);
                    break;
                case "--local-port":
                    localPort = ParsePort(value, option);
                    break;
                case "--bind":
                    bindAddress = ParseAddress(value, option);
                    break;
                case "--ssh-port":
                    sshPort = ParsePort(value, option);
                    break;
                case "--identity":
                    identityFile = RequireExistingFile(value, option);
                    break;
                case "--ssh-path":
                    sshPath = RequireExistingFile(value, option);
                    break;
                case "--password":
                    password = value;
                    break;
                case "--ssh-option":
                    sshOptions.Add(RequireNonEmpty(value, option));
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {option}");
            }
        }

        return new ConnectConfig(
            RequireNonEmpty(user, "--user"),
            RequireNonEmpty(host, "--host"),
            localPort,
            bindAddress,
            sshPort,
            identityFile,
            sshPath,
            password,
            sshOptions);
    }

    private static RouteConfig ParseRoute(string[] args)
    {
        var parser = new ArgumentReader(args);
        string? user = null;
        string? host = null;
        var localPort = 8000;
        var bindAddress = IPAddress.Loopback;
        var sshPort = 22;
        string? identityFile = null;
        string? sshPath = null;
        string? password = null;
        var sshOptions = new List<string>();
        string? tun2SocksPath = null;
        string? primaryInterface = null;
        var tunName = "wintun";
        var tunAddress = IPAddress.Parse("192.168.123.1");
        var tunMask = IPAddress.Parse("255.255.255.0");
        var dnsServer = IPAddress.Parse("1.1.1.1");
        var applyNetwork = false;
        IPAddress? primaryGateway = null;
        IPAddress? sshServerAddress = null;

        while (parser.TryRead(out var option, out var value))
        {
            switch (option)
            {
                case "--apply-network":
                    applyNetwork = true;
                    break;
                case "--user":
                    user = RequireNonEmpty(value, option);
                    break;
                case "--host":
                    host = RequireNonEmpty(value, option);
                    break;
                case "--local-port":
                    localPort = ParsePort(value, option);
                    break;
                case "--bind":
                    bindAddress = ParseAddress(value, option);
                    break;
                case "--ssh-port":
                    sshPort = ParsePort(value, option);
                    break;
                case "--identity":
                    identityFile = RequireExistingFile(value, option);
                    break;
                case "--ssh-path":
                    sshPath = RequireExistingFile(value, option);
                    break;
                case "--password":
                    password = value;
                    break;
                case "--ssh-option":
                    sshOptions.Add(RequireNonEmpty(value, option));
                    break;
                case "--tun2socks-path":
                    tun2SocksPath = RequireExistingFile(value, option);
                    break;
                case "--primary-interface":
                    primaryInterface = RequireNonEmpty(value, option);
                    break;
                case "--tun-name":
                    tunName = RequireNonEmpty(value, option);
                    break;
                case "--tun-address":
                    tunAddress = ParseAddress(value, option);
                    break;
                case "--tun-mask":
                    tunMask = ParseAddress(value, option);
                    break;
                case "--dns":
                    dnsServer = ParseAddress(value, option);
                    break;
                case "--primary-gateway":
                    primaryGateway = ParseAddress(value, option);
                    break;
                case "--ssh-server-address":
                    sshServerAddress = ParseAddress(value, option);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {option}");
            }
        }

        host = RequireNonEmpty(host, "--host");
        if (sshServerAddress is null && IPAddress.TryParse(host, out var parsedHostAddress))
        {
            sshServerAddress = parsedHostAddress;
        }

        var connect = new ConnectConfig(
            RequireNonEmpty(user, "--user"),
            host,
            localPort,
            bindAddress,
            sshPort,
            identityFile,
            sshPath,
            password,
            sshOptions);

        return new RouteConfig(
            connect,
            tun2SocksPath,
            primaryInterface?.Trim() ?? string.Empty,
            tunName,
            tunAddress,
            tunMask,
            dnsServer,
            applyNetwork,
            primaryGateway,
            sshServerAddress);
    }

    private static SingBoxConfig ParseSingBox(string[] args)
    {
        var parser = new ArgumentReader(args);
        string? singBoxPath = null;
        string? configPath = null;

        while (parser.TryRead(out var option, out var value))
        {
            switch (option)
            {
                case "--sing-box-path":
                    singBoxPath = RequireExistingFile(value, option);
                    break;
                case "--config":
                    configPath = RequireExistingFile(value, option);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {option}");
            }
        }

        return new SingBoxConfig(singBoxPath, RequireExistingFile(configPath, "--config"));
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private static string RequireNonEmpty(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing or empty value for {optionName}.");
        }

        return value;
    }

    private static string RequireExistingFile(string? value, string optionName)
    {
        var path = RequireNonEmpty(value, optionName);
        if (!File.Exists(path))
        {
            throw new ArgumentException($"File does not exist for {optionName}: {path}");
        }

        return path;
    }

    private static int ParsePort(string? value, string optionName)
    {
        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException($"{optionName} must be a TCP port between 1 and 65535.");
        }

        return port;
    }

    private static IPAddress ParseAddress(string? value, string optionName)
    {
        if (!IPAddress.TryParse(RequireNonEmpty(value, optionName), out var address))
        {
            throw new ArgumentException($"{optionName} must be a valid IP address.");
        }

        return address;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        Console.Error.WriteLine();
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        ssh-env

        Usage:
          ssh-env check
          ssh-env deps check
          ssh-env deps install [--include-openssh]
          ssh-env deps install-sing-box
          ssh-env deps list-wintun
          ssh-env deps repair-wintun
          ssh-env import-vless --link URL [--out-dir PATH]
          ssh-env plan
          ssh-env connect --user USER --host HOST [options]
          ssh-env route --user USER --host HOST [options]
          ssh-env sing-box --config PATH [--sing-box-path PATH]

        Options:
          --local-port PORT        Local SOCKS5 port. Default: 8000.
          --bind ADDRESS           Local bind address. Default: 127.0.0.1.
          --ssh-port PORT          SSH server port. Default: 22.
          --identity PATH          SSH private key path.
          --ssh-path PATH          Explicit ssh.exe path.
          --password VALUE         SSH password for this run only. Not saved by the desktop app.
          --ssh-option VALUE       Extra OpenSSH -o option. Can be repeated.
          --tun2socks-path PATH    Explicit tun2socks.exe path.
          --primary-interface NAME Physical interface used when applying Windows routes.
          --tun-name NAME          Wintun interface name. Default: wintun.
          --tun-address ADDRESS    TUN IPv4 address. Default: 192.168.123.1.
          --tun-mask MASK          TUN IPv4 mask. Default: 255.255.255.0.
          --dns ADDRESS            DNS server for TUN. Default: 1.1.1.1.
          --apply-network          Apply netsh IP/DNS/routes and rollback on stop.
          --primary-gateway IP     Required with --apply-network.
          --ssh-server-address IP  Required with --apply-network if HOST is not an IP.
          --config PATH            sing-box JSON config path.
          --sing-box-path PATH     Explicit sing-box.exe path.
          --link URL               VLESS share link or HTTP(S) subscription URL.
          --out-dir PATH           Directory for imported sing-box configs.
          --system-proxy-port PORT Local mixed proxy port. Default: 2080.

        Example:
          ssh-env connect --user alice --host 203.0.113.10 --local-port 8000
        """);
    }

    private sealed class ArgumentReader(string[] args)
    {
        private int index;

        public bool TryRead(out string option, out string? value)
        {
            option = string.Empty;
            value = null;

            if (index >= args.Length)
            {
                return false;
            }

            option = args[index++];
            if (option is "-h" or "--help")
            {
                throw new ArgumentException("Use `ssh-env help` to show usage.");
            }

            if (option is "--apply-network")
            {
                return true;
            }

            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected positional argument: {option}");
            }

            if (index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Missing value for {option}.");
            }

            value = args[index++];
            return true;
        }
    }
}
