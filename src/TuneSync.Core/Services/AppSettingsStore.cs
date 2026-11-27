using System.Text.Json;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Reads/writes <see cref="AppSettings"/> as JSON under the user's local app-data
/// folder. A corrupt or missing file just yields defaults rather than failing startup; a corrupt
/// one is kept beside it ("settings.json.bad") before the defaults are saved over it. Saving writes
/// a new file and swaps it in, so a crash or power cut mid-save leaves the old settings, not an
/// empty or half-written file (which used to reset every setting on the next start).</summary>
public static class AppSettingsStore
{
    private static readonly object Gate = new();

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
            // a seek bar look this version doesn't know (a newer version's, a hand edit) goes back to 무작위
            if (!Enum.IsDefined(settings.SeekBarStyle) || settings.SeekBarStyle == SeekBarStyle.RetiredRibbon)
                settings.SeekBarStyle = SeekBarStyle.Random;
            settings.SeekBarRandomExcluded?.RemoveAll(l => !Enum.IsDefined(l) || l == SeekBarStyle.RetiredRibbon);
            return settings;
        }
        catch (Exception)
        {
            // Unreadable: keep a copy (it holds the library folders, the network sources and every
            // choice) for a hand repair - the first save would otherwise write the defaults over it.
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception) { /* best effort */ }
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Saved from the UI thread and from background work alike: one at a time, as the
            // temporary file is shared.
            lock (Gate)
            {
                var temp = path + ".tmp";
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(json);
                    file.Flush(flushToDisk: true); // on disk before it takes the old one's place
                }
                File.Move(temp, path, overwrite: true);
            }
        }
        catch
        {
            // best-effort - losing a settings write shouldn't crash the UI action that triggered it
        }
    }
}
