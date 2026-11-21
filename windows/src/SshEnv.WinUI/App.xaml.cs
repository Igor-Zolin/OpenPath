using Microsoft.UI.Xaml;

namespace SshEnv.WinUI;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }

    internal void RestoreWindow() => window?.RestoreWindow();
}
