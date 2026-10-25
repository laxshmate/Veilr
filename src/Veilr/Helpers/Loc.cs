namespace Veilr.Helpers;

public static class Loc
{
    private static string _lang = "ja";

    public static void SetLanguage(string lang) => _lang = lang;

    public static string SheetMode => _lang == "en" ? "Sheet mode" : "シートモード";
    public static string EraseMode => _lang == "en" ? "Erase mode" : "消去モード";
    public static string SheetModeFullScreen => _lang == "en" ? "Sheet mode (full screen)" : "シートモード（全画面）";
    public static string EraseModeFullScreen => _lang == "en" ? "Erase mode (full screen)" : "消去モード（全画面）";
    public static string SwitchToEraseMode => _lang == "en" ? "Switch to erase" : "消去モードに切替";
    public static string SwitchToSheetMode => _lang == "en" ? "Switch to sheet" : "シートモードに切替";
    public static string FullScreen => _lang == "en" ? "🖥 Full screen" : "🖥 全画面";
    public static string ExitFullScreen => _lang == "en" ? "🖥 Exit" : "🖥 解除";
    public static string Export => _lang == "en" ? "📷 Save" : "📷 保存";
    public static string Settings => _lang == "en" ? "⚙ Settings" : "⚙ 設定";
    public static string ShowHideSheet => _lang == "en" ? "Show / Hide sheet" : "シートを表示 / 非表示";
    public static string SettingsMenu => _lang == "en" ? "Settings..." : "設定...";
    public static string Exit => _lang == "en" ? "Exit" : "終了";

    // Settings window
    public static string SettingsTitle => _lang == "en" ? "Veilr Settings" : "Veilr 設定";
    public static string TabColor => _lang == "en" ? "Color" : "色設定";
    public static string TabBehavior => _lang == "en" ? "Behavior" : "動作";
    public static string TabHotkey => _lang == "en" ? "Hotkeys" : "ホットキー";
    public static string TabAppearance => _lang == "en" ? "Appearance" : "外観";

    public static string SheetColor => _lang == "en" ? "Sheet color" : "シートの色";
    public static string ChangeColor => _lang == "en" ? "Change..." : "変更...";
    public static string SheetOpacity => _lang == "en" ? "Sheet opacity" : "シート透明度";
    public static string EraseTolerance => _lang == "en" ? "Erase mode tolerance" : "消去モードの許容範囲";
    public static string EraseToleranceDesc => _lang == "en"
        ? "Higher values will also erase colors similar to the target"
        : "値が大きいほど、ターゲット色に近い色も消去対象になります";
    public static string Strict => _lang == "en" ? "Strict" : "厳密";
    public static string Flexible => _lang == "en" ? "Flexible" : "柔軟";
    public static string EraseAlgorithm => _lang == "en" ? "Erase method" : "消し方";
    public static string ChromaKeyDesc => _lang == "en"
        ? "Erases with smooth boundaries. Good for blurred text or faint colors."
        : "境界をなめらかに消す。文字の輪郭がにじんでいたり、色が薄い部分があるときに向いている";
    public static string LabMaskDesc => _lang == "en"
        ? "Sharp removal. Good for text and shapes with clear color boundaries."
        : "くっきり消す。文字と背景がはっきり分かれている場面で使いやすい";
    public static string YCbCrDesc => _lang == "en"
        ? "Default. Uses TV broadcast color math. Removes text edges cleanly and also works for video."
        : "既定。テレビ放送と同じ色の扱い方で判定する。文字のふちまできれいに消えやすく、動画にも向いている";

    public static string ModeSelection => _lang == "en" ? "Mode" : "モード切替";
    public static string SheetModeDesc => _lang == "en"
        ? "Overlay a colored filter to hide same-color text (like a physical red sheet)"
        : "カラーフィルターを重ねて同色の文字を隠す（実物の赤シートと同じ）";
    public static string EraseModeDesc => _lang == "en"
        ? "Detect and erase the target color by filling with surrounding background"
        : "ターゲット色を検出し、周囲の背景色で塗りつぶして消去する";

    public static string EraseSettings => _lang == "en" ? "Erase mode settings" : "消去モード設定";
    public static string AutoRefresh => _lang == "en" ? "Auto refresh" : "自動更新";
    public static string AutoRefreshDesc => _lang == "en"
        ? "Periodically recapture the screen at the specified interval"
        : "指定した間隔で画面を自動的に再キャプチャします";
    public static string UpdateFrequency => _lang == "en" ? "Update rate:" : "更新頻度:";
    public static string UpdateFrequencyDesc => _lang == "en"
        ? "1ms = MAX (unlimited), 8ms = 120fps, 16ms = 60fps. Lower = faster but more CPU/GPU."
        : "1ms = 最速（無制限）、8ms = 120fps、16ms = 60fps。小さいほどCPU/GPU負荷増";

    public static string BarOpacity => _lang == "en" ? "Bar opacity (erase mode)" : "バー透明度（消去モード）";
    public static string BarOpacityDesc => _lang == "en"
        ? "0% = fully transparent, 100% = fully opaque"
        : "0% = 完全に透明、100% = 不透明";
    public static string GpuAcceleration => _lang == "en" ? "GPU acceleration" : "GPU高速化";
    public static string GpuAccelerationDesc => _lang == "en"
        ? "Use GPU compute shaders for faster processing. Disable if you experience issues."
        : "GPUコンピュートシェーダーで高速処理。問題がある場合はOFFにしてください";

    public static string HotkeySettings => _lang == "en" ? "Keyboard shortcut" : "ショートカットキー設定";
    public static string ToggleSheet => _lang == "en" ? "Toggle sheet:" : "シート表示/非表示:";
    public static string HotkeyNote => _lang == "en"
        ? "* Hotkey changes take effect after restart"
        : "※ ホットキーの変更はアプリの再起動後に反映されます";

    public static string LanguageLabel => "言語 / Language";
    public static string LanguageNote => _lang == "en"
        ? "* The app will restart when language is changed"
        : "※ 言語を変更するとアプリが再起動されます";

    public static string Apply => _lang == "en" ? "Apply" : "適用";
    public static string Cancel => _lang == "en" ? "Cancel" : "キャンセル";

    // Color picker
    public static string ColorPickerTitle => _lang == "en" ? "Color Picker" : "🔍 カラーピッカー";
    public static string EyedropperMode => _lang == "en" ? "Eyedropper" : "スポイトモード";
    public static string EyedropperButton => _lang == "en" ? "🔍 Pick from screen" : "🔍 画面から色を取得";
    public static string PickedColor => _lang == "en" ? "Picked color" : "取得した色";
    public static string ManualInput => _lang == "en" ? "Manual input" : "手動入力";
    public static string UseThisColor => _lang == "en" ? "Use this color" : "この色を使う";
    public static string Eyedropper => _lang == "en" ? "🔍 Eyedropper" : "🔍 スポイト";
}
