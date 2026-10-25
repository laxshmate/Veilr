using System.Text.Json.Serialization;

namespace Veilr.Models;

public class AppSettings
{
    public string Version { get; set; } = "1.0";
    public string Mode { get; set; } = "sheet"; // "sheet" or "erase"

    public ColorSettings TargetColor { get; set; } = new();
    public OverlayColorSettings OverlayColor { get; set; } = new();
    public int UpdateIntervalMs { get; set; } = 200;
    public bool AutoRefreshEnabled { get; set; } = false;
    public bool UseGpuProcessing { get; set; } = false;
    public int BarOpacityPercent { get; set; } = 70;
    public HotkeySettings Hotkeys { get; set; } = new();
    public ThemeSettings UiTheme { get; set; } = new();

    public List<int[]> ColorPresets { get; set; } = new();
    public List<int[]> ColorHistory { get; set; } = new();

    public SessionSettings LastSession { get; set; } = new();
    public ExportSettings Export { get; set; } = new();
}

public class ColorSettings
{
    public int[] Rgb { get; set; } = [255, 0, 0];
    public ThresholdSettings Threshold { get; set; } = new();
    public string ThresholdMode { get; set; } = "strict";
    public string EraseAlgorithm { get; set; } = "ycbcr"; // "chromakey" / "labmask" / "ycbcr"。既定は文字のふちまでいちばんきれいに消える ycbcr
}

public class ThresholdSettings
{
    // 既定は設定画面の「許容範囲」20 と同じ値（H = 20 * 0.45、S・V = 20）
    public int H { get; set; } = 9;
    public int S { get; set; } = 20;
    public int V { get; set; } = 20;
}

public class OverlayColorSettings
{
    public int[] Rgb { get; set; } = [255, 0, 0];
    public double Opacity { get; set; } = 0.5;
}

public class HotkeySettings
{
    public string ToggleSheet { get; set; } = "ctrl+shift+e";
}

public class ThemeSettings
{
    public string Mode { get; set; } = "light";
    public int[] AccentColor { get; set; } = [0, 120, 215];
    public string Language { get; set; } = "ja";
}

public class SessionSettings
{
    public int[] TargetColorRgb { get; set; } = [255, 0, 0];
    public ThresholdSettings Threshold { get; set; } = new();
    public string ThresholdMode { get; set; } = "strict";
    public int[] OverlayColorRgb { get; set; } = [255, 0, 0];
    public double OverlayOpacity { get; set; } = 0.5;
    public string Mode { get; set; } = "sheet";
    public int UpdateIntervalMs { get; set; } = 200;
    public List<SheetPosition> Sheets { get; set; } = new();
}

public class SheetPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 420;
    public double H { get; set; } = 200;
}

public class ExportSettings
{
    public string Format { get; set; } = "png";
    public int JpegQuality { get; set; } = 90;
    public string SavePath { get; set; } = "";
}
