using System.Text.Json;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Reads/writes <see cref="AppSettings"/> as JSON under the user's local app-data
/// folder. A corrupt or missing file just yields defaults rather than failing startup.</summary>
public static class AppSettingsStore
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuneSync", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        catch
        {
            // best-effort - losing a settings write shouldn't crash the UI action that triggered it
        }
    }
}
