using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SshEnv.Core;

public sealed record InstalledApplication(string DisplayName, string ProcessName, string ExecutablePath)
{
    public override string ToString()
    {
        return string.Equals(DisplayName, ProcessName, StringComparison.OrdinalIgnoreCase)
            ? DisplayName
            : $"{DisplayName} ({ProcessName})";
    }
}

public static class InstalledApplicationCatalog
{
    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledApplication> FindInstalledApplications()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var applications = new List<InstalledApplication>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                ReadAppPaths(hive, view, applications);
                ReadUninstallEntries(hive, view, applications);
            }
        }

        ReadRunningApplications(applications);
        return MergeApplications(applications);
    }

    internal static IReadOnlyList<InstalledApplication> MergeApplications(IEnumerable<InstalledApplication> applications)
    {
        return applications
            .Where(application => !string.IsNullOrWhiteSpace(application.ProcessName))
            .GroupBy(application => application.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(application => IsGenericDisplayName(application.DisplayName, application.ProcessName))
                .ThenBy(application => string.IsNullOrEmpty(application.ExecutablePath))
                .ThenBy(application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .First())
            .OrderBy(application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(application => application.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ReadRunningApplications(ICollection<InstalledApplication> applications)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var application = ReadRunningApplication(process, ReadProcessExecutablePath);
                if (application is not null)
                {
                    applications.Add(application);
                }
            }
        }
    }

    internal static InstalledApplication? ReadRunningApplication(Process process, Func<int, string?> readExecutablePath)
    {
        string processName;
        try
        {
            if (process.Id is 0 or 4)
            {
                return null;
            }

            processName = process.ProcessName;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // A process may exit between enumeration and inspection.
            return null;
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            processName += ".exe";
        }

        string? executablePath = null;
        try
        {
            executablePath = readExecutablePath(process.Id);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            // Process-name rules still work when a protected process denies access to its path.
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new InstalledApplication(processName, processName, string.Empty);
        }

        processName = Path.GetFileName(executablePath);
        return new InstalledApplication(ReadExecutableDisplayName(executablePath, processName), processName, executablePath);
    }

    private static string? ReadProcessExecutablePath(int processId)
    {
        // MainModule requires more access and can fail for elevated or different-bitness games.
        const uint processQueryLimitedInformation = 0x1000;
        using var handle = OpenProcess(processQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new StringBuilder(32768);
        var length = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder executableName, ref int size);

    private static void ReadAppPaths(RegistryHive hive, RegistryView view, ICollection<InstalledApplication> applications)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var appPaths = baseKey.OpenSubKey(AppPathsKey);
            if (appPaths is null)
            {
                return;
            }

            foreach (var subKeyName in appPaths.GetSubKeyNames())
            {
                using var entry = appPaths.OpenSubKey(subKeyName);
                var executablePath = ExtractExecutablePath(entry?.GetValue(null) as string);
                if (executablePath is null)
                {
                    continue;
                }

                var processName = Path.GetFileName(executablePath);
                applications.Add(new InstalledApplication(
                    ReadExecutableDisplayName(executablePath, processName),
                    processName,
                    executablePath));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    private static void ReadUninstallEntries(RegistryHive hive, RegistryView view, ICollection<InstalledApplication> applications)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall is null)
            {
                return;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(subKeyName);
                var displayName = (entry?.GetValue("DisplayName") as string)?.Trim();
                var executablePath = ExtractExecutablePath(entry?.GetValue("DisplayIcon") as string);
                if (string.IsNullOrWhiteSpace(displayName) || executablePath is null)
                {
                    continue;
                }

                applications.Add(new InstalledApplication(displayName, Path.GetFileName(executablePath), executablePath));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    private static string? ExtractExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = Environment.ExpandEnvironmentVariables(value.Trim());
        if (candidate.StartsWith('"'))
        {
            var closingQuote = candidate.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                candidate = candidate[1..closingQuote];
            }
        }
        else
        {
            var executableEnd = candidate.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableEnd >= 0)
            {
                candidate = candidate[..(executableEnd + ".exe".Length)];
            }
        }

        candidate = candidate.Trim().Trim('"');
        return candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)
            ? Path.GetFullPath(candidate)
            : null;
    }

    private static string ReadExecutableDisplayName(string executablePath, string fallback)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executablePath);
            return FirstNonEmpty(version.FileDescription, version.ProductName, fallback);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();
    }

    private static bool IsGenericDisplayName(string displayName, string processName)
    {
        return string.Equals(displayName, processName, StringComparison.OrdinalIgnoreCase);
    }
}
