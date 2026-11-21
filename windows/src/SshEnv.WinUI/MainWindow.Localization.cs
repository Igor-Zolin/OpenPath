using Microsoft.UI.Xaml.Automation;

namespace SshEnv.WinUI;

public sealed partial class MainWindow
{
    private static readonly IReadOnlyDictionary<string, (string English, string Russian)> WindowsText =
        new Dictionary<string, (string English, string Russian)>(StringComparer.Ordinal)
        {
            ["CheckUpdates"] = ("Check for updates", "Проверить обновления"),
            ["CheckingUpdates"] = ("Checking GitHub Releases…", "Проверяем выпуски на GitHub…"),
            ["NoPublicRelease"] = ("No public release is available yet.", "Публичный выпуск пока недоступен."),
            ["UpToDate"] = ("You have the latest version.", "У вас актуальная версия."),
            ["UpdateAvailable"] = ("OpenPath {0} is available.", "Доступна версия OpenPath {0}."),
            ["DownloadSetup"] = ("Download installer", "Скачать установщик"),
            ["DownloadPortable"] = ("Download portable EXE", "Скачать портативный EXE"),
            ["UpdateCheckFailed"] = ("Could not check for updates. Try again later.", "Не удалось проверить обновления. Попробуйте позже."),
            ["Connection"] = ("Connection", "Подключение"),
            ["ConnectionStatus"] = ("Connection status", "Состояние подключения"),
            ["Tunneling"] = ("Tunneling", "Туннелирование"),
            ["FragmentationTitle"] = ("TLS fragmentation", "Фрагментация TLS"),
            ["ConfigureFragmentation"] = ("Configure fragmentation", "Настроить фрагментацию"),
            ["FragmentationHelp"] = ("Applies to matching TLS destinations in sing-box TUN and system proxy modes on the next connection. Use one domain rule per line.",
                "Применяется к TLS-трафику выбранных доменов в режимах sing-box TUN и системного прокси при следующем подключении. Указывайте по одному правилу на строку."),
            ["FragmentationMode"] = ("Fragmentation mode", "Режим фрагментации"),
            ["FragmentationOff"] = ("Off", "Выключена"),
            ["FragmentationRecords"] = ("TLS records", "Записи TLS"),
            ["FragmentationTcp"] = ("TCP segments", "Сегменты TCP"),
            ["FragmentationEnabled"] = ("{0} · domain rules: {1}", "{0} · правил доменов: {1}"),
            ["FragmentationDomains"] = ("Destination domains", "Домены назначения"),
            ["FragmentationDelay"] = ("Fallback delay (ms, optional)", "Резервная задержка (мс, необязательно)"),
            ["FragmentationDelayInvalid"] = ("Enter a positive whole number of milliseconds.", "Введите целое положительное число миллисекунд."),
            ["ReplacementVlessKey"] = ("New VLESS key (optional)", "Новый ключ VLESS (необязательно)"),
            ["ReplacementVlessKeyHelp"] = ("Paste a VLESS link to rebuild both configurations for this profile.", "Вставьте ссылку VLESS, чтобы пересоздать обе конфигурации этого профиля."),
            ["SharedVlessFields"] = ("Edits to these connection settings apply to the TUN and Proxy configurations attached to this profile.",
                "Изменённые параметры подключения применяются к конфигурациям TUN и Proxy, привязанным к этому профилю."),
            ["VlessFieldsUnavailable"] = ("The VLESS connection settings could not be read. Choose a configuration file or replace the VLESS key.",
                "Не удалось прочитать параметры подключения VLESS. Выберите файл конфигурации или замените ключ VLESS."),
            ["VlessFilesAndKey"] = ("Configuration files and key replacement", "Файлы конфигурации и замена ключа"),
            ["VlessServerPort"] = ("Server port", "Порт сервера"),
            ["VlessSecurity"] = ("Connection security", "Защита соединения"),
            ["VlessSecurityNone"] = ("None", "Без TLS"),
            ["VlessFingerprint"] = ("uTLS fingerprint", "Отпечаток uTLS"),
            ["VlessPublicKey"] = ("REALITY public key", "Публичный ключ REALITY"),
            ["VlessUuidInvalid"] = ("Enter a valid UUID.", "Укажите корректный UUID."),
            ["VlessPublicKeyRequired"] = ("Enter the REALITY public key.", "Укажите публичный ключ REALITY."),
            ["SshUnavailable"] = ("OpenSSH is unavailable. Check SSH or choose the executable in the profile.", "OpenSSH недоступен. Проверьте SSH или укажите исполняемый файл в профиле."),
            ["Tun2SocksUnavailable"] = ("Install tun2socks from Settings or choose its executable in the profile.", "Установите tun2socks в настройках или укажите исполняемый файл в профиле."),
            ["RoutingMode"] = ("Routing mode", "Режим маршрутизации"),
            ["Latency"] = ("Latency", "Задержка"),
            ["TestLatency"] = ("Test latency", "Проверить задержку"),
            ["TestAllLatency"] = ("Test all", "Проверить все"),
            ["PingAllComplete"] = ("Profile latency checks complete.", "Проверка задержки профилей завершена."),
            ["LatencyProgress"] = ("Testing latency for {0} of {1} profiles…", "Проверка задержки профилей: {0} из {1}…"),
            ["AddProfile"] = ("Add profile", "Добавить профиль"),
            ["Import"] = ("Import", "Импортировать"),
            ["Edit"] = ("Edit", "Изменить"),
            ["Save"] = ("Save", "Сохранить"),
            ["NoProfiles"] = ("No profiles yet.", "Пока нет профилей."),
            ["AdvancedSettings"] = ("Advanced settings", "Расширенные параметры"),
            ["ClearSavedPassword"] = ("Clear saved password", "Удалить сохранённый пароль"),
            ["ProfileFieldRequired"] = ("Enter a value for {0}.", "Заполните поле «{0}»."),
            ["ProfilePortInvalid"] = ("Enter a value between 1 and 65535 for {0}.", "Укажите значение от 1 до 65535 в поле «{0}»."),
            ["ProfileNameExists"] = ("A profile named '{0}' already exists.", "Профиль «{0}» уже существует."),
            ["VlessConfigRequired"] = ("Choose at least one sing-box configuration file.", "Укажите хотя бы один файл конфигурации sing-box."),
            ["LocalProxyPort"] = ("Local proxy port", "Локальный порт прокси"),
            ["SshProfilesImported"] = ("Imported SSH profiles: {0}.", "Импортировано профилей SSH: {0}."),
            ["NoSshProfilesFound"] = ("The file contains no SSH host profiles.", "В файле нет профилей SSH."),
            ["AppearanceSection"] = ("Appearance", "Внешний вид"),
            ["ApplicationSection"] = ("Application", "Приложение"),
            ["RoutingSection"] = ("VLESS TUN routing", "Маршрутизация VLESS TUN"),
            ["Theme"] = ("Theme", "Тема"),
            ["UseSystemTheme"] = ("Use system setting", "Как в системе"),
            ["SocksMode"] = ("SOCKS (SSH -D)", "SOCKS (SSH -D)"),
            ["SystemProxyMode"] = ("System proxy", "Системный прокси"),
            ["OperationCanceled"] = ("Operation canceled.", "Операция отменена."),
            ["ExecutableUnavailable"] = ("Could not determine the path to the application.", "Не удалось определить путь к приложению."),
            ["MissingSshProfile"] = ("Select an SSH profile first.", "Сначала выберите профиль SSH."),
            ["MissingVlessProfile"] = ("Select a VLESS profile first.", "Сначала выберите профиль VLESS."),
            ["MissingTunConfig"] = ("Choose a TUN configuration file for this profile.", "Выберите файл конфигурации TUN для этого профиля."),
            ["MissingProxyConfig"] = ("Choose a system proxy configuration file for this profile.", "Выберите файл конфигурации системного прокси для этого профиля."),
            ["ElevationRequired"] = ("Restart OpenPath as administrator to use this feature.", "Для этой функции перезапустите OpenPath от имени администратора."),
            ["TunnelEnded"] = ("The tunnel ended with exit code {0}.", "Туннель завершился с кодом {0}."),
            ["SingBoxUnavailable"] = ("Install sing-box from Settings or choose an executable for this profile.", "Установите sing-box в настройках или укажите исполняемый файл в профиле."),
            ["UnresolvedIpv4"] = ("The SSH host {0} has no IPv4 address.", "У сервера SSH {0} нет адреса IPv4."),
            ["DependenciesReady"] = ("Dependencies are ready.", "Зависимости готовы к работе."),
            ["InstallingDependencies"] = ("Installing dependencies…", "Установка зависимостей…"),
            ["RepairQuestion"] = (
                "Remove the tun2socks Wintun network adapters? This can interrupt connections that use these adapters.",
                "Удалить сетевые адаптеры Wintun для tun2socks? Это может прервать подключения, которые используют эти адаптеры.")
        };

