using System.IO;
using System.Text.Json;
using Veilr.Models;

namespace Veilr.Services;

public class SettingsService
{
    public const string SettingsFileName = "Veilr_settings.json";

    private static readonly string SettingsPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(SettingsPath)) return;

        try
        {
            var json = File.ReadAllText(SettingsPath);
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
