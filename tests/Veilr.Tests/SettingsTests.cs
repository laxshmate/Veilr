using System.Text.Json;
using Veilr.Models;

namespace Veilr.Tests;

/// <summary>
/// AppSettings の AutoRefreshEnabled シリアライズテスト。
/// SettingsViewModel の HasChanges 検出テスト。
/// </summary>
public class SettingsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    // ══════════════════════════════════════════════════════════
    //  AppSettings テスト
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void AppSettings_AutoRefreshEnabled_DefaultIsTrue()
    {
        var settings = new AppSettings();
        Assert.True(settings.AutoRefreshEnabled);
    }

    [Fact]
    public void AppSettings_AutoRefreshEnabled_SerializesCorrectly()
    {
        var settings = new AppSettings { AutoRefreshEnabled = true };
        var json = JsonSerializer.Serialize(settings, JsonOptions);

        Assert.Contains("auto_refresh_enabled", json);
        Assert.Contains("true", json);
    }

    [Fact]
    public void AppSettings_AutoRefreshEnabled_DeserializesCorrectly()
    {
        // A saved OFF must stay OFF even though the default is ON
        var settings = new AppSettings { AutoRefreshEnabled = false };
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.False(deserialized!.AutoRefreshEnabled);
    }

    [Fact]
    public void AppSettings_AutoRefreshEnabled_DeserializeMissing_DefaultsTrue()
    {
        // Simulate old settings file without auto_refresh_enabled
        var json = """{"version":"1.0","mode":"sheet","update_interval_ms":200}""";
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.AutoRefreshEnabled);
    }

    [Fact]
    public void AppSettings_UseGpuProcessing_DefaultIsFalse()
    {
        var settings = new AppSettings();
        Assert.False(settings.UseGpuProcessing);
    }

    [Fact]
    public void AppSettings_UseGpuProcessing_SerializesCorrectly()
    {
        var settings = new AppSettings { UseGpuProcessing = false };
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        Assert.False(restored!.UseGpuProcessing);
    }

    [Fact]
    public void AppSettings_UpdateIntervalMs_DefaultIs200()
    {
        var settings = new AppSettings();
        Assert.Equal(200, settings.UpdateIntervalMs);
    }

    // ══════════════════════════════════════════════════════════
    //  設定の全プロパティ整合性
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void AppSettings_RoundTrip_AllFieldsPreserved()
    {
        var original = new AppSettings
        {
            Mode = "erase",
            AutoRefreshEnabled = true,
            UpdateIntervalMs = 50,
            TargetColor = new ColorSettings
            {
                Rgb = [128, 64, 32],
                EraseAlgorithm = "ycbcr",
                Threshold = new ThresholdSettings { H = 20, S = 60, V = 60 }
            },
            OverlayColor = new OverlayColorSettings
            {
                Rgb = [0, 255, 0],
                Opacity = 0.75
            }
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal("erase", restored!.Mode);
        Assert.True(restored.AutoRefreshEnabled);
        Assert.Equal(50, restored.UpdateIntervalMs);
        Assert.Equal([128, 64, 32], restored.TargetColor.Rgb);
        Assert.Equal("ycbcr", restored.TargetColor.EraseAlgorithm);
        Assert.Equal(20, restored.TargetColor.Threshold.H);
        Assert.Equal([0, 255, 0], restored.OverlayColor.Rgb);
        Assert.Equal(0.75, restored.OverlayColor.Opacity);
    }
}
