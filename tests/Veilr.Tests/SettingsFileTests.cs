using System.IO;
using Veilr.Services;

namespace Veilr.Tests;

/// <summary>
/// Settings file name: Veilr_settings.json (the old settings.json is not read).
/// Uses the test binary folder (SettingsService stores next to the exe).
/// </summary>
[Collection("SettingsFile")]
public class SettingsFileTests : IDisposable
{
    private static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string NewPath = Path.Combine(Dir, "Veilr_settings.json");
    private static readonly string OldPath = Path.Combine(Dir, "settings.json");

    public SettingsFileTests() => Clean();
    public void Dispose() => Clean();

    private static void Clean()
    {
        File.Delete(NewPath);
        File.Delete(OldPath);
    }

    [Fact]
    public void Save_WritesVeilrSettingsJson()
    {
        var ss = new SettingsService();
        ss.Settings.UpdateIntervalMs = 123;
        ss.Save();

        Assert.True(File.Exists(NewPath));
        Assert.False(File.Exists(OldPath));
    }

    [Fact]
    public void Load_ReadsVeilrSettingsJson_IgnoresOldName()
    {
        File.WriteAllText(OldPath, """{"update_interval_ms":11}""");
        File.WriteAllText(NewPath, """{"update_interval_ms":22}""");

        var ss = new SettingsService();
        ss.Load();

        Assert.Equal(22, ss.Settings.UpdateIntervalMs);
    }

    [Fact]
    public void Load_OldNameOnly_UsesDefaults()
    {
        File.WriteAllText(OldPath, """{"update_interval_ms":11}""");

        var ss = new SettingsService();
        ss.Load();

        Assert.Equal(200, ss.Settings.UpdateIntervalMs);
    }
}
