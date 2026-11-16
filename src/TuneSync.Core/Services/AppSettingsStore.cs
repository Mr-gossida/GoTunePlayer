using System.Text.Json;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Reads/writes <see cref="AppSettings"/> as JSON under the user's local app-data
/// folder. A corrupt or missing file just yields defaults rather than failing startup.</summary>
public static class AppSettingsStore
{
    // Computed on use (not at type load), so loading from an explicit path - tests - never
    // touches the real data folder.
    public static string DefaultPath => AppDataFolder.Combine("settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppSettings();

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            (settings.Tone ??= new ToneSettings()).Normalize();
            (settings.Hotkeys ??= new HotkeySettings()).Normalize();
            (settings.Output ??= new OutputSettings()).Normalize();
            (settings.Hud ??= new HudSettings()).Normalize();
            // an unknown value (a newer version's choice, a hand edit) falls back to quitting
            if (!Enum.IsDefined(settings.CloseAction)) settings.CloseAction = CloseAction.Exit;
            return settings;
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
