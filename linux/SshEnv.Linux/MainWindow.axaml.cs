using Avalonia.Controls;
using Avalonia.Threading;
using SshEnv.Core;

namespace SshEnv.Linux;

public sealed partial class MainWindow : Window
{
    private const int VlessProxyPort = 2080;
    private readonly string configsDirectory = GetConfigsDirectory();
    private CancellationTokenSource? connectionCancellation;
    private Task? connectionTask;
    private bool closing;

    public MainWindow()
    {
        InitializeComponent();
        ConnectionButton.Click += (_, _) => ToggleConnection();
        ImportButton.Click += async (_, _) => await ImportAsync();
        Closing += StopBeforeClose;
        try
        {
            LoadProfiles();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ImportStatus.Text = $"Could not load profiles: {exception.Message}";
        }
    }

    private void ToggleConnection()
    {
        if (connectionCancellation is not null)
        {
            RequestStop();
            return;
        }

        try
        {
            var request = ConnectionTabs.SelectedIndex == 1 ? BuildVlessRequest() : BuildSshRequest();
            connectionTask = RunConnectionAsync(request);
        }
        catch (ArgumentException exception)
        {
            Status.Text = exception.Message;
        }
    }

    private ProxyRequest BuildSshRequest()
    {
        var host = SshHost.Text?.Trim();
        var user = SshUser.Text?.Trim();
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user))
        {
            throw new ArgumentException("Enter the SSH hostname and username.");
        }

        if (host.Contains('@') || user.Contains('@') || host.Any(char.IsWhiteSpace) || user.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Enter the SSH username and host in separate fields.");
        }

        var sshPort = ParsePort(SshPort.Text, "SSH port");
        var localPort = ParsePort(SocksPort.Text, "Local SOCKS5 port");
        var arguments = new List<string>
        {
            "-N", "-T", "-D", $"127.0.0.1:{localPort}", "-p", sshPort.ToString(),
            "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes",
            "-o", "StrictHostKeyChecking=accept-new", "-o", "ServerAliveInterval=30",
            "-o", "ServerAliveCountMax=3", "-o", "ForkAfterAuthentication=no",
            "-o", "ControlMaster=no", "-o", "ControlPath=none"
        };
        if (!string.IsNullOrWhiteSpace(IdentityFile.Text))
        {
            var path = IdentityFile.Text.Trim();
            if (path.StartsWith("~/", StringComparison.Ordinal))
            {
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
            }

            if (!File.Exists(path))
            {
                throw new ArgumentException("The SSH private key file does not exist.");
            }

            arguments.AddRange(["-i", path]);
        }

        arguments.AddRange(["--", $"{user}@{host}"]);
        return new ProxyRequest("ssh", arguments, localPort);
    }

    private ProxyRequest BuildVlessRequest()
    {
        if (Profiles.SelectedItem is not Profile profile || !File.Exists(profile.ConfigPath))
        {
            throw new ArgumentException("Import and select a VLESS profile.");
        }

        return new ProxyRequest("sing-box", ["run", "-c", profile.ConfigPath], VlessProxyPort);
    }

    private async Task RunConnectionAsync(ProxyRequest request)
    {
        using var cancellation = new CancellationTokenSource();
        connectionCancellation = cancellation;
        ConnectionButton.Content = "Disconnect";
        Status.Text = $"Starting {request.Executable}…";
        try
        {
            var exitCode = await ProxySession.RunAsync(
                request.Executable, request.Arguments, request.Port,
                message => Dispatcher.UIThread.Post(() => AppendLog(message)),
                () => Status.Text = $"Proxy listening on 127.0.0.1:{request.Port}.", cancellation.Token);
            Status.Text = exitCode == 0 ? "Disconnected" : $"{request.Executable} exited with code {exitCode}.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status.Text = "Disconnected";
        }
        catch (Exception exception)
        {
            Status.Text = exception.Message;
        }
        finally
        {
            connectionCancellation = null;
            ConnectionButton.Content = "Connect";
            ConnectionButton.IsEnabled = !closing;
        }
    }

    private async Task ImportAsync()
    {
        var link = VlessLink.Text;
        if (string.IsNullOrWhiteSpace(link))
        {
            ImportStatus.Text = "Enter a VLESS key or subscription URL.";
            return;
        }

        ImportButton.IsEnabled = false;
        ImportStatus.Text = "Importing VLESS profiles…";
        try
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException();
            }

            const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(configsDirectory, privateDirectory);
            if ((File.GetUnixFileMode(configsDirectory) & ~privateDirectory) != 0)
            {
                throw new InvalidOperationException("Restrict the profile directory permissions to 0700 before importing credentials.");
            }

            var result = await SingBoxProfileImporter.ImportLinkAsync(link, configsDirectory, VlessProxyPort);
            LoadProfiles(result.Profiles[0].SystemProxyConfigPath);
            VlessLink.Clear();
            ImportStatus.Text = $"Imported {result.Profiles.Count} profile(s). Skipped {result.SkippedEntries} entries.";
        }
        catch (Exception exception)
        {
            ImportStatus.Text = $"Import failed: {exception.Message}";
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private void LoadProfiles(string? selectedPath = null)
    {
        const string suffix = "-system-proxy";
        var profiles = Directory.Exists(configsDirectory)
            ? Directory.EnumerateFiles(configsDirectory, $"*{suffix}.json")
                .Select(path => new Profile(Path.GetFileNameWithoutExtension(path)[..^suffix.Length], path))
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        Profiles.ItemsSource = profiles;
        Profiles.SelectedItem = profiles.FirstOrDefault(profile => profile.ConfigPath == selectedPath) ?? profiles.FirstOrDefault();
    }

    private void AppendLog(string message)
    {
        const int maxLength = 20_000;
        var text = Log.Text + message + Environment.NewLine;
        Log.Text = text.Length > maxLength ? text[^maxLength..] : text;
        Log.CaretIndex = Log.Text.Length;
    }

    private void RequestStop()
    {
        ConnectionButton.IsEnabled = false;
        Status.Text = "Stopping…";
        connectionCancellation?.Cancel();
    }

    private async void StopBeforeClose(object? sender, WindowClosingEventArgs args)
    {
        if (connectionTask is not { IsCompleted: false } task)
        {
            return;
        }

        args.Cancel = true;
        if (closing)
        {
            return;
        }

        closing = true;
        RequestStop();
        await task;
        Dispatcher.UIThread.Post(Close);
    }

    private static int ParsePort(string? value, string fieldName)
    {
        return int.TryParse(value, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new ArgumentException($"{fieldName} must be between 1 and 65535.");
    }

    private static string GetConfigsDirectory()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome) || !Path.IsPathFullyQualified(dataHome))
        {
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(dataHome, "ssh-env-linux", "configs");
    }

    private sealed record ProxyRequest(string Executable, IReadOnlyList<string> Arguments, int Port);

    private sealed record Profile(string Name, string ConfigPath)
    {
        public override string ToString() => Name;
    }
}
