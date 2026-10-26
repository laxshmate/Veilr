using System.IO;
using System.Text.Json;
using Veilr.Models;

namespace Veilr.Services;

public class SettingsService
{
    public const string SettingsFileName = "Veilr_settings.json";

    private static readonly string SettingsPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);

    // Name used up to v1.0.5 — migrated on first load
    private static readonly string LegacySettingsPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    // Diagnostic log written by v1.0.5 and earlier (now Debug builds only)
    private static readonly string LegacyProfileLogPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "capture-profile.log");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        MigrateLegacyFiles();
        string path = File.Exists(SettingsPath) ? SettingsPath
            : File.Exists(LegacySettingsPath) ? LegacySettingsPath // migration failed: read in place
            : "";
        if (path == "") return;

        try
        {
            var json = File.ReadAllText(path);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // TODO: log error
        }
    }

    /// <summary>
    /// settings.json → Veilr_settings.json (keeps the user's settings), and remove
    /// the old diagnostic log in release builds.
    /// </summary>
    private static void MigrateLegacyFiles()
    {
        try
        {
            if (!File.Exists(SettingsPath) && File.Exists(LegacySettingsPath))
                File.Move(LegacySettingsPath, SettingsPath);
        }
        catch { /* read-only folder etc. — Load() falls back to the old file */ }

#if !DEBUG
        try { if (File.Exists(LegacyProfileLogPath)) File.Delete(LegacyProfileLogPath); }
        catch { /* ignore */ }
#endif
    }

    public void AddColorHistory(int[] rgb)
    {
        Settings.ColorHistory.RemoveAll(c => c.SequenceEqual(rgb));
        Settings.ColorHistory.Insert(0, rgb);
        if (Settings.ColorHistory.Count > 20)
            Settings.ColorHistory.RemoveRange(20, Settings.ColorHistory.Count - 20);
    }

    public void AddColorPreset(int[] rgb)
    {
        if (Settings.ColorPresets.Count >= 10) return;
        if (Settings.ColorPresets.Any(c => c.SequenceEqual(rgb))) return;
        Settings.ColorPresets.Add(rgb);
    }
}
