using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SshEnv.Core;

namespace SshEnv.WinUI;

public sealed partial class MainWindow
{
    private CancellationTokenSource? pingCancellation;
    private Task? pingTask;
    private IReadOnlyList<ProfileItem>? pingQueue;
    private CancellationTokenSource? transitionCancellation;
    private Task? transitionTask;
    private bool transitioning => transitionCancellation is not null;

    private Task ChangeConnectionAsync(bool connect)
    {
        var target = connect ? profileRows.FirstOrDefault(item => item.IsActive) : null;
        if (connect && target is null)
        {
            UpdateConnectionControls();
            return Task.CompletedTask;
        }

        return TransitionConnectionAsync(target);
    }

    private bool CanConnectProfile(ProfileItem item) =>
        !transitioning && !busy && !closing && activeDialog is null &&
        profileRows.Contains(item) && (connectionTask is null || !item.IsActive);

    private async Task TransitionConnectionAsync(ProfileItem? target)
    {
        if (transitioning || busy || closing || activeDialog is not null ||
            (target is not null && !CanConnectProfile(target)))
        {
            UpdateConnectionControls();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        transitionCancellation = cancellation;
        UpdateConnectionControls();
        try
        {
            transitionTask = SwitchConnectionAsync(target, cancellation.Token);
            await transitionTask;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            transitionCancellation = null;
            transitionTask = null;
            if (!closing)
            {
                UpdateConnectionControls();
            }
        }
    }

    private async Task SwitchConnectionAsync(ProfileItem? target, CancellationToken cancellation)
    {
        Func<CancellationToken, Task<int>>? run = null;
        var mode = profiles.Mode;
        if (target is not null)
        {
            var ssh = target.IsVless ? null : profiles.Ssh.Find(item => SameName(item.Name, target.Name));
            var vless = target.IsVless ? profiles.Vless.Find(item => SameName(item.Name, target.Name)) : null;
            mode = vless is null ? sshMode : AvailableVlessMode(vless, vlessMode);
            if (target.IsActive)
            {
                mode = profiles.Mode;
            }

            run = await PrepareConnectionAsync(mode, ssh, vless, profiles.DirectRules.ToArray(),
                profiles.TlsFragmentation, cancellation);
            cancellation.ThrowIfCancellationRequested();
        }

        connectionCancellation?.Cancel();
        await WaitForWorkAsync(connectionTask);
        cancellation.ThrowIfCancellationRequested();
        if (target is null || run is null)
        {
            return;
        }

        SelectProfile(target.IsVless, target.Name);
        profiles.Mode = mode;
        Save();
        if (!pinging)
        {
            Notice.IsOpen = false;
        }

        var lifetime = new CancellationTokenSource();
        connectionCancellation = lifetime;
        connectionTask = Task.Run(() => run(lifetime.Token), lifetime.Token);
        _ = ObserveConnectionAsync(connectionTask, lifetime);
        Refresh();
    }

    private async Task ObserveConnectionAsync(Task<int> running, CancellationTokenSource lifetime)
    {
        try
        {
            var exitCode = await running;
            if (!lifetime.IsCancellationRequested)
            {
                LogConnectionFailure(string.Format(L("TunnelEnded"), exitCode));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            if (ReferenceEquals(connectionTask, running))
            {
                connectionTask = null;
                connectionCancellation = null;
                if (!closing)
                {
                    UpdateConnectionControls();
                }
            }

            lifetime.Dispose();
        }
    }

    private Task<Func<CancellationToken, Task<int>>> PrepareConnectionAsync(
        ConnectionMode mode,
        SshProfile? ssh,
        VlessProfile? vless,
        IReadOnlyList<SingBoxDirectRule> rules,
        SingBoxTlsFragmentOptions? fragmentation,
        CancellationToken cancellation)
    {
        return Task.Run<Func<CancellationToken, Task<int>>>(async () =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (mode is ConnectionMode.Socks or ConnectionMode.Route)
            {
                var profile = ssh ?? throw new InvalidOperationException(L("MissingSshProfile"));
                var credentials = ProfileStore.UnprotectPassword(profile.EncryptedPassword);
                var sshExecutable = ExistingFile(profile.SshPath) ??
                    (string.IsNullOrWhiteSpace(profile.SshPath) ? TunnelSession.ResolveExecutable("ssh") : null)
                    ?? throw new InvalidOperationException(L("SshUnavailable"));
                var config = new ConnectConfig(profile.User, profile.Host, profile.LocalPort, IPAddress.Loopback,
                    profile.SshPort, profile.IdentityPath, sshExecutable,
                    string.IsNullOrEmpty(credentials) ? null : credentials, []);
                if (mode == ConnectionMode.Socks)
                {
                    return token => session.RunSocksAsync(config, token);
                }

                if (!Administrator)
                {
                    throw new InvalidOperationException(L("ElevationRequired"));
                }

                var route = new RouteConfig(config,
                    ExistingFile(profile.Tun2SocksPath) ?? dependencies.Check().Tun2SocksPath
                        ?? throw new InvalidOperationException(L("Tun2SocksUnavailable")),
                    string.IsNullOrWhiteSpace(profile.PrimaryInterface) ? FindPrimaryInterface() ?? string.Empty : profile.PrimaryInterface,
                    profile.TunName,
                    IPAddress.Parse(profile.TunAddress),
                    IPAddress.Parse("255.255.255.0"),
                    IPAddress.Parse(profile.DnsServer),
                    true,
                    profile.PrimaryGateway is { Length: > 0 } gateway ? IPAddress.Parse(gateway) : null,
                    await ResolveIpv4Async(profile.Host, cancellation));
                return token => session.RunRouteAsync(route, token);
            }

            var singBox = vless ?? throw new InvalidOperationException(L("MissingVlessProfile"));
            var executable = ExistingFile(singBox.SingBoxPath) ?? dependencies.Check().SingBoxPath
                ?? throw new InvalidOperationException(L("SingBoxUnavailable"));
            if (mode == ConnectionMode.SingBoxTun)
            {
                var config = ExistingFile(singBox.TunConfigPath) ?? throw new InvalidOperationException(L("MissingTunConfig"));
                return token => session.RunSingBoxAsync(new SingBoxConfig(executable, config, rules, fragmentation), token);
            }

            var proxyConfig = ExistingFile(singBox.SystemProxyConfigPath) ?? throw new InvalidOperationException(L("MissingProxyConfig"));
            return token => session.RunSingBoxSystemProxyAsync(
                new SingBoxSystemProxyConfig(executable, proxyConfig, singBox.SystemProxyPort, fragmentation), token);
        }, cancellation);
    }

    private void LogConnectionFailure(string message)
    {
        logs.Enqueue(message);
        Notify(message, InfoBarSeverity.Error);
    }

    private Task PingAsync() =>
        profileRows.FirstOrDefault(item => item.IsActive) is { } profile
            ? MeasureProfilesAsync([profile])
            : Task.CompletedTask;

    private Task PingSelectedAsync()
    {
        return ProfileList.SelectedItem is ProfileItem profile
            ? MeasureProfilesAsync([profile])
            : Task.CompletedTask;
    }

    private async Task MeasureProfilesAsync(IReadOnlyList<ProfileItem> queue)
    {
        if (queue.Count == 0 || pinging || busy || closing)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        pingCancellation = cancellation;
        pinging = true;
        pingQueue = queue;
        UpdatePingResult();
        UpdateConnectionControls();
        var multiple = queue.Count > 1;
        try
        {
            if (multiple)
            {
                var cancel = new Button { Content = L("Cancel") };
                cancel.Click += (_, _) => cancellation.Cancel();
                Notice.ActionButton = cancel;
            }

            pingTask = MeasureQueueAsync(queue, cancellation.Token);
            await pingTask;
            if (multiple)
            {
                logs.Enqueue(L("PingAllComplete"));
                Notify(L("PingAllComplete"));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Notify(L("OperationCanceled"));
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            pingTask = null;
            pingCancellation = null;
            pingQueue = null;
            pinging = false;
            if (multiple)
            {
                Notice.ActionButton = null;
            }

            if (!closing)
            {
                UpdatePingResult();
                UpdateConnectionControls();
            }
        }
    }

    private async Task MeasureQueueAsync(IReadOnlyList<ProfileItem> queue, CancellationToken cancellation)
    {
        var bundledExecutable = new Lazy<string?>(() => dependencies.Check().SingBoxPath);
        for (var index = 0; index < queue.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (queue.Count > 1)
            {
                Notify(string.Format(L("LatencyProgress"), index + 1, queue.Count));
            }

            var profile = queue[index];
            var source = FindPingProfile(profile);
            if (source is null)
            {
                continue;
            }

            var results = PingResults(profile.IsVless);
            try
            {
                var hintMode = vlessMode;
                var elapsed = await Task.Run(() => source is VlessProfile vless
                    ? MeasureVlessLatencyAsync(vless, hintMode, bundledExecutable, cancellation)
                    : SshLatencyTester.MeasureAsync(((SshProfile)source).Host, ((SshProfile)source).SshPort, cancellation),
                    cancellation);
                var milliseconds = Math.Max(1, (int)Math.Round(elapsed.TotalMilliseconds));
                if (ReferenceEquals(source, FindPingProfile(profile)))
                {
                    results[profile.Name] = milliseconds;
                }

                logs.Enqueue($"{profile.Name}: {UiText.Format("PingResult", milliseconds)}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellation.IsCancellationRequested)
            {
                var message = $"{profile.Name}: {exception.Message}";
                logs.Enqueue(message);
                if (ReferenceEquals(source, FindPingProfile(profile)))
                {
                    results[profile.Name] = null;
                    if (queue.Count == 1)
                    {
                        Notify(message, InfoBarSeverity.Error);
                    }
                }
            }

            if (ReferenceEquals(source, FindPingProfile(profile)))
            {
                var (text, brush) = GetPingResult(profile.IsVless, profile.Name);
                profileRows.FirstOrDefault(row => row.IsVless == profile.IsVless && SameName(row.Name, profile.Name))
                    ?.UpdateLatency(text, brush);
            }
        }
    }

    private object? FindPingProfile(ProfileItem item) => item.IsVless
        ? profiles.Vless.Find(profile => SameName(profile.Name, item.Name))
        : profiles.Ssh.Find(profile => SameName(profile.Name, item.Name));

    private Task<TimeSpan> MeasureVlessLatencyAsync(
        VlessProfile profile, ConnectionMode preferredMode, Lazy<string?> bundledExecutable, CancellationToken cancellation)
    {
        var mode = AvailableVlessMode(profile, preferredMode);
        var config = ExistingFile(mode == ConnectionMode.SystemProxy ? profile.SystemProxyConfigPath : profile.TunConfigPath)
            ?? throw new InvalidOperationException(L(mode == ConnectionMode.SingBoxTun ? "MissingTunConfig" : "MissingProxyConfig"));
        var executable = ExistingFile(profile.SingBoxPath) ?? bundledExecutable.Value
            ?? throw new InvalidOperationException(L("SingBoxUnavailable"));
        return SingBoxLatencyTester.MeasureAsync(executable, config, cancellation);
    }

    private Task InstallDependenciesAsync()
    {
        return RunOperationAsync(async cancellation =>
        {
            Notify(L("InstallingDependencies"));
            var cancel = new Button { Content = L("Cancel") };
            cancel.Click += (_, _) => operationCancellation?.Cancel();
            Notice.ActionButton = cancel;
            try
            {
                await dependencies.InstallAllAsync(new Progress<string>(logs.Enqueue), cancellation);
                Notify(L("DependenciesReady"), InfoBarSeverity.Success);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Notify(L("OperationCanceled"));
                throw;
            }
            finally
            {
                Notice.ActionButton = null;
            }
        });
    }

    private async Task RemoveDependenciesAsync()
    {
        var content = new TextBlock { Text = L("RemoveDependenciesQuestion"), TextWrapping = TextWrapping.Wrap };
        var dialog = CreateDialog(L("RemoveDependencies"), content, L("RemoveDependencies"));
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        if (connectionTask is not null || pinging)
        {
            Notify(L("DependencyRemovalUnavailable"));
            return;
        }

        await RunOperationAsync(async cancellation =>
        {
            try
            {
                await Task.Run(() => dependencies.RemoveAllAsync(cancellation), cancellation);
                var message = L("DependenciesRemoved");
                logs.Enqueue(message);
                Notify(message, InfoBarSeverity.Success);
            }
            catch (AggregateException exception)
            {
                var message = string.Join(Environment.NewLine, exception.InnerExceptions.Select(error => error.Message));
                logs.Enqueue(message);
                Notify(message, InfoBarSeverity.Error);
            }
        });
    }

    private async Task RepairWintunAsync()
    {
        var content = new TextBlock { Text = L("RepairQuestion"), TextWrapping = TextWrapping.Wrap };
        if (await ShowDialogAsync(CreateDialog(L("RepairWintun"), content, L("RepairWintun"))) != ContentDialogResult.Primary)
        {
            return;
        }

        await RunOperationAsync(async cancellation =>
        {
            var repair = new WintunRepairService();
            var progress = new Progress<string>(message =>
            {
                logs.Enqueue(message);
                Notify(message);
            });
            await repair.RemoveTun2SocksAdaptersAsync(progress, cancellation);
        });
    }

    private static string? ExistingFile(string? path) => File.Exists(path) ? path : null;

    private static async Task<IPAddress> ResolveIpv4Async(string host, CancellationToken cancellation)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellation);
        return addresses.FirstOrDefault() ?? throw new InvalidOperationException(string.Format(L("UnresolvedIpv4"), host));
    }

    private static string? FindPrimaryInterface()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
            .OrderBy(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
            .FirstOrDefault()?.Name;
    }
}
