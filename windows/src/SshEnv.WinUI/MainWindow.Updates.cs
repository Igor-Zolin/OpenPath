using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using SshEnv.Core;

namespace SshEnv.WinUI;

public sealed partial class MainWindow
{
    private static readonly Version CurrentVersion = Version.Parse(
        Assembly.GetExecutingAssembly().GetName().Version!.ToString(3));

    private async Task CheckForUpdatesAsync()
    {
        CheckUpdatesButton.IsEnabled = false;
        DownloadUpdateLink.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = L("CheckingUpdates");
        try
        {
            // The Inno uninstall entry also covers installations in a custom directory.
            const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SshEnv.WinUI.9D8F82EA-2486-45A4-B269-B8FEC695C1F9_is1";
            using var key = Registry.CurrentUser.OpenSubKey(uninstallKey);
            var rid = RuntimeInformation.RuntimeIdentifier;
            var installed = key?.GetValue("InstallLocation") is string location &&
                string.Equals(Path.Combine(location, $"OpenPath-{rid}.exe"), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            var release = await WindowsRelease.GetLatestAsync(rid, installed);
            if (release == null)
            {
                UpdateStatus.Text = L("NoPublicRelease");
            }
            else if (release.Version <= CurrentVersion)
            {
                UpdateStatus.Text = L("UpToDate");
            }
            else
            {
                UpdateStatus.Text = string.Format(L("UpdateAvailable"), release.Version);
                DownloadUpdateLink.NavigateUri = release.DownloadUri;
                DownloadUpdateLink.Content = L(installed ? "DownloadSetup" : "DownloadPortable");
                DownloadUpdateLink.Visibility = Visibility.Visible;
            }
        }
        catch (Exception exception)
        {
            UpdateStatus.Text = L("UpdateCheckFailed");
            logs.Enqueue(exception.Message);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }
}
