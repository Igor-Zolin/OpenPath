using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SshEnv.Core;

internal sealed class WindowsSystemProxy : IDisposable
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private readonly ProxyValue proxyEnable;
    private readonly ProxyValue proxyServer;
    private readonly ProxyValue proxyOverride;
    private bool disposed;

    private WindowsSystemProxy(ProxyValue proxyEnable, ProxyValue proxyServer, ProxyValue proxyOverride)
    {
        this.proxyEnable = proxyEnable;
        this.proxyServer = proxyServer;
        this.proxyOverride = proxyOverride;
    }

    public static WindowsSystemProxy Enable(string proxyServer)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("System proxy mode is supported only on Windows.");
        }

        using var key = OpenInternetSettings(writable: true);
        var state = new WindowsSystemProxy(
            ProxyValue.Read(key, "ProxyEnable"),
            ProxyValue.Read(key, "ProxyServer"),
            ProxyValue.Read(key, "ProxyOverride"));

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
        key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
        NotifySettingsChanged();
        return state;
    }

    public void Dispose()
    {
        if (disposed || !OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = OpenInternetSettings(writable: true);
        proxyEnable.Restore(key);
        proxyServer.Restore(key);
        proxyOverride.Restore(key);
        NotifySettingsChanged();
        disposed = true;
    }

    private static RegistryKey OpenInternetSettings(bool writable)
    {
        return Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable)
            ?? throw new InvalidOperationException("Could not open current user Internet Settings registry key.");
    }

    private static void NotifySettingsChanged()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private sealed record ProxyValue(string Name, object? Value, RegistryValueKind ValueKind, bool Exists)
    {
        public static ProxyValue Read(RegistryKey key, string name)
        {
            var value = key.GetValue(name);
            var exists = value is not null;
            var valueKind = exists ? key.GetValueKind(name) : RegistryValueKind.String;
            return new ProxyValue(name, value, valueKind, exists);
        }

        public void Restore(RegistryKey key)
        {
            if (!Exists)
            {
                try
                {
                    key.DeleteValue(Name);
                }
                catch (ArgumentException)
                {
                }

                return;
            }

            key.SetValue(Name, Value!, ValueKind);
        }
    }
}
