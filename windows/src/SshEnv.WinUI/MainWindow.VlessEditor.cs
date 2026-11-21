using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SshEnv.WinUI;

public sealed partial class MainWindow
{
    private VlessSettings? ReadEditableVlessSettings(VlessProfile profile)
    {
        var paths = vlessMode == ConnectionMode.SystemProxy
            ? new[] { profile.SystemProxyConfigPath, profile.TunConfigPath }
            : new[] { profile.TunConfigPath, profile.SystemProxyConfigPath };
        foreach (var path in paths.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                return VlessConfigEditor.Read(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                JsonException or InvalidOperationException or ArgumentException)
            {
                // The other configuration may still provide the editable fields.
            }
        }

        return null;
    }

    private static Func<VlessSettings> AddVlessSettingsEditor(StackPanel form, VlessSettings settings)
    {
        form.Children.Add(new TextBlock { Text = L("SharedVlessFields"), TextWrapping = TextWrapping.Wrap });
        var server = AddDialogTextBox(form, L("Host"), settings.Server);
        var port = AddDialogTextBox(form, L("VlessServerPort"), settings.Port.ToString());
        var uuid = new PasswordBox { Header = "UUID", Password = settings.Uuid };
        form.Children.Add(uuid);
        var flow = AddDialogTextBox(form, "Flow", settings.Flow);
        flow.PlaceholderText = "xtls-rprx-vision";

        var tls = new StackPanel { Spacing = 12 };
        form.Children.Add(new Expander
        {
            Header = L("VlessSecurity"),
            Content = tls,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });
        var security = new ComboBox
        {
            Header = L("VlessSecurity"),
            Items = { L("VlessSecurityNone"), "TLS", "REALITY" },
            SelectedIndex = (int)settings.Security
        };
        tls.Children.Add(security);
        var serverName = AddDialogTextBox(tls, "SNI", settings.ServerName);
        var fingerprint = AddDialogTextBox(tls, L("VlessFingerprint"), settings.Fingerprint);
        var alpn = AddDialogTextBox(tls, "ALPN", settings.Alpn);
        var publicKey = AddDialogTextBox(tls, L("VlessPublicKey"), settings.PublicKey);
        var shortId = AddDialogTextBox(tls, "Short ID", settings.ShortId);

        void UpdateEnabledFields()
        {
            var selected = (VlessSecurity)security.SelectedIndex;
            serverName.IsEnabled = fingerprint.IsEnabled = alpn.IsEnabled = selected != VlessSecurity.None;
            publicKey.IsEnabled = shortId.IsEnabled = selected == VlessSecurity.Reality;
        }

        security.SelectionChanged += (_, _) => UpdateEnabledFields();
        UpdateEnabledFields();

        return () =>
        {
            var edited = new VlessSettings(DialogRequired(server), DialogPort(port), uuid.Password.Trim(),
                flow.Text.Trim(), (VlessSecurity)security.SelectedIndex, serverName.Text.Trim(),
                fingerprint.Text.Trim(), alpn.Text.Trim(), publicKey.Text.Trim(), shortId.Text.Trim());
            if (edited == settings)
            {
                return settings;
            }

            if (!Guid.TryParse(edited.Uuid, out var uuidValue))
            {
                throw new ArgumentException(L("VlessUuidInvalid"));
            }

            if (edited.Security == VlessSecurity.Reality && edited.PublicKey.Length == 0)
            {
                throw new ArgumentException(L("VlessPublicKeyRequired"));
            }

            return edited.Uuid == settings.Uuid ? edited : edited with { Uuid = uuidValue.ToString("D") };
        };
    }
}
