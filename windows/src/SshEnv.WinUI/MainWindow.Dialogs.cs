using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using SshEnv.Core;
using Windows.ApplicationModel.DataTransfer;

namespace SshEnv.WinUI;

public sealed partial class MainWindow
{
    private ContentDialog CreateDialog(string title, FrameworkElement content, string primaryButtonText) =>
        new()
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = L("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (closing || activeDialog is not null)
        {
            return ContentDialogResult.None;
        }

        dialog.XamlRoot = Root.XamlRoot;
        dialog.RequestedTheme = Root.RequestedTheme;
        activeDialog = dialog;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            activeDialog = null;
        }
    }

    private void UpdateProfileMenus()
    {
        AddProfileButton.Flyout = CreateProfileMenu(
            "AddSshConfig", () => EditSshProfileAsync(null),
            "AddVlessConfig", () => EditVlessProfileAsync(null));
        ImportProfileButton.Flyout = CreateProfileMenu(
            "ImportSshConfig", () => RunOperationAsync(ImportSshProfilesAsync),
            "VlessImport", ImportVlessLinkAsync);
    }

    private MenuFlyout CreateProfileMenu(
        string sshLabel, Func<Task> sshAction, string vlessLabel, Func<Task> vlessAction)
    {
        var ssh = new MenuFlyoutItem { Text = L(sshLabel) };
        ssh.Click += async (_, _) => await RunActionAsync(sshAction);
        var vless = new MenuFlyoutItem { Text = L(vlessLabel) };
        vless.Click += async (_, _) => await RunActionAsync(vlessAction);
        var menu = new MenuFlyout();
        menu.Items.Add(ssh);
        menu.Items.Add(vless);
        return menu;
    }

    private async void ProfileRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ProfileItem profile } && CanConnectProfile(profile))
        {
            args.Handled = true;
            await TransitionConnectionAsync(profile);
        }
    }

    private void ProfileRowRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ProfileItem profile } target)
        {
            return;
        }

        var menu = new MenuFlyout();
        AddCommand("Edit", Symbol.Edit, CanEditProfile(profile), () => RunActionAsync(() => EditProfileAsync(profile)));
        AddCommand("Connect", Symbol.Play, CanConnectProfile(profile), () => TransitionConnectionAsync(profile));
        AddCommand("Delete", Symbol.Delete, CanEditProfile(profile), () => RunActionAsync(() => DeleteProfileAsync(profile)));
        AddCommand("Ping", Symbol.Clock, !busy && !pinging && !closing, () => MeasureProfilesAsync([profile]));
        args.Handled = true;
        menu.ShowAt(target, args.GetPosition(target));

        void AddCommand(string key, Symbol symbol, bool enabled, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = L(key), Icon = new SymbolIcon(symbol), IsEnabled = enabled };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }
    }

    private Task EditProfileAsync(ProfileItem? item) => item switch
    {
        { IsVless: true } when CanEditProfile(item) => EditVlessProfileAsync(item),
        { IsVless: false } when CanEditProfile(item) => EditSshProfileAsync(item),
        _ => Task.CompletedTask
    };

    private async Task EditSshProfileAsync(ProfileItem? item)
    {
        var index = item is null ? -1 : profiles.Ssh.FindIndex(profile => SameName(profile.Name, item.Name));
        var existing = index < 0 ? new SshProfile { User = Environment.UserName } : profiles.Ssh[index];
        var form = new StackPanel { Spacing = 12 };
        var feedback = CreateDialogFeedback();
        var name = AddDialogTextBox(form, L("ProfileName"), existing.Name);
        var host = AddDialogTextBox(form, L("Host"), existing.Host);
        var user = AddDialogTextBox(form, L("User"), existing.User);
        var password = new PasswordBox { Header = L("Password") };
        form.Children.Add(password);
        var clearPassword = new CheckBox { Content = L("ClearSavedPassword") };
        if (!string.IsNullOrEmpty(existing.EncryptedPassword))
        {
            form.Children.Add(clearPassword);
        }

        var identity = AddDialogFileBox(form, L("PrivateKey"), existing.IdentityPath, feedback);
        var advanced = new StackPanel { Spacing = 12 };
        form.Children.Add(new Expander
        {
            Header = L("AdvancedSettings"),
            Content = advanced,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });
        var sshPort = AddDialogTextBox(advanced, L("SshPort"), existing.SshPort.ToString());
        var localPort = AddDialogTextBox(advanced, L("LocalSocksPort"), existing.LocalPort.ToString());
        var executable = AddDialogFileBox(advanced, L("SshExecutable"), existing.SshPath, feedback, ".exe");
        var tun2socks = AddDialogFileBox(advanced, L("Tun2SocksExecutable"), existing.Tun2SocksPath, feedback, ".exe");
        var primaryInterface = AddDialogTextBox(advanced, L("PrimaryInterface"), existing.PrimaryInterface);
        var gateway = AddDialogTextBox(advanced, L("PrimaryGateway"), existing.PrimaryGateway);
        var tunName = AddDialogTextBox(advanced, L("TunName"), existing.TunName);
        var tunAddress = AddDialogTextBox(advanced, L("TunAddress"), existing.TunAddress);
        var dns = AddDialogTextBox(advanced, "DNS", existing.DnsServer);

        var edited = await ShowFormAsync(L(item is null ? "AddProfile" : "Edit"), form, feedback, L("Save"), () =>
        {
            var newName = DialogRequired(name);
            ValidateProfileName(newName, item, profiles.Ssh.Select(profile => profile.Name));
            var newHost = DialogRequired(host);
            var newUser = DialogRequired(user);
            var newSshPort = DialogPort(sshPort);
            var newLocalPort = DialogPort(localPort);
            var encryptedPassword = clearPassword.IsChecked == true
                ? null
                : string.IsNullOrEmpty(password.Password)
                    ? existing.EncryptedPassword
                    : ProfileStore.ProtectPassword(password.Password);
            return existing with
            {
                Name = newName,
                User = newUser,
                Host = newHost,
                SshPort = newSshPort,
                LocalPort = newLocalPort,
                EncryptedPassword = encryptedPassword,
                IdentityPath = DialogOptional(identity),
                SshPath = DialogOptional(executable),
                Tun2SocksPath = DialogOptional(tun2socks),
                PrimaryInterface = DialogOptional(primaryInterface),
                PrimaryGateway = DialogOptional(gateway),
                TunName = DialogOptional(tunName) ?? "wintun",
                TunAddress = DialogOptional(tunAddress) ?? "192.168.123.1",
                DnsServer = DialogOptional(dns) ?? "1.1.1.1"
            };
        });
        if (edited is null)
        {
            return;
        }

        if (index < 0)
        {
            profiles.Ssh.Add(edited);
        }
        else
        {
            profiles.Ssh[index] = edited;
            sshLatency.Remove(existing.Name);
        }

        CompleteProfileEdit(item, edited.Name, isVless: false);
    }

    private async Task EditVlessProfileAsync(ProfileItem? item)
    {
        var index = item is null ? -1 : profiles.Vless.FindIndex(profile => SameName(profile.Name, item.Name));
        var existing = index < 0 ? new VlessProfile(string.Empty, null, null) : profiles.Vless[index];
        var form = new StackPanel { Spacing = 12 };
        var feedback = CreateDialogFeedback();
        var name = AddDialogTextBox(form, L("ProfileName"), existing.Name);
        var originalSettings = ReadEditableVlessSettings(existing);
        var readSettings = originalSettings is null ? null : AddVlessSettingsEditor(form, originalSettings);
        if (index >= 0 && originalSettings is null)
        {
            form.Children.Add(new TextBlock { Text = L("VlessFieldsUnavailable"), TextWrapping = TextWrapping.Wrap });
        }

        var files = new StackPanel { Spacing = 12 };
        form.Children.Add(new Expander
        {
            Header = L("VlessFilesAndKey"),
            Content = files,
            IsExpanded = originalSettings is null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });
        var replacementKey = new PasswordBox
        {
            Header = L("ReplacementVlessKey"),
            PlaceholderText = "vless://…"
        };
        files.Children.Add(replacementKey);
        files.Children.Add(new TextBlock { Text = L("ReplacementVlessKeyHelp"), TextWrapping = TextWrapping.Wrap });
        var tun = AddDialogFileBox(files, L("TunConfig"), existing.TunConfigPath, feedback, ".json");
        var proxy = AddDialogFileBox(files, L("SystemProxyConfig"), existing.SystemProxyConfigPath, feedback, ".json");
        var port = AddDialogTextBox(files, L("LocalProxyPort"), existing.SystemProxyPort.ToString());
        var executable = AddDialogFileBox(files, L("SingBoxExecutable"), existing.SingBoxPath, feedback, ".exe");

        var revision = await ShowFormAsync(L(item is null ? "AddProfile" : "Edit"), form, feedback, L("Save"), () =>
        {
            var newName = DialogRequired(name);
            ValidateProfileName(newName, item, profiles.Vless.Select(profile => profile.Name));
            var localPort = DialogPort(port);
            var tunPath = DialogOptional(tun);
            var proxyPath = DialogOptional(proxy);
            var editedProfile = new VlessProfile(newName, tunPath, proxyPath, localPort, DialogOptional(executable));
            if (!string.IsNullOrWhiteSpace(replacementKey.Password))
            {
                var directory = Path.Combine(store.ConfigDirectory, "edited", Guid.NewGuid().ToString("N"));
                var generated = SingBoxProfileImporter.ImportVlessUriPair(replacementKey.Password, directory, localPort);
                return new VlessConfigRevision(editedProfile with
                {
                    TunConfigPath = generated.TunConfigPath,
                    SystemProxyConfigPath = generated.SystemProxyConfigPath
                }, [generated.TunConfigPath, generated.SystemProxyConfigPath]);
            }

            if (tunPath is null && proxyPath is null)
            {
                throw new ArgumentException(L("VlessConfigRequired"));
            }

            return VlessConfigEditor.CreateRevision(editedProfile, originalSettings, readSettings?.Invoke(),
                index >= 0 && proxyPath is not null ? existing.SystemProxyPort : null);
        });
        if (revision is null)
        {
            return;
        }

        var edited = revision.Profile;
        var previousSelection = (profiles.SelectedSsh, profiles.SelectedVless, profiles.Mode);
        if (index < 0)
        {
            profiles.Vless.Add(edited);
        }
        else
        {
            profiles.Vless[index] = edited;
        }

        UpdateEditedProfileSelection(item, edited.Name, isVless: true);
        if (Save())
        {
            vlessLatency.Remove(existing.Name);
            InvalidateProtocolLabels();
            Refresh();
            HighlightProfile(isVless: true, edited.Name);
            return;
        }

        if (index < 0)
        {
            profiles.Vless.RemoveAt(profiles.Vless.Count - 1);
        }
        else
        {
            profiles.Vless[index] = existing;
        }

        (profiles.SelectedSsh, profiles.SelectedVless, profiles.Mode) = previousSelection;
        VlessConfigRevision.Discard(revision.Files);
    }

    private void ValidateProfileName(string name, ProfileItem? item, IEnumerable<string> names)
    {
        if (!SameName(name, item?.Name) && names.Any(existing => SameName(existing, name)))
        {
            throw new ArgumentException(string.Format(L("ProfileNameExists"), name));
        }
    }

    private void CompleteProfileEdit(ProfileItem? original, string name, bool isVless)
    {
        UpdateEditedProfileSelection(original, name, isVless);
        RefreshEditedProfiles(isVless, name);
    }

    private void UpdateEditedProfileSelection(ProfileItem? original, string name, bool isVless)
    {
        if (original?.IsActive == true || ActiveName is null)
        {
            SelectProfile(isVless, name);
        }
        else if (original is not null)
        {
            if (isVless && SameName(original.Name, profiles.SelectedVless))
            {
                profiles.SelectedVless = name;
            }
            else if (!isVless && SameName(original.Name, profiles.SelectedSsh))
            {
                profiles.SelectedSsh = name;
            }
        }

    }

    private bool RefreshEditedProfiles(bool isVless, string? highlightedName)
    {
        var saved = Save();
        Refresh();
        if (highlightedName is not null)
        {
            HighlightProfile(isVless, highlightedName);
        }

        return saved;
    }

    private async Task ImportVlessLinkAsync()
    {
        var form = new StackPanel { Spacing = 12 };
        var feedback = CreateDialogFeedback();
        form.Children.Add(new TextBlock { Text = L("VlessHelp"), TextWrapping = TextWrapping.Wrap });
        var link = AddDialogTextBox(form, L("VlessLink"));
        form.Children.Add(CreateDialogAction(L("ImportFromClipboard"), feedback, async () =>
        {
            var clipboard = Clipboard.GetContent();
            if (clipboard.Contains(StandardDataFormats.Text))
            {
                link.Text = await clipboard.GetTextAsync();
            }
        }));

        var value = await ShowFormAsync(L("VlessImport"), form, feedback, L("Import"), () => DialogRequired(link));
        if (value is not null)
        {
            await RunOperationAsync(cancellation => ImportVlessProfilesAsync(value, cancellation));
        }
    }

    private async Task ImportSshProfilesAsync(CancellationToken cancellation)
    {
        var picker = new FileOpenPicker(AppWindow.Id) { Title = L("ImportSshConfig") };
        var sshDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (Directory.Exists(sshDirectory))
        {
            picker.SuggestedStartFolder = sshDirectory;
        }

        var selected = await picker.PickSingleFileAsync();
        if (selected is null)
        {
            return;
        }

        var imported = await Task.Run(() => ProfileStore.ReadOpenSshConfig(selected.Path), cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (imported.Count == 0)
        {
            Notify(L("NoSshProfilesFound"), InfoBarSeverity.Warning);
            return;
        }

        foreach (var profile in imported)
        {
            var index = profiles.Ssh.FindIndex(existing => SameName(existing.Name, profile.Name));
            if (index < 0)
            {
                profiles.Ssh.Add(profile);
            }
            else
            {
                var existing = profiles.Ssh[index];
                sshLatency.Remove(existing.Name);
                profiles.Ssh[index] = existing with
                {
                    Host = profile.Host,
                    User = profile.User,
                    SshPort = profile.SshPort,
                    IdentityPath = profile.IdentityPath ?? existing.IdentityPath
                };
            }
        }

        if (profiles.Ssh.All(profile => !SameName(profile.Name, profiles.SelectedSsh)))
        {
            profiles.SelectedSsh = imported[0].Name;
        }

        EnsureActiveProfile();
        if (RefreshEditedProfiles(false, imported[0].Name))
        {
            Notify(string.Format(L("SshProfilesImported"), imported.Count), InfoBarSeverity.Success);
        }
    }

    private async Task ImportVlessProfilesAsync(string value, CancellationToken cancellation)
    {
        const int port = 2080;
        var imported = await SingBoxProfileImporter.ImportLinkAsync(value, store.ConfigDirectory, port);
        cancellation.ThrowIfCancellationRequested();
        string? firstName = null;
        foreach (var profile in imported.Profiles)
        {
            var index = profiles.Vless.FindIndex(existing =>
                SameName(existing.TunConfigPath, profile.TunConfigPath) ||
                SameName(existing.SystemProxyConfigPath, profile.SystemProxyConfigPath));
            string name;
            if (index < 0)
            {
                name = UniqueVlessProfileName(profile.Name);
                profiles.Vless.Add(new VlessProfile(name, profile.TunConfigPath, profile.SystemProxyConfigPath, port));
            }
            else
            {
                var existing = profiles.Vless[index];
                name = existing.Name;
                profiles.Vless[index] = existing with
                {
                    TunConfigPath = profile.TunConfigPath,
                    SystemProxyConfigPath = profile.SystemProxyConfigPath,
                    SystemProxyPort = port
                };
            }

            vlessLatency.Remove(name);
            firstName ??= name;
        }

        if (profiles.Vless.All(profile => !SameName(profile.Name, profiles.SelectedVless)))
        {
            profiles.SelectedVless = firstName;
        }

        InvalidateProtocolLabels();
        EnsureActiveProfile();
        if (RefreshEditedProfiles(true, firstName))
        {
            Notify(string.Format(L("VlessImportComplete"), imported.Profiles.Count, imported.SkippedEntries), InfoBarSeverity.Success);
        }
    }

    private string UniqueVlessProfileName(string original)
    {
        var baseName = string.IsNullOrWhiteSpace(original) ? "VLESS" : original.Trim();
        var name = baseName;
        for (var number = 2; profiles.Vless.Any(profile => SameName(profile.Name, name)); number++)
        {
            name = $"{baseName} ({number})";
        }

        return name;
    }

    private async Task DeleteProfileAsync(ProfileItem? item)
    {
        if (item is null || !CanEditProfile(item))
        {
            return;
        }

        var content = new TextBlock
        {
            Text = string.Format(L(item.IsVless ? "DeleteVlessQuestion" : "DeleteSshQuestion"), item.Name),
            TextWrapping = TextWrapping.Wrap
        };
        var dialog = CreateDialog(L(item.IsVless ? "DeleteVlessConfig" : "DeleteSshConfig"), content, L("Delete"));
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || !CanEditProfile(item))
        {
            return;
        }

        if (item.IsVless)
        {
            profiles.Vless.RemoveAll(profile => SameName(profile.Name, item.Name));
            vlessLatency.Remove(item.Name);
            InvalidateProtocolLabels();
            if (SameName(item.Name, profiles.SelectedVless))
            {
                profiles.SelectedVless = null;
            }
        }
        else
        {
            profiles.Ssh.RemoveAll(profile => SameName(profile.Name, item.Name));
            sshLatency.Remove(item.Name);
            if (SameName(item.Name, profiles.SelectedSsh))
            {
                profiles.SelectedSsh = null;
            }
        }

        EnsureActiveProfile();
        RefreshEditedProfiles(IsVless, ActiveName);
    }

    private async Task EditExclusionsAsync()
    {
        var form = new StackPanel { Spacing = 12 };
        var feedback = CreateDialogFeedback();
        form.Children.Add(new TextBlock { Text = L("ExclusionHelp"), TextWrapping = TextWrapping.Wrap });
        var network = AddDialogTextBox(form, L("NetworkRules"));
        network.AcceptsReturn = true;
        network.TextWrapping = TextWrapping.Wrap;
        network.Height = 112;
        form.Children.Add(new TextBlock { Text = L("ApplicationsHelp"), TextWrapping = TextWrapping.Wrap });
        var processes = AddDialogTextBox(form, L("Applications"));
        processes.AcceptsReturn = true;
        processes.Height = 92;

        void SetEditors(IEnumerable<SingBoxDirectRule> rules)
        {
            network.Text = string.Join(Environment.NewLine, rules
                .Where(rule => rule.Type != "process_name")
                .Select(SingBoxConfigDirectRules.Format));
            processes.Text = SingBoxConfigDirectRules.FormatProcessNames(rules);
        }

        IReadOnlyList<SingBoxDirectRule> ReadEditors() => SingBoxConfigDirectRules.Normalize(
            SingBoxConfigDirectRules.Parse(network.Text).Concat(SingBoxConfigDirectRules.ParseProcessNames(processes.Text)));

        SetEditors(profiles.DirectRules);
        form.Children.Add(CreateDialogAction(L("BrowseExe"), feedback, async () =>
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".exe");
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count != 0)
            {
                AppendProcessNames(processes, files.Select(file => Path.GetFileName(file.Path)));
            }
        }));
        form.Children.Add(CreateApplicationCatalog(processes, feedback));

        var fileActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fileActions.Children.Add(CreateDialogAction(L("ImportExclusions"), feedback, async () =>
        {
            var path = await PickDialogFileAsync(L("ImportExclusions"), ".json");
            if (path is not null)
            {
                SetEditors(SingBoxConfigDirectRules.Import(path));
            }
        }));
        fileActions.Children.Add(CreateDialogAction(L("ExportExclusions"), feedback, async () =>
        {
            var rules = ReadEditors();
            var picker = new FileSavePicker(AppWindow.Id)
            {
                Title = L("ExportExclusions"),
                SuggestedFileName = "ssh-env-exclusions"
            };
            picker.FileTypeChoices.Add("JSON", new[] { ".json" });
            var selected = await picker.PickSaveFileAsync();
            if (selected is not null)
            {
                SingBoxConfigDirectRules.Export(selected.Path, rules);
            }
        }));
        form.Children.Add(fileActions);

        var updated = await ShowFormAsync(L("TunExclusions"), form, feedback, L("SaveExclusions"), ReadEditors);
        if (updated is not null)
        {
            profiles.DirectRules = updated.ToList();
            Save();
        }
    }

    private async Task EditFragmentationAsync()
    {
        var form = new StackPanel { Spacing = 12 };
        var feedback = CreateDialogFeedback();
        form.Children.Add(new TextBlock { Text = L("FragmentationHelp"), TextWrapping = TextWrapping.Wrap });

        var current = profiles.TlsFragmentation;
        var mode = new ComboBox
        {
            Header = L("FragmentationMode"),
            Items = { L("FragmentationOff"), L("FragmentationRecords"), L("FragmentationTcp") },
            SelectedIndex = current is null ? 0 : current.FragmentTcp ? 2 : 1
        };
        form.Children.Add(mode);

        var domains = AddDialogTextBox(form, L("FragmentationDomains"),
            current is null ? null : string.Join(Environment.NewLine, current.Domains.Select(SingBoxConfigDirectRules.Format)));
        domains.AcceptsReturn = true;
        domains.Height = 96;
        domains.PlaceholderText = "example.com";
        var delay = AddDialogTextBox(form, L("FragmentationDelay"), current?.FallbackDelayMilliseconds?.ToString());
        delay.PlaceholderText = "500";

        void UpdateFields()
        {
            domains.IsEnabled = mode.SelectedIndex > 0;
            delay.IsEnabled = mode.SelectedIndex == 2;
        }

        mode.SelectionChanged += (_, _) => UpdateFields();
        UpdateFields();
        var updated = await ShowFormAsync(L("FragmentationTitle"), form, feedback, L("Save"), () =>
        {
            if (mode.SelectedIndex <= 0)
            {
                return new FragmentationDraft(null);
            }

            var rules = SingBoxConfigDirectRules.ParseTlsFragmentDomains(domains.Text);
            var fragmentTcp = mode.SelectedIndex == 2;
            int? fallback = null;
            if (fragmentTcp && DialogOptional(delay) is { } text)
            {
                if (!int.TryParse(text, out var milliseconds) || milliseconds <= 0)
                {
                    throw new ArgumentException(L("FragmentationDelayInvalid"));
                }

                fallback = milliseconds;
            }

            return new FragmentationDraft(new SingBoxTlsFragmentOptions(rules, fragmentTcp, fallback));
        });
        if (updated is not null)
        {
            profiles.TlsFragmentation = updated.Options;
            Save();
            UpdateFragmentationStatus();
        }
    }

    private void UpdateFragmentationStatus()
    {
        var options = profiles.TlsFragmentation;
        FragmentationStatus.Text = options is null
            ? L("FragmentationOff")
            : string.Format(L("FragmentationEnabled"), L(options.FragmentTcp ? "FragmentationTcp" : "FragmentationRecords"),
                options.Domains.Count);
    }

    private sealed record FragmentationDraft(SingBoxTlsFragmentOptions? Options);

    private static Expander CreateApplicationCatalog(TextBox processes, InfoBar feedback)
    {
        var search = new TextBox();
        AutomationProperties.SetName(search, L("SearchApplications"));
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        AutomationProperties.SetName(picker, L("Applications"));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var refresh = new Button { Content = L("RefreshList") };
        var add = CreateDialogAction(L("AddSelected"), feedback, () =>
        {
            if (picker.SelectedItem is InstalledApplication selected)
            {
                AppendProcessNames(processes, [selected.ProcessName]);
            }

            return Task.CompletedTask;
        });
        add.IsEnabled = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, refresh } };
        var content = new StackPanel { Spacing = 8, Children = { search, picker, status, actions } };
        var expander = new Expander
        {
            Header = new TextBlock { Text = L("SearchApplications"), TextWrapping = TextWrapping.Wrap },
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        IReadOnlyList<InstalledApplication>? applications = null;

        void UpdatePicker()
        {
            if (applications is null || !refresh.IsEnabled)
            {
                return;
            }

            var query = search.Text.Trim();
            var selectedName = (picker.SelectedItem as InstalledApplication)?.ProcessName;
            var matches = query.Length == 0
                ? applications
                : applications.Where(application =>
                    application.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    application.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    application.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

            picker.ItemsSource = matches;
            picker.IsEnabled = matches.Count > 0;
            picker.SelectedItem = matches.FirstOrDefault(application => SameName(application.ProcessName, selectedName))
                ?? matches.FirstOrDefault();
            status.Text = matches.Count > 0
                ? string.Format(L("ApplicationsFound"), matches.Count)
                : L(applications.Count == 0 ? "NoApplications" : "NoMatchingApplications");
        }

        async Task ScanAsync()
        {
            if (!refresh.IsEnabled)
            {
                return;
            }

            refresh.IsEnabled = false;
            status.Text = L("ScanningApplications");
            try
            {
                applications = await Task.Run(InstalledApplicationCatalog.FindInstalledApplications);
            }
            catch (Exception exception)
            {
                status.Text = L("RefreshApplicationsFailed");
                SetDialogError(feedback, exception);
                return;
            }
            finally
            {
                refresh.IsEnabled = true;
            }

            UpdatePicker();
        }

        search.TextChanged += (_, _) => UpdatePicker();
        picker.SelectionChanged += (_, _) => add.IsEnabled = picker.SelectedItem is not null;
        refresh.Click += async (_, _) => await ScanAsync();
        expander.Expanding += async (_, _) =>
        {
            if (applications is null)
            {
                await ScanAsync();
            }
        };
        return expander;
    }

    private static void AppendProcessNames(TextBox processes, IEnumerable<string> names)
    {
        var additions = names.Select(name => new SingBoxDirectRule("process_name", name));
        processes.Text = SingBoxConfigDirectRules.FormatProcessNames(
            SingBoxConfigDirectRules.ParseProcessNames(processes.Text).Concat(additions));
    }

    private async Task<T?> ShowFormAsync<T>(
        string title, StackPanel form, InfoBar feedback, string primaryButtonText, Func<T> validate) where T : class
    {
        var scroll = new ScrollViewer
        {
            Content = form,
            MaxHeight = 390,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            },
            Children = { feedback, scroll }
        };
        Grid.SetRow(scroll, 1);
        var dialog = CreateDialog(title, content, primaryButtonText);
        T? value = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                value = validate();
            }
            catch (Exception exception)
            {
                args.Cancel = true;
                SetDialogError(feedback, exception);
            }
        };

        var result = await ShowDialogAsync(dialog);
        return result == ContentDialogResult.Primary && !closing ? value : null;
    }

    private static InfoBar CreateDialogFeedback() => new()
    {
        Severity = InfoBarSeverity.Error,
        IsClosable = false,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 0, 0, 10)
    };

    private static void SetDialogError(InfoBar feedback, Exception exception)
    {
        feedback.Message = exception.Message;
        feedback.Visibility = Visibility.Visible;
        feedback.IsOpen = true;
    }

    private static Button CreateDialogAction(string label, InfoBar feedback, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                feedback.IsOpen = false;
                feedback.Visibility = Visibility.Collapsed;
                await action();
            }
            catch (Exception exception)
            {
                SetDialogError(feedback, exception);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private static TextBox AddDialogTextBox(Panel parent, string label, string? value = null)
    {
        var box = new TextBox { Header = label, Text = value ?? string.Empty };
        parent.Children.Add(box);
        return box;
    }

    private TextBox AddDialogFileBox(Panel parent, string label, string? path, InfoBar feedback, string? extension = null)
    {
        var row = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        var box = AddDialogTextBox(row, label, path);
        var browse = CreateDialogAction(L("Browse"), feedback, async () =>
        {
            var selected = await PickDialogFileAsync(label, extension);
            if (selected is not null)
            {
                box.Text = selected;
            }
        });
        AutomationProperties.SetName(browse, $"{L("Browse")} · {label}");
        browse.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        parent.Children.Add(row);
        return box;
    }

    private async Task<string?> PickDialogFileAsync(string label, string? extension)
    {
        var picker = new FileOpenPicker(AppWindow.Id) { Title = label };
        if (extension is not null)
        {
            picker.FileTypeFilter.Add(extension);
        }

        return (await picker.PickSingleFileAsync())?.Path;
    }

    private static string? DialogOptional(TextBox box) =>
        string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();

    private static string DialogRequired(TextBox box) => DialogOptional(box)
        ?? throw new ArgumentException(string.Format(L("ProfileFieldRequired"), box.Header));

    private static int DialogPort(TextBox box)
    {
        if (int.TryParse(box.Text, out var port) && port is >= 1 and <= 65535)
        {
            return port;
        }

        throw new ArgumentException(string.Format(L("ProfilePortInvalid"), box.Header));
    }
}
