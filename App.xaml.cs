using System.Windows;

namespace SimpleUSBBackup;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\SimpleUSBBackup.Desktop", out ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("USB Backup is already open. Use the existing window.", "USB Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
