using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using SshEnv.Core;
using Windows.Graphics;

namespace SshEnv.WinUI;

public sealed partial class MainWindow : Window
{
    private const int MinimumWindowWidth = 540;
    private const int MinimumWindowHeight = 480;

    private readonly ProfileStore store = new();
    private readonly DesktopProfiles profiles;
    private readonly DependencyInstaller dependencies = new();
    private readonly TunnelSession session = new();
    private readonly BoundedLogBuffer logs = new();
    private readonly DispatcherQueueTimer logTimer;
    private string? pendingLogRender;
    private readonly Dictionary<string, int?> sshLatency = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?> vlessLatency = new(StringComparer.OrdinalIgnoreCase);
    private readonly SolidColorBrush successfulPingBrush = new(Windows.UI.Color.FromArgb(255, 0, 255, 0));
    private readonly SolidColorBrush failedPingBrush = new(Windows.UI.Color.FromArgb(255, 255, 0, 0));
    private readonly Dictionary<(VlessProfile Profile, ConnectionMode Mode), string> protocolLabels = [];
    private readonly TrayIcon tray;
    private readonly nint windowHandle;

    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? connectionCancellation;
    private Task<int>? connectionTask;
    private Task? operationTask;
    private ContentDialog? activeDialog;
    private ConnectionMode sshMode = ConnectionMode.Socks;
    private ConnectionMode vlessMode = ConnectionMode.SingBoxTun;
    private bool updatingControls;
    private bool closing;
    private bool allowClose;
    private bool busy;
    private bool pinging;
    private bool loadingProtocolLabels;
    private int protocolLabelsVersion;
    private ProfileItem[] profileRows = [];