    private void ApplyLanguage()
    {
        UiText.Language = profiles.Language == "Russian" ? UiLanguage.Russian : UiLanguage.English;
        Title = "OpenPath";

        HomeNav.Content = L("Home");
        LogsNav.Content = L("Logs");
        TunnelingNav.Content = L("Tunneling");
        SettingsNav.Content = L("Settings");

        HomeTitle.Text = L("Connection");
        ConnectionLabel.Text = L("ConnectionStatus");
        ModeLabel.Text = L("RoutingMode");
        ModeCombo.PlaceholderText = L("NoProfile");
        PingLabel.Text = L("Latency");
        PingButton.Content = L("TestLatency");
        PingAllButton.Content = L("TestAllLatency");
        ProfilePingButton.Content = L("TestLatency");

        ProfilesTitle.Text = L("Profiles");
        ProfilesEmptyText.Text = L("NoProfiles");
        AddProfileButton.Content = L("AddProfile");
        ImportProfileButton.Content = L("Import");
        EditProfileButton.Content = L("Edit");
        DeleteProfileButton.Content = L("Delete");
        UpdateProfileMenus();

        LogsTitle.Text = L("Logs");
        ClearLogsButton.Content = L("ClearLogs");
        UpdateLogToggleLabel();
        TunnelingTitle.Text = L("Tunneling");
        SettingsTitle.Text = L("Settings");
        AppearanceLabel.Text = L("AppearanceSection");
        ThemeLabel.Text = L("Theme");
        SystemThemeItem.Content = L("UseSystemTheme");
        LightThemeItem.Content = L("Light");
        DarkThemeItem.Content = L("Dark");
        LanguageLabel.Text = L("Language");
        BehaviorLabel.Text = L("ApplicationSection");
        AppVersionLabel.Text = $"OpenPath {CurrentVersion}";
        CheckUpdatesButton.Content = L("CheckUpdates");
        UpdateStatus.Text = string.Empty;
        DownloadUpdateLink.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        CloseBehaviorLabel.Text = L("CloseBehavior");
        AskCloseItem.Content = L("AskEveryTime");
        TrayCloseItem.Content = L("MinimizeToTray");
        ExitCloseItem.Content = L("ExitApplication");
        RoutingLabel.Text = L("RoutingSection");
        ExclusionSettingsButton.Content = L("ManageExclusions");
        FragmentationLabel.Text = L("FragmentationTitle");
        FragmentationSettingsButton.Content = L("ConfigureFragmentation");
        UpdateFragmentationStatus();
        ToolsLabel.Text = L("Tools");
        CheckSshButton.Content = L("CheckSsh");
        InstallDependenciesButton.Content = L("InstallDependencies");
        RemoveDependenciesButton.Content = L("RemoveDependencies");
        RepairWintunButton.Content = L("RepairWintun");
        RestartAsAdminButton.Content = L("RestartAsAdmin");

        AutomationProperties.SetName(ConnectionSwitch, L("Connection"));
    }

    private static string L(string key)
    {
        if (WindowsText.TryGetValue(key, out var text))
        {
            return UiText.Language == UiLanguage.Russian ? text.Russian : text.English;
        }

        return UiText.Get(key);
    }
}
