using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using SshEnv.Core;

namespace SshEnv.WinUI;

public sealed partial class MainWindow
{
    private static readonly Version CurrentVersion = Version.Parse(
        Assembly.GetExecutingAssembly().GetName().Version!.ToString(3));
    private static readonly HttpClient UpdateClient = new();
    private readonly CancellationTokenSource updateCancellation = new();
    private WindowsRelease? availableRelease;
    private bool installedCopy;
    private bool applyingUpdate;

    private async Task CheckForUpdatesAsync()
    {
        if (!CheckUpdatesButton.IsEnabled || closing || applyingUpdate) return;
        CheckUpdatesButton.IsEnabled = false;
        DownloadUpdateLink.Visibility = Visibility.Collapsed;
        availableRelease = null;
        UpdateStatus.Text = L("CheckingUpdates");
        try
        {
            const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SshEnv.WinUI.9D8F82EA-2486-45A4-B269-B8FEC695C1F9_is1";
            using var key = Registry.CurrentUser.OpenSubKey(uninstallKey);
            var rid = RuntimeInformation.RuntimeIdentifier;
            installedCopy = key?.GetValue("InstallLocation") is string location &&
                string.Equals(Path.Combine(location, $"OpenPath-{rid}.exe"), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            var release = await WindowsRelease.GetLatestAsync(rid, installedCopy);
            if (closing) return;
            if (release == null || release.Version <= CurrentVersion)
            {
                UpdateStatus.Text = L(release == null ? "NoPublicRelease" : "UpToDate");
                return;
            }

            availableRelease = release;
            DisplayAvailableRelease();
            await OfferUpdateAsync();
        }
        catch (Exception exception)
        {
            if (closing) return;
            UpdateStatus.Text = L("UpdateCheckFailed");
            logs.Enqueue(exception.Message);
        }
        finally
        {
            if (!closing) CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void DisplayAvailableRelease()
    {
        UpdateStatus.Text = string.Format(L("UpdateAvailable"), availableRelease!.Version);
        DownloadUpdateLink.Content = L(installedCopy ? "DownloadSetup" : "DownloadPortable");
        DownloadUpdateLink.Visibility = Visibility.Visible;
    }

    private async Task OfferUpdateAsync()
    {
        if (applyingUpdate || closing || availableRelease is not { } release) return;
        applyingUpdate = true;
        string? installer = null;
        try
        {
            var dialog = CreateDialog(string.Format(L("UpdateAvailable"), release.Version),
                new TextBlock { Text = L(installedCopy ? "ConfirmInstall" : "ConfirmPortable"), TextWrapping = TextWrapping.Wrap },
                L(installedCopy ? "DownloadSetup" : "DownloadPortable"));
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || closing) return;
            if (!installedCopy)
            {
                Process.Start(new ProcessStartInfo(release.DownloadUri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                return;
            }

            UpdateStatus.Text = L("DownloadingUpdate");
            Notify(UpdateStatus.Text);
            installer = Path.Combine(Path.GetTempPath(), $"OpenPath-{Guid.NewGuid():N}-Setup.exe");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(updateCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using (var source = await UpdateClient.GetStreamAsync(release.DownloadUri, timeout.Token))
            await using (var target = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(target, timeout.Token);

            using var verified = File.OpenRead(installer);
            await release.VerifyInstallerAsync(verified, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            using var setup = Process.Start(new ProcessStartInfo(installer)
            {
                Arguments = $"/SILENT /NORESTART /OPENPATHUPDATE=1 /OPENPATHPID={Environment.ProcessId}",
                UseShellExecute = false
            });
            installer = null; // Setup is running from this file.
            await ExitAsync();
        }
        catch (Exception exception)
        {
            if (!closing)
            {
                UpdateStatus.Text = L("InstallFailed");
                Notify(UpdateStatus.Text);
                logs.Enqueue(exception.Message);
            }
        }
        finally
        {
            applyingUpdate = false;
            if (installer != null)
            {
                try { File.Delete(installer); }
                catch (IOException exception) { logs.Enqueue(exception.Message); }
            }
        }
    }
}
