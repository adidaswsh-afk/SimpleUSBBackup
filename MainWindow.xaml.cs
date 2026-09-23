using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SimpleUSBBackup;

public partial class MainWindow : Window
{
    private Settings settings = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool busy;
    private bool refreshing;
    private bool updatingPicker;
    private string? ejectedVolume;
    private bool operationStatus;
    private UsbDrive? SelectedDrive => DrivePicker.SelectedItem as UsbDrive;

    public MainWindow()
    {
        InitializeComponent();
        try { settings = SettingsService.Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ShowError("Settings could not be loaded. Please select your folders again. " + e.Message); }
        ShowFolders();
        timer.Tick += async (_, _) => await RefreshDrivesAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshDrivesAsync();
        if (string.IsNullOrWhiteSpace(settings.FolderA)) SelectFolder(true);
        if (string.IsNullOrWhiteSpace(settings.FolderB)) SelectFolder(false);
        timer.Start();
        UpdateControls();
    }

    private void SelectFolder(bool first)
    {
        if (busy) return;
        var dialog = new OpenFolderDialog { Title = first ? "Choose Folder A to back up" : "Choose Folder B to back up", Multiselect = false };
        string current = first ? settings.FolderA : settings.FolderB;
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) != true) return;
        if (first) settings.FolderA = dialog.FolderName; else settings.FolderB = dialog.FolderName;
        SaveSettings();
        ShowFolders();
        operationStatus = false;
        UpdateControls();
    }

    private void ShowFolders()
    {
        FolderAText.Text = string.IsNullOrWhiteSpace(settings.FolderA) ? "Select a folder" : settings.FolderA;
        FolderBText.Text = string.IsNullOrWhiteSpace(settings.FolderB) ? "Select a folder" : settings.FolderB;
        FolderAText.ToolTip = FolderAText.Text;
        FolderBText.ToolTip = FolderBText.Text;
    }
    private void ChangeA_Click(object sender, RoutedEventArgs e) => SelectFolder(true);
    private void ChangeB_Click(object sender, RoutedEventArgs e) => SelectFolder(false);

    private void SaveSettings()
    {
        try { SettingsService.Save(settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { ShowError("Your choices could not be saved for next time: " + e.Message); }
    }

    private async Task RefreshDrivesAsync()
    {
        if (busy || refreshing || DrivePicker.IsDropDownOpen) return;
        refreshing = true;
        try
        {
            var drives = await Task.Run(UsbService.Detect);
            if (busy || !IsLoaded) return;
            // Keep a successfully ejected volume out of the picker until Windows removes it.
            if (ejectedVolume is not null && drives.All(x => x.VolumeId != ejectedVolume)) ejectedVolume = null;
            drives = drives.Where(x => x.VolumeId != ejectedVolume).ToList();
            string? previous = SelectedDrive?.VolumeId ?? settings.VolumeId;
            var selected = drives.FirstOrDefault(x => x.VolumeId == previous) ?? (drives.Count == 1 ? drives[0] : null);
            updatingPicker = true;
            DrivePicker.ItemsSource = drives;
            DrivePicker.SelectedItem = selected;
            updatingPicker = false;
            if (selected is not null && settings.VolumeId != selected.VolumeId)
            { settings.VolumeId = selected.VolumeId; SaveSettings(); }
            UpdateControls();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            updatingPicker = true;
            DrivePicker.ItemsSource = null;
            updatingPicker = false;
            ShowError("Unable to read USB drives: " + e.Message);
            UpdateControls();
        }
        finally { refreshing = false; }
    }

    private void DrivePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPicker || !IsLoaded) return;
        if (SelectedDrive is { } drive) { settings.VolumeId = drive.VolumeId; SaveSettings(); }
        operationStatus = false;
        UpdateControls();
    }

    private void UpdateControls()
    {
        bool connected = SelectedDrive is not null;
        bool configured = !string.IsNullOrWhiteSpace(settings.FolderA) && !string.IsNullOrWhiteSpace(settings.FolderB);
        BackupButton.IsEnabled = !busy && connected && configured;
        EjectButton.IsEnabled = !busy && connected;
        ChangeAButton.IsEnabled = ChangeBButton.IsEnabled = DrivePicker.IsEnabled = !busy;
        LogButton.IsEnabled = !busy;
        ConnectionText.Text = connected ? "●  Connected" : "●  Not connected";
        ConnectionText.Foreground = connected ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        DriveDetails.Text = SelectedDrive?.Details ?? (DrivePicker.Items.Count > 1 ? "Choose a USB drive above" : "Plug in a USB flash drive");
        if (!busy && !operationStatus)
        {
            StatusText.Text = !configured ? "Choose your source folders" : connected ? "Ready to back up" : "USB not connected";
            ActivityText.Text = !configured ? "Use Change to select Folder A and Folder B." : connected ? "Saves to Backup\\FolderA.zip and FolderB.zip" : "Your USB will appear automatically.";
        }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (busy || SelectedDrive is not { } drive) return;
        busy = true;
        operationStatus = true;
        UpdateControls();
        ErrorText.Visibility = Visibility.Collapsed;
        BackupProgressBar.Value = 0;
        PercentText.Text = "0%";
        StatusText.Text = "Preparing...";
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        string sources = $"Folder A: {settings.FolderA}{Environment.NewLine}Folder B: {settings.FolderB}{Environment.NewLine}USB: {drive.Name} · {drive.Root} ({drive.VolumeId})";
        bool succeeded = false;
        string? error = null;
        var throttle = Stopwatch.StartNew();
        string lastStage = "";
        var progress = new Progress<BackupProgress>(value =>
        {
            BackupProgressBar.Value = value.Percent;
            PercentText.Text = $"{value.Percent:0}%";
            StatusText.Text = value.Stage;
            ActivityText.Text = value.File;
            ActivityText.ToolTip = value.File;
        });
        // Throttle on the worker thread before posting to the UI, so thousands of tiny files stay responsive.
        var reporter = new CallbackProgress(value =>
        {
            if (value.Stage != lastStage || value.Percent >= 100 || throttle.ElapsedMilliseconds >= 80)
            {
                ((IProgress<BackupProgress>)progress).Report(value);
                lastStage = value.Stage;
                throttle.Restart();
            }
        });
        try
        {
            LogService.Append($"{started:yyyy-MM-dd HH:mm:ss zzz}  STARTED{Environment.NewLine}{sources}");
            string a = settings.FolderA, b = settings.FolderB;
            await Task.Run(() => new BackupService().RunAsync(a, b, drive.Root, drive.Format,
                () => UsbService.Check(drive), () => UsbService.FreeBytes(drive), reporter));
            succeeded = true;
            StatusText.Text = "✓  Backup Complete";
            BackupProgressBar.Value = 100;
            PercentText.Text = "100%";
            ActivityText.Text = "Both folders are saved. You can eject the USB.";
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            StatusText.Text = "Backup failed";
            ActivityText.Text = "Check the problem below, then run the backup again.";
            ShowError(FriendlyError(ex, drive.Format));
        }
        finally
        {
            clock.Stop();
            try
            {
                LogService.Append($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {(succeeded ? "SUCCESS" : "FAILED")}{Environment.NewLine}{sources}{Environment.NewLine}" +
                    (succeeded ? $"FolderA.zip completed{Environment.NewLine}FolderB.zip completed{Environment.NewLine}" : $"Error: {error}{Environment.NewLine}") +
                    $"Duration: {clock.Elapsed.TotalSeconds:0.0} seconds{Environment.NewLine}---");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { ShowError((succeeded ? "Backup completed, but the log could not be saved. " : "The failure log could not be saved. ") + ex.Message); }
            busy = false;
            UpdateControls();
            await RefreshDrivesAsync();
        }
    }

    private async void Eject_Click(object sender, RoutedEventArgs e)
    {
        if (busy || SelectedDrive is not { } drive) return;
        busy = true;
        operationStatus = true;
        UpdateControls();
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Text = "Ejecting USB...";
        ActivityText.Text = "Waiting for Windows to safely remove the device.";
        try
        {
            await Task.Run(() => EjectService.Eject(drive));
            ejectedVolume = drive.VolumeId;
            StatusText.Text = "USB can now be safely removed.";
            ActivityText.Text = "Your backup is ready to go.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Unable to eject USB";
            ActivityText.Text = "Close files on the USB and try again.";
            ShowError("USB is currently in use or Windows could not safely eject it. Use Safely Remove Hardware if it persists.");
            try { LogService.Append($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  EJECT FAILED · {drive.Root}{Environment.NewLine}{ex}{Environment.NewLine}---"); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { ShowError(ErrorText.Text + " The error log could not be saved."); }
        }
        finally { busy = false; UpdateControls(); await RefreshDrivesAsync(); }
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = new TextBox { Text = LogService.Read(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(18), BorderThickness = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            new Window { Title = "USB Backup · Log", Owner = this, Width = 700, Height = 520, MinWidth = 400, MinHeight = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = text, Icon = Icon }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError("The log could not be opened: " + ex.Message); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; ShowError("Please wait for the current operation to finish before closing."); }
        else timer.Stop();
    }
    private void ShowError(string text) { ErrorText.Text = text; ErrorText.Visibility = Visibility.Visible; }
    private static string FriendlyError(Exception e, string format) => e switch
    {
        UnauthorizedAccessException => "Access denied. Check folder permissions and USB write protection.",
        DirectoryNotFoundException => e.Message,
        InvalidDataException => "The ZIP could not be verified. Run the backup again. " + e.Message,
        IOException => e.Message + (format.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ? " FAT32 cannot store a ZIP of 4 GB or larger." : " Check available space, the USB connection, and files open in other apps."),
        _ => "The backup could not finish. View Log for details."
    };
    private sealed class CallbackProgress(Action<BackupProgress> callback) : IProgress<BackupProgress>
    { public void Report(BackupProgress value) => callback(value); }
}
