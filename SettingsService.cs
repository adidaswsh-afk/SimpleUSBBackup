using System.IO;
using System.Text.Json;

namespace SimpleUSBBackup;

public sealed class Settings
{
    public string FolderA { get; set; } = "";
    public string FolderB { get; set; } = "";
    public string? VolumeId { get; set; }
}

public static class SettingsService
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleUSBBackup");
    public static Settings Load()
    {
        string path = Path.Combine(DataDirectory, "settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new();
    }
    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        string path = Path.Combine(DataDirectory, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
