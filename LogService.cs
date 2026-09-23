using System.IO;

namespace SimpleUSBBackup;

public static class LogService
{
    public static string LogPath => Path.Combine(SettingsService.DataDirectory, "logs.txt");
    public static void Append(string text)
    {
        Directory.CreateDirectory(SettingsService.DataDirectory);
        File.AppendAllText(LogPath, text + Environment.NewLine, System.Text.Encoding.UTF8);
    }
    public static string Read() => File.Exists(LogPath) ? File.ReadAllText(LogPath) : "No backups yet.";
}