    public MainWindow()
    {
        InitializeComponent();
        ElevationNotice.IsOpen = !Administrator;
        try
        {
            profiles = store.Load();
        }
        catch (Exception exception)
        {
            profiles = new DesktopProfiles();
            logs.Enqueue(exception.Message);
        }

        sshMode = profiles.LastSshMode is ConnectionMode.Socks or ConnectionMode.Route
            ? profiles.LastSshMode.Value : IsVless ? ConnectionMode.Socks : profiles.Mode;
        vlessMode = profiles.LastVlessMode is ConnectionMode.SingBoxTun or ConnectionMode.SystemProxy
            ? profiles.LastVlessMode.Value : IsVless ? profiles.Mode : ConnectionMode.SingBoxTun;
        EnsureActiveProfile();
        windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        UpdateMinimumWindowSize();
        Root.Loaded += AttachWindowScaleHandler;
        AppWindow.Resize(new SizeInt32(680, 600));
        AppWindow.Closing += (sender, args) =>
        {
            if (!allowClose)
            {
                args.Cancel = true;
                DispatcherQueue.TryEnqueue(() => _ = RequestCloseAsync());
            }
        };
        tray = new TrayIcon(
            windowHandle,
            RestoreWindow,
            () => _ = ChangeConnectionAsync(connectionTask is null),
            () => _ = ExitAsync());

        session.Log += logs.Enqueue;
        logTimer = DispatcherQueue.CreateTimer();
        logTimer.Interval = TimeSpan.FromMilliseconds(150);
        logTimer.Tick += (_, _) => FlushLogs();
        logTimer.Start();
        ClearLogsButton.Click += (_, _) =>
        {
            logs.Clear();
            pendingLogRender = string.Empty;
            FlushLogs();
        };
        ToggleLogsButton.Click += (_, _) =>
        {
            logs.IsEnabled = !logs.IsEnabled;
            UpdateLogToggleLabel();
            FlushLogs();
        };

        Navigation.SelectionChanged += (_, _) => UpdatePage();
        ConnectionSwitch.Toggled += (_, _) =>
        {
            if (!updatingControls)
            {
                _ = ChangeConnectionAsync(ConnectionSwitch.IsOn);
            }
        };
        ModeCombo.SelectionChanged += (_, _) =>
        {
            if (ModeCombo.SelectedItem is ComboBoxItem { Tag: ConnectionMode mode })
            {
                ChangeMode(mode);
            }
        };
        PingButton.Click += async (_, _) => await PingAsync();
        ProfilePingButton.Click += async (_, _) => await PingSelectedAsync();
        PingAllButton.Click += async (_, _) => await MeasureProfilesAsync(profileRows);
        ProfileList.SelectionChanged += (_, _) => UpdateProfileActions();
        ProfileList.Loaded += (_, _) => ScrollToSelectedProfile();
        ProfileList.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is ProfileItem item)
            {
                DispatcherQueue.TryEnqueue(() => ActivateProfile(item));
            }
        };
        EditProfileButton.Click += async (_, _) => await RunActionAsync(() => EditProfileAsync(ProfileList.SelectedItem as ProfileItem));
        DeleteProfileButton.Click += async (_, _) => await RunActionAsync(() => DeleteProfileAsync(ProfileList.SelectedItem as ProfileItem));
        ThemeCombo.SelectionChanged += (_, _) => UpdatePreference(ThemeCombo, value =>
        {
            profiles.Theme = value;
            ApplyTheme();
        });
        LanguageCombo.SelectionChanged += (_, _) => UpdatePreference(LanguageCombo, value =>
        {
            profiles.Language = value;
            ApplyLanguage();
            Refresh();
        });
        CloseBehaviorCombo.SelectionChanged += (_, _) => UpdatePreference(CloseBehaviorCombo, value => profiles.CloseBehavior = value);
        ExclusionSettingsButton.Click += async (_, _) => await RunActionAsync(EditExclusionsAsync);
        FragmentationSettingsButton.Click += async (_, _) => await RunActionAsync(EditFragmentationAsync);
        CheckSshButton.Click += async (_, _) => await RunOperationAsync(async cancellation =>
        {
            var version = await TunnelSession.GetSshVersionAsync(cancellation);
            logs.Enqueue(version);
            Notify(version);
        });
        InstallDependenciesButton.Click += async (_, _) => await RunActionAsync(InstallDependenciesAsync);
        RemoveDependenciesButton.Click += async (_, _) => await RunActionAsync(RemoveDependenciesAsync);
        RepairWintunButton.Click += async (_, _) => await RunActionAsync(RepairWintunAsync);
        ElevationRestartButton.Click += async (_, _) => await RunActionAsync(RestartAsAdminAsync);
        RestartAsAdminButton.Click += async (_, _) => await RunActionAsync(RestartAsAdminAsync);

        CheckUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync();
        ApplyTheme();
        ApplyLanguage();
        Refresh();
        Navigation.SelectedItem = HomeNav;
        UpdatePage();
    }

    private bool IsVless => profiles.Mode is ConnectionMode.SingBoxTun or ConnectionMode.SystemProxy;

    private SshProfile? ActiveSsh => profiles.Ssh.Find(profile => SameName(profile.Name, profiles.SelectedSsh));
    private VlessProfile? ActiveVless => profiles.Vless.Find(profile => SameName(profile.Name, profiles.SelectedVless));
    private string? ActiveName => IsVless ? ActiveVless?.Name : ActiveSsh?.Name;
    private bool Administrator => TunnelSession.IsWindowsAdministrator();

    private void AttachWindowScaleHandler(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= AttachWindowScaleHandler;
        if (Root.XamlRoot is { } xamlRoot)
        {
            xamlRoot.Changed += (_, _) => UpdateMinimumWindowSize();
            UpdateMinimumWindowSize();
        }
    }

    private void UpdateMinimumWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        var scale = Root.XamlRoot?.RasterizationScale ?? 1;
        var width = (int)Math.Ceiling(MinimumWindowWidth * scale);
        var height = (int)Math.Ceiling(MinimumWindowHeight * scale);
        if (presenter.PreferredMinimumWidth != width)
        {
            presenter.PreferredMinimumWidth = width;
        }
        if (presenter.PreferredMinimumHeight != height)
        {
            presenter.PreferredMinimumHeight = height;
        }
    }

    private void UpdatePage()
    {
        var selected = (Navigation.SelectedItem as NavigationViewItem)?.Tag as string;
        HomePage.Visibility = VisibilityFor(selected is null or "Home");
        LogsPage.Visibility = VisibilityFor(selected == "Logs");
        TunnelingPage.Visibility = VisibilityFor(selected == "Tunneling");
        SettingsPage.Visibility = VisibilityFor(selected == "Settings");
        if (selected == "Logs")
        {
            FlushLogs();
        }
    }

    private void ChangeMode(ConnectionMode mode)
    {
        if (updatingControls || connectionTask is not null || transitioning || busy || mode == profiles.Mode)
        {
            return;
        }

        profiles.Mode = mode;
        RememberMode();
        Save();
        Refresh();
    }

    private void RememberMode()
    {
        if (IsVless)
        {
            vlessMode = profiles.Mode;
        }
        else
        {
            sshMode = profiles.Mode;
        }
    }

    private void Refresh()
    {
        updatingControls = true;
        try
        {
            var modes = new List<(ConnectionMode Mode, string Label)>();
            if (IsVless && ActiveVless is { } vless)
            {
                if (!string.IsNullOrWhiteSpace(vless.TunConfigPath))
                {
                    modes.Add((ConnectionMode.SingBoxTun, "TUN"));
                }

                if (!string.IsNullOrWhiteSpace(vless.SystemProxyConfigPath))
                {
                    modes.Add((ConnectionMode.SystemProxy, L("SystemProxyMode")));
                }
            }
            else if (ActiveSsh is not null && !IsVless)
            {
                modes.Add((ConnectionMode.Socks, L("SocksMode")));
                modes.Add((ConnectionMode.Route, "tun2socks"));
            }

            var displayedModes = ModeCombo.Items.OfType<ComboBoxItem>()
                .Select(item => ((ConnectionMode)item.Tag, (string)item.Content));
            if (!displayedModes.SequenceEqual(modes))
            {
                ModeCombo.Items.Clear();
                foreach (var (mode, label) in modes)
                {
                    ModeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
                }
            }

            ModeCombo.SelectedItem = ModeCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is ConnectionMode mode && mode == profiles.Mode);
            SetSelection(ThemeCombo, profiles.Theme);
            SetSelection(LanguageCombo, profiles.Language);
            SetSelection(CloseBehaviorCombo, profiles.CloseBehavior);

            RefreshProfileList();
            UpdatePingResult();
        }
        finally
        {
            updatingControls = false;
        }

        UpdateConnectionControls();
        _ = LoadProtocolLabelsAsync();
    }

    private void RefreshProfileList()
    {
        var selected = ProfileList.SelectedItem as ProfileItem;
        var activeName = ActiveName;
        var items = profiles.Ssh.Select(profile => (profile.Name, IsVless: false, Protocol: "SSH"))
            .Concat(profiles.Vless.Select(profile => (profile.Name, IsVless: true,
                Protocol: protocolLabels.GetValueOrDefault((profile, vlessMode), "VLESS"))))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.IsVless)
            .ToArray();
        var replaceRows = !profileRows.Select(row => (row.Name, row.IsVless))
            .SequenceEqual(items.Select(item => (item.Name, item.IsVless)));
        if (replaceRows)
        {
            profileRows = items.Select(item => new ProfileItem(item.Name, item.IsVless)).ToArray();
        }

        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var (text, brush) = GetPingResult(item.IsVless, item.Name);
            profileRows[index].Update(item.IsVless == IsVless && SameName(item.Name, activeName), item.Protocol, text, brush);
        }

        if (replaceRows)
        {
            ProfileList.ItemsSource = profileRows;
            var highlighted = selected is null
                ? null
                : profileRows.FirstOrDefault(item => item.IsVless == selected.IsVless && SameName(item.Name, selected.Name));
            ProfileList.SelectedItem = highlighted ?? profileRows.FirstOrDefault(item => item.IsActive);
            ScrollToSelectedProfile();
        }

        ProfilesEmptyText.Visibility = VisibilityFor(profileRows.Length == 0);
    }

    private void ScrollToSelectedProfile()
    {
        if (ProfileList.IsLoaded && ProfileList.SelectedItem is ProfileItem selected)
        {
            ProfileList.ScrollIntoView(selected);
        }
    }

    private async Task LoadProtocolLabelsAsync()
    {
        if (loadingProtocolLabels)
        {
            return;
        }

        loadingProtocolLabels = true;
        try
        {
            while (!closing)
            {
                var mode = vlessMode;
                var version = protocolLabelsVersion;
                var missing = profiles.Vless.Where(profile => !protocolLabels.ContainsKey((profile, mode))).ToArray();
                if (missing.Length == 0)
                {
                    return;
                }

                var descriptions = await Task.Run(() => missing.Select(profile => ProfileProtocol.Describe(profile, mode)).ToArray());
                if (closing)
                {
                    return;
                }

                if (version != protocolLabelsVersion)
                {
                    continue;
                }

                for (var index = 0; index < missing.Length; index++)
                {
                    protocolLabels[(missing[index], mode)] = descriptions[index];
                }

                RefreshProfileList();
            }
        }
        finally
        {
            loadingProtocolLabels = false;
        }
    }

    private void InvalidateProtocolLabels()
    {
        protocolLabels.Clear();
        protocolLabelsVersion++;
    }

    private Dictionary<string, int?> PingResults(bool isVless) => isVless ? vlessLatency : sshLatency;

    private (string Text, Brush? Brush) GetPingResult(bool isVless, string? name)
    {
        if (name is null || !PingResults(isVless).TryGetValue(name, out var milliseconds))
        {
            return (string.Empty, null);
        }

        return milliseconds is int value
            ? (UiText.Format("PingResult", value), successfulPingBrush)
            : (L("PingFailed"), failedPingBrush);
    }

    private void UpdatePingResult()
    {
        var activeName = ActiveName;
        var (text, brush) = pingQueue?.Any(item => item.IsVless == IsVless && SameName(item.Name, activeName)) == true
            ? (L("Pinging"), (Brush?)null)
            : GetPingResult(IsVless, activeName);
        PingResultText.Text = text;
        if (brush is null)
        {
            PingResultText.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            PingResultText.Foreground = brush;
        }
    }

    private void UpdateConnectionControls()
    {
        var connected = connectionTask is not null;
        var stopping = connected && connectionCancellation?.IsCancellationRequested == true;
        var available = !busy && !transitioning && !closing;
        var editable = !connected && available;
        var canToggle = available && !stopping && (connected || ActiveName is not null);

        updatingControls = true;
        try
        {
            ConnectionSwitch.IsOn = connected && !stopping;
        }
        finally
        {
            updatingControls = false;
        }

        StatusText.Text = stopping || busy || transitioning ? L("Working") : L(connected ? "Connected" : "Disconnected");
        ConnectionSwitch.IsEnabled = canToggle;
        ModeCombo.IsEnabled = editable && ActiveName is not null;
        ProfileList.IsItemClickEnabled = available;
        AddProfileButton.IsEnabled = editable;
        ImportProfileButton.IsEnabled = editable;
        ExclusionSettingsButton.IsEnabled = available;
        FragmentationSettingsButton.IsEnabled = available;
        InstallDependenciesButton.IsEnabled = editable;
        RemoveDependenciesButton.IsEnabled = editable && !pinging;
        RepairWintunButton.IsEnabled = editable && Administrator;
        RestartAsAdminButton.IsEnabled = editable && !Administrator;
        ElevationRestartButton.IsEnabled = RestartAsAdminButton.IsEnabled;
        CheckSshButton.IsEnabled = available;
        PingButton.IsEnabled = ActiveName is not null && !pinging && !busy && !closing;
        PingAllButton.IsEnabled = profileRows.Length != 0 && !pinging && !busy && !closing;
        UpdateProfileActions();
        tray.Update(L("OpenApp"), StatusText.Text, L(connected ? "Disconnect" : "Connect"), L("Exit"), canToggle);
    }

    private void UpdateProfileActions()
    {
        var selected = ProfileList.SelectedItem as ProfileItem;
        var editable = selected is not null && CanEditProfile(selected);
        var canPingSelected = selected is { IsActive: false };
        EditProfileButton.IsEnabled = editable;
        DeleteProfileButton.IsEnabled = editable;
        ProfilePingButton.Visibility = VisibilityFor(canPingSelected);
        ProfilePingButton.IsEnabled = canPingSelected && !pinging && !busy && !closing;
    }

    private bool CanEditProfile(ProfileItem item) =>
        !busy && !transitioning && !closing && (connectionTask is null || !item.IsActive) && profileRows.Contains(item);

    private void ActivateProfile(ProfileItem item)
    {
        if (closing || connectionTask is not null || transitioning || busy || activeDialog is not null ||
            !profileRows.Contains(item) ||
            (item.IsVless == IsVless && SameName(item.Name, ActiveName)))
        {
            return;
        }

        SelectProfile(item.IsVless, item.Name);
        Save();
        Refresh();
    }

    private void HighlightProfile(bool isVless, string name)
    {
        var item = ProfileList.Items.OfType<ProfileItem>()
            .FirstOrDefault(row => row.IsVless == isVless && SameName(row.Name, name));
        if (item is not null)
        {
            ProfileList.SelectedItem = item;
            ScrollToSelectedProfile();
        }
    }

    private void SelectProfile(bool isVless, string name)
    {
        if (isVless)
        {
            profiles.SelectedVless = name;
            profiles.Mode = ActiveVless is { } vless ? AvailableVlessMode(vless, vlessMode) : vlessMode;
        }
        else
        {
            profiles.SelectedSsh = name;
            profiles.Mode = sshMode;
        }
    }

    private void EnsureActiveProfile()
    {
        if (ActiveName is not null)
        {
            if (IsVless && ActiveVless is { } active)
            {
                profiles.Mode = AvailableVlessMode(active, profiles.Mode);
            }

            return;
        }

        var ssh = ActiveSsh ?? profiles.Ssh.FirstOrDefault();
        var vless = ActiveVless ?? profiles.Vless.FirstOrDefault();
        if (vless is not null && (IsVless || ssh is null))
        {
            SelectProfile(true, vless.Name);
        }
        else if (ssh is not null)
        {
            SelectProfile(false, ssh.Name);
        }
    }

    private static ConnectionMode AvailableVlessMode(VlessProfile profile, ConnectionMode preferred)
    {
        return (string.IsNullOrWhiteSpace(profile.TunConfigPath), string.IsNullOrWhiteSpace(profile.SystemProxyConfigPath)) switch
        {
            (true, false) => ConnectionMode.SystemProxy,
            (false, true) => ConnectionMode.SingBoxTun,
            _ => preferred
        };
    }
    private async Task RunActionAsync(Func<Task> action)
    {
        if (closing || activeDialog is not null)
        {
            return;
        }

        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (busy || transitioning || closing)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        busy = true;
        UpdateConnectionControls();
        try
        {
            operationTask = action(cancellation.Token);
            await operationTask;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            logs.Enqueue(L("OperationCanceled"));
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            operationTask = null;
            operationCancellation = null;
            busy = false;
            if (!closing)
            {
                UpdateConnectionControls();
            }
        }
    }

    private void FlushLogs()
    {
        if (logs.TryDrain(out var text))
        {
            pendingLogRender = text;
        }

        if (LogsPage.Visibility != Visibility.Visible || pendingLogRender is not { } logText)
        {
            return;
        }

        pendingLogRender = null;
        LogBox.Inlines.Clear();
        foreach (var segment in AnsiLogText.Parse(logText))
        {
            var run = new Run { Text = segment.Text };
            if (segment.Foreground is { } color)
            {
                run.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, color.Red, color.Green, color.Blue));
            }

            LogBox.Inlines.Add(run);
        }

        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
    }

    private void UpdateLogToggleLabel() =>
        ToggleLogsButton.Content = L(logs.IsEnabled ? "DisableLogging" : "EnableLogging");

    private void Notify(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (!closing)
        {
            Notice.Message = message;
            Notice.Severity = severity;
            Notice.IsOpen = true;
        }
    }

    private void ReportError(Exception exception)
    {
        logs.Enqueue(exception.Message);
        Notify(exception.Message, InfoBarSeverity.Error);
    }

    private bool Save()
    {
        try
        {
            profiles.LastSshMode = sshMode;
            profiles.LastVlessMode = vlessMode;
            store.Save(profiles);
            return true;
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return false;
        }
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = profiles.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.PreferredTheme = profiles.Theme switch
            {
                "Light" => TitleBarTheme.Light,
                "Dark" => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode
            };
        }
    }

    private void UpdatePreference(ComboBox combo, Action<string> change)
    {
        if (!updatingControls && combo.SelectedItem is ComboBoxItem { Tag: string value })
        {
            change(value);
            Save();
        }
    }

    internal void RestoreWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }
        Activate();
    }

    private async Task RequestCloseAsync()
    {
        if (closing || activeDialog is not null)
        {
            return;
        }

        if (profiles.CloseBehavior == "Exit" || !tray.IsAvailable)
        {
            await ExitAsync();
            return;
        }

        if (profiles.CloseBehavior == "MinimizeToTray")
        {
            AppWindow.Hide();
            return;
        }

        await RunActionAsync(async () =>
        {
            var remember = new CheckBox { Content = L("RememberChoice"), Margin = new Thickness(0, 8, 0, 0) };
            var content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = L(connectionTask is null ? "CloseMessage" : "CloseActiveMessage"), TextWrapping = TextWrapping.Wrap },
                    remember
                }
            };
            var dialog = CreateDialog(L("CloseTitle"), content, L("MinimizeToTray"));
            dialog.SecondaryButtonText = L("ExitApplication");
            var result = await ShowDialogAsync(dialog);
            if (result == ContentDialogResult.None)
            {
                return;
            }

            if (remember.IsChecked == true)
            {
                profiles.CloseBehavior = result == ContentDialogResult.Primary ? "MinimizeToTray" : "Exit";
                Save();
                Refresh();
            }

            if (result == ContentDialogResult.Primary)
            {
                AppWindow.Hide();
            }
            else
            {
                await ExitAsync();
            }
        });
    }

    private async Task ExitAsync()
    {
        if (closing)
        {
            return;
        }

        closing = true;
        operationCancellation?.Cancel();
        pingCancellation?.Cancel();
        transitionCancellation?.Cancel();
        connectionCancellation?.Cancel();
        activeDialog?.Hide();
        await WaitForWorkAsync(connectionTask, operationTask, pingTask, transitionTask);

        tray.Dispose();
        session.Dispose();
        logTimer.Stop();
        allowClose = true;
        Close();
    }

    private static async Task WaitForWorkAsync(params Task?[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks.OfType<Task>());
        }
        catch (Exception)
        {
            // Each operation reports its own result.
        }
    }

    private async Task RestartAsAdminAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? throw new InvalidOperationException(L("ExecutableUnavailable")),
                Arguments = Program.RestartElevatedArgument,
                UseShellExecute = true,
                Verb = "runas"
            });
            await ExitAsync();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
        }
    }

    private static bool SameName(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static void SetSelection(ComboBox combo, string value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == value);
    }

    private static Visibility VisibilityFor(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class ProfileItem(string name, bool isVless) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public bool IsVless { get; } = isVless;
    public bool IsActive { get; private set; }
    public string Protocol { get; private set; } = string.Empty;
    public string Latency { get; private set; } = string.Empty;
    public Brush? LatencyBrush { get; private set; }
    public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LatencyVisibility => string.IsNullOrEmpty(Latency) ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(bool active, string protocol, string latency, Brush? latencyBrush)
    {
        if (IsActive != active)
        {
            IsActive = active;
            Notify(nameof(ActiveVisibility));
        }

        if (Protocol != protocol)
        {
            Protocol = protocol;
            Notify(nameof(Protocol));
        }

        UpdateLatency(latency, latencyBrush);
    }

    internal void UpdateLatency(string latency, Brush? latencyBrush)
    {
        if (Latency != latency || LatencyBrush != latencyBrush)
        {
            Latency = latency;
            LatencyBrush = latencyBrush;
            Notify(nameof(Latency));
            Notify(nameof(LatencyBrush));
            Notify(nameof(LatencyVisibility));
        }
    }

    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public override string ToString() => $"{Name}, {Protocol}";
}
