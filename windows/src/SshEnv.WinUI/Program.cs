using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Security.Principal;
using WinRT;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using SshEnv.Core;

namespace SshEnv.WinUI;

internal static class Program
{
    internal const string RestartElevatedArgument = "--restart-elevated";
    private const string PreferencesKey = @"HKEY_CURRENT_USER\Software\OpenPath";
    internal static bool RequireAdministrator
    {
        get => Registry.GetValue(PreferencesKey, nameof(RequireAdministrator), 0) is 1;
        set => Registry.SetValue(PreferencesKey, nameof(RequireAdministrator), value ? 1 : 0);
    }

    internal static void StartElevated()
    {
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            Arguments = RestartElevatedArgument,
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("SSH_ENV_ASKPASS_MODE") == "1")
        {
            var password = Environment.GetEnvironmentVariable("SSH_ENV_PASSWORD");
            if (string.IsNullOrEmpty(password))
            {
                return 1;
            }

            Console.Write(password);
            return 0;
        }

        if (args is ["--setup-dependencies"])
        {
            try
            {
                var dependencies = new DependencyInstaller();
                dependencies.InstallAllAsync().GetAwaiter().GetResult();
                return dependencies.Check().HasSsh ? 0 : 2;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        if (RequireAdministrator && !TunnelSession.IsWindowsAdministrator())
        {
            try { StartElevated(); }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) { }
            return 0;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var instanceName = $@"Local\SshEnv.WinUI.{identity.User?.Value}";
        using var instance = new Mutex(false, instanceName);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Activate");
        var restartElevated = args.Contains(RestartElevatedArgument, StringComparer.Ordinal);
        if (!AcquireInstance(instance, restartElevated ? TimeSpan.FromSeconds(30) : TimeSpan.Zero))
        {
            activation.Set();
            return 0;
        }

        RegisteredWaitHandle? activationRegistration = null;
        try
        {
            Environment.SetEnvironmentVariable("SSH_ENV_ASKPASS_EXECUTABLE", Environment.ProcessPath);
            ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                var app = new App();
                activationRegistration = ThreadPool.RegisterWaitForSingleObject(activation,
                    (_, _) => dispatcher.TryEnqueue(app.RestoreWindow),
                    null, Timeout.Infinite, executeOnlyOnce: false);
            });
        }
        finally
        {
            activationRegistration?.Unregister(null);
            instance.ReleaseMutex();
        }

        return 0;
    }

    private static bool AcquireInstance(Mutex instance, TimeSpan timeout)
    {
        try
        {
            return instance.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}
