---
name: improve-erase
description: 消去モードの色除去アルゴリズムを改善バックログに基づいて反復改善する
user_invocable: true
---

# 消去モード改善ループ

`docs/improvement-backlog.md` の改善案リストを上から順に実施し、各ループでバックログ自体も更新する。

## ループの流れ（1周分）

### ステップ1: バックログ確認

```bash
cat docs/improvement-backlog.md
```

**改善案リストの一番上**の項目を今回実施する。

### ステップ2: 実装

`src/Veilr/Services/ColorDetectorService.cs` を中心に改善を実装する。

新アルゴリズムの場合:
- 既存アルゴリズムを壊さず**新メソッドとして追加**
- `EraseColor()` のディスパッチに追加
- `AppSettings.cs` の `EraseAlgorithm` に新しい値を追加

### ステップ3: ビルド・起動・スクリーンショット

```bash
powershell -Command "Get-Process -Name 'Veilr' -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 1"
dotnet publish src/Veilr -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./dist
```

Veilr_settings.jsonをdist/にコピーしてから起動:
```bash
cp build/Veilr_settings.json dist/Veilr_settings.json 2>/dev/null
cd C:/workspace/Veilr && ./dist/Veilr.exe &
sleep 6
```

PowerShellでウィンドウ検出→クロップ→保存:
```powershell
Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class WImprove {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc f, IntPtr p);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static RECT Find(int pid) { RECT r = new RECT(); EnumWindows((h, p) => { int fp; GetWindowThreadProcessId(h, out fp); if (fp == pid && IsWindowVisible(h)) { GetWindowRect(h, out r); return false; } return true; }, IntPtr.Zero); return r; }
}
'@
$procs = @(Get-Process -Name 'Veilr' -ErrorAction SilentlyContinue)
$r = [WImprove]::Find($procs[0].Id)
$s = [System.Windows.Forms.Screen]::PrimaryScreen
$b = New-Object System.Drawing.Bitmap($s.Bounds.Width, $s.Bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($b)
$g.CopyFromScreen($s.Bounds.Location, [System.Drawing.Point]::Empty, $s.Bounds.Size)
$c = $b.Clone((New-Object System.Drawing.Rectangle($r.L, $r.T, ($r.R-$r.L), ($r.B-$r.T))), $b.PixelFormat)
$c.Save("C:/tmp/veilr_improve_N.png", [System.Drawing.Imaging.ImageFormat]::Png)
$c.Dispose(); $g.Dispose(); $b.Dispose()
```

### ステップ4: 品質チェック

Readツールでスクリーンショットを確認し、全項目を評価:

| チェック項目 | 合格基準 |
|---|---|
| 黒文字の保持 | 黒テキストが欠けたり薄くなったり崩れていない |
| 赤文字の消去 | 赤テキスト・赤マーカー(▶、→等)が完全に見えない |
| 「例題」ラベル | 茶色/オレンジの「例題」ヘッダーとその背景が元の色・形のまま保持 |
| 罫線・枠線 | 表の枠線や区切り線が消えたり欠けていない |
| 背景色 | ページの白/クリーム色の背景が汚れたり変色していない |
| 数字・記号 | 数値（0.8626等）やカッコ・ドットが欠損していない |
| 置換品質 | 消去跡が周囲の背景色に自然に溶け込み、白い穴や色のにじみがない |

### ステップ5: 結果に応じた判断

#### 改善が確認できた場合

新アルゴリズムなら設定UIに追加:
1. `AppSettings.cs` に選択肢追加
2. `SettingsViewModel.cs` にラジオボタン用プロパティ追加
3. `SettingsWindow.xaml` にラジオボタン+説明文追加
4. `Loc.cs` に日英の説明文追加

#### 既存アルゴリズムより悪い場合

コードは残すが設定UIには追加しない。バックログに「結果不良」と記録。

### ステップ6: アルゴリズム整理（3つ以上になった場合）

以下を検討:
- **デフォルトの再決定**: 全テストで最も安定した結果のアルゴリズムをデフォルトに
- **劣化アルゴリズムの削除**: 全ケースで他に劣るアルゴリズムは選択肢から除去
- 残すアルゴリズムは**明確に異なる得意分野**があるものだけ

### ステップ7: バックログ更新

`docs/improvement-backlog.md` を更新:

1. 実施した項目に結果（✅成功/❌失敗/△部分的）を記録
2. 今回の実装で**新たに気づいた改善案**を追加
3. **優先順位を再評価**して並べ替え
   - 今回の結果から得た知見で優先度が変わることがある
   - 新しい改善案が既存より高優先度になることもある

### ステップ8: コミット

```bash
git add src/ docs/ .claude/skills/ dist/Veilr.exe
git commit -m "Improvement loop: [実施した改善の概要]"
git push origin main
```

→ **ステップ1に戻る**（次の改善案を実施）

---

## 参照ファイル

- 改善バックログ: `docs/improvement-backlog.md`（優先順位付き改善案リスト）
- アルゴリズム仕様: `docs/algorithm.md`
- 色検出サービス: `src/Veilr/Services/ColorDetectorService.cs`
- 設定モデル: `src/Veilr/Models/AppSettings.cs`（`EraseAlgorithm`フィールド）
- 設定ViewModel: `src/Veilr/ViewModels/SettingsViewModel.cs`
- 設定UI: `src/Veilr/Views/SettingsWindow.xaml`
- ローカライゼーション: `src/Veilr/Helpers/Loc.cs`

## 重要な原則

- **バックログの一番上から順に実施**する
- 1周ごとにバックログ自体を更新・再優先順位化する
- 新アルゴリズムは**既存を壊さず追加**。良ければUIに選択肢追加
- アルゴリズムが増えすぎたら**劣るものを削除**し、**デフォルトを最良に更新**
- 隣接分野（映像制作、印刷、医療画像等）の技術を常に意識する
