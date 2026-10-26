# Veilr - Screen Color Eraser 仕様書

**バージョン**: v1.0.1
**更新日**: 2026-04-09
**対応OS**: Windows 10/11
**開発言語**: C# (.NET 8 / WPF)
**ライセンス**: MIT
**配布形式**: exe単体（self-contained、インストール不要）

---

## 1. 概要

画面上の指定色を隠す・消すタスクトレイ常駐ツール。

### 1.1 動作モード

| モード | 動作 | 処理 |
|--------|------|------|
| **シートモード** | 乗算ブレンドで同色の文字を隠す | GPU/CPU |
| **消去モード** | ターゲット色を周囲色で塗りつぶして消去 | GPU/CPU |

### 1.2 処理モード

| モード | 表示 | 説明 |
|--------|------|------|
| **GPU** | `[GPU]` | D3D11 Compute Shader（DXGI直接キャプチャ） |
| **CPU** | `[CPU]` | Parallel.For + Lab LUT（GDIフォールバック） |

ウィンドウ左上に `シートモード [GPU]` のように表示。

---

## 2. 機能一覧

### 2.1 シートモード
- 乗算ブレンド（実物の赤シートと同じ原理）
- `結果 = ピクセル × シート色 / 255`

### 2.2 消去モード
- 3つのアルゴリズム: Chroma Key / Lab Mask / YCbCr
- CIE Lab色空間で色差計算
- 色ファミリー自動検出（Lab色相角度±10°）
- クロマキー方式のソフトアルファ（0.0〜1.0）
- 段階的デスピル
- BFS距離変換による背景色推定

### 2.3 キャプチャ
- DXGI Desktop Duplication（GPU直接、<1ms）
- GDI CopyFromScreen（フォールバック）
- WDA_EXCLUDEFROMCAPTURE（ウィンドウ自身を除外）
- マルチモニター対応: 全モニターを個別に複製し、各モニターの最新フレームを保持。モニターを跨ぐ範囲は各モニターから合成する
- 画面に変化がない間も、保持したフレームから現在位置を切り出す(移動しても古い画像が残らない)
- 回転したモニター・別 GPU のモニター・画面外にかかる範囲は GDI で取得し、GPU 処理は継続
- キャプチャスレッドは Per-Monitor DPI(物理ピクセル座標)で動作。拡大率が異なるモニターでも位置がずれない
- 自動復旧: ACCESS_LOST(ロック画面・UAC・スリープ復帰・解像度変更)では複製を作り直し、デバイス喪失(ドライバー更新・TDR)では D3D デバイスと GPU シェーダーを再生成する

### 2.4 自動更新
- 1ms（最速、無制限）〜 500ms
- バックグラウンドスレッド処理
- CompositionTarget.Rendering同期
- 高精度タイマー（timeBeginPeriod）

### 2.5 GPU高速化
- D3D11 Compute Shader（8本のHLSLシェーダー）
- DXGI キャプチャと同一デバイスでゼロコピー
- Jump Flooding Algorithm（BFS代替）
- 起動時GPU能力テスト
- 設定でON/OFF切替
- エラー時自動CPUフォールバック

### 2.6 その他
- 全画面モード
- 画像エクスポート（PNG/JPEG/BMP）
- カラーピッカー（スポイト + ズームプレビュー）
- 前回値の復元
- 日本語/英語UI
- 全辺・全角リサイズ（WM_NCHITTEST）

---

## 3. 設定項目

| 項目 | 範囲 | デフォルト |
|------|------|-----------|
| シートの色 | RGB / HEX | #FF0000 |
| シート透明度 | 10-100% | 50% |
| 消去アルゴリズム | chromakey / labmask / ycbcr | ycbcr |
| 許容範囲 | 0-100 | 20 |
| 自動更新 | ON/OFF | ON |
| 更新頻度 | 1ms-500ms | 200ms |
| GPU高速化 | ON/OFF | OFF |
| バー透明度 | 0-100% | 70% |
| ショートカット | キー組み合わせ | Ctrl+Shift+E |
| UI言語 | ja / en | ja |

---

## 4. アーキテクチャ

```
CaptureLoop (background thread)
  ├── DXGI AcquireNextFrame
  ├── GPU path: TryCaptureToGpuTexture → Compute Shader → ReadResultToCpu
  ├── CPU path: CaptureIntoBuffer → EraseColorInto / MultiplyBlendInto
  ├── Buffer.BlockCopy → front buffer
  └── _frameReady = true

OnRendering (UI thread, 60Hz)
  └── WriteableBitmap.WritePixels (memcpy only, <0.5ms)
```

### 4.1 サービス構成

| サービス | 役割 |
|----------|------|
| ScreenCaptureService | GDI CopyFromScreen |
| DxgiCaptureService | DXGI Desktop Duplication |
| GpuProcessingService | D3D11 Compute Shader |
| ColorDetectorService | CPU色検出・処理 |
| SettingsService | JSON設定読み書き |
| HotkeyService | グローバルホットキー |

### 4.2 HLSLシェーダー

| シェーダー | 用途 |
|-----------|------|
| MultiplyBlend.hlsl | シートモード |
| AlphaChromaKey.hlsl | Lab色差→アルファ |
| AlphaYCbCr.hlsl | YCbCr色差→アルファ |
| MaskLabMask.hlsl | 二値マスク+膨張 |
| JfaInit.hlsl | JFA種初期化 |
| JfaStep.hlsl | JFA伝播 |
| Blend.hlsl | アルファブレンド |
| Despill.hlsl | デスピル |

---

## 5. ビルド

```
build.bat → dist/Veilr.exe
```

.NET 8 SDK が必要。ビルドスクリプトはこの1つだけ。

---

## 6. テスト

50件の自動テスト（xUnit）:
- アルゴリズム正当性テスト
- 設定シリアライズテスト
- UI コントロール存在テスト
- DXGI/GPUパイプラインテスト
- E2Eパイプラインテスト（キャプチャ→処理→PNG保存）

```
dotnet test tests/Veilr.Tests
```
