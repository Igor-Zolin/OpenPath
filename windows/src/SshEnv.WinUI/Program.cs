using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Security.Principal;
using WinRT;

namespace SshEnv.WinUI;

internal static class Program
{
    internal const string RestartElevatedArgument = "--restart-elevated";

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
