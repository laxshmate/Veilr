# Veilr

**Screen Color Eraser** — 画面上の指定色を隠す・消すWindows常駐ツール

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/OS-Windows%2010%2F11-blue.svg)]()

[English](README.en.md)

---

## Veilrとは

画面上の特定の色（デフォルト：赤）を隠したり消したりできるWindows用の常駐ツール。
勉強用の赤シートのデジタル版として使ったり、画面上の不要な色情報を除去できる。

## 特徴

- **シートモード** — 乗算ブレンド（実物の赤シートと同じ原理）で同色の文字を隠す
- **消去モード** — 指定色を検出し、周囲の色で自然に塗りつぶして完全に消去
- **3つの消去アルゴリズム** — Chroma Key / Lab Mask / YCbCr から選択可能
- **色ファミリー自動検出** — 1つの赤を指定するだけで濃淡の赤も自動的に消去
- **GPU高速化** — D3D11 Compute Shader + DXGI Desktop Duplication（オプション）
- **120fps+リアルタイム更新** — 自動更新モードで動画にも対応
- **全画面対応** — モニターを選択してシートを全画面に拡大
- **画像エクスポート** — 色抜き済みの画像をPNG/JPEG/BMPで保存
- **カラーピッカー** — スポイトで画面上の任意の色を取得（ズームプレビュー付き）
- **前回値の復元** — 色・位置・サイズ・モードを自動記憶
- **日本語/英語対応** — UI言語を切替可能
- **ポータブル** — exe1つで動作、インストール不要

## 動作環境

- Windows 10 / 11
- .NET 8 ランタイム（self-contained版はランタイム不要）
- GPU高速化: DirectX 11対応GPU（Feature Level 11_0以上）

## ダウンロード

[Releases](https://github.com/laxshmate/Veilr/releases) ページから `Veilr.exe` をダウンロードして実行。

> **SmartScreenの警告が出る場合**
> 初回起動時に「WindowsによってPCが保護されました」と表示されることがあります。
> コード署名のないexeで表示される標準的な警告です。
> 「詳細情報」→「実行」で起動できます。

## 安全性

- **ソースはすべて公開**しています（MIT License）。配布している exe は、このリポジトリのソースからビルドしたものです
- **通信しません**。ネットワークにつなぐコードはありません（`HttpClient` などを使っていません）
- **インストール不要・レジストリに書き込みません**。書き込むのは exe と同じフォルダの `settings.json`（設定）と `capture-profile.log`（画面の取り込みの動作記録）だけです。消すときはフォルダごと消せば終わりです
- 画面の取り込みは、シートの下の画面を色処理するためだけに使い、保存・送信はしません（「画像エクスポート」で自分で保存したときだけファイルになります）

### ダウンロードした exe を確かめる

v1.0.2 以降の exe は、タグを push したときに GitHub Actions（[release.yml](.github/workflows/release.yml)）がビルドしてリリースに載せています。手元でビルドしたものは載せていません。

1. **SHA-256 を比べる**（リリースの `SHA256SUMS.txt` と同じ値になれば、改ざんされていません）

   ```powershell
   Get-FileHash .\Veilr.exe -Algorithm SHA256
   ```

2. **ビルドの証明を確かめる**（[GitHub CLI](https://cli.github.com/) で。このリポジトリの Actions が作った exe かが分かります）

   ```powershell
   gh attestation verify .\Veilr.exe -R laxshmate/Veilr
   ```

## 使い方

### 基本操作

1. `Veilr.exe` を起動するとタスクトレイに常駐し、シートウィンドウが表示される
2. シートのどこでもドラッグして移動
3. 辺や角をドラッグしてサイズを調整
4. F5またはスペースキーで画面を再キャプチャ

### ショートカットキー

| キー | 動作 |
|------|------|
| `Ctrl+Shift+E` | シートの表示/非表示 |
| `F5` / `Space` | 画面再キャプチャ |
| `Escape` | 全画面モード解除 |

### モードの違い

**シートモード（デフォルト）**

実物の赤シートと同じ**乗算ブレンド**方式。赤い文字が赤い背景に溶け込んで見えなくなる。

**消去モード**

指定色のピクセルを検出し、周囲の背景色で置換する。3つの消し方から選択可能：

| 消し方 | 設定名 | 向いている場面 |
|---|---|---|
| **くっきり消す** | Lab Mask | 文字と背景がはっきり分かれている場面 |
| **なめらかに消す** | クロマキー | 文字の輪郭がにじんでいたり、色が薄い部分があるとき |
| **動画向け**（既定） | YCbCr | 文書の文字のふちまできれいに消したいとき・動画 |

### 設定

| 項目 | 説明 |
|------|------|
| モード切替 | シートモード / 消去モードの切替（動作タブ） |
| シートの色 | カラーピッカー、スポイト、HEX入力 |
| シート透明度 | シートモードのブレンド強度 |
| 消し方 | くっきり消す / なめらかに消す / 動画向け（既定） |
| 許容範囲 | 厳密〜柔軟のスライダー（既定 20） |
| 自動更新 | ON/OFFと更新頻度（1ms〜500ms） |
| GPU高速化 | D3D11 Compute Shaderの有効/無効 |
| バー透明度 | 消去モードのツールバー透明度 |
| ショートカットキー | カスタマイズ可能 |
| UI言語 | 日本語 / 英語 |

## 技術詳細

### パフォーマンス

| 項目 | 値 |
|---|---|
| キャプチャ方式 | DXGI Desktop Duplication（GPU直接、GDIフォールバック付き） |
| 色処理 | Parallel.For + Lab LUT + BFS距離変換 / GPU Compute Shader |
| フレームレート | 120fps+（自動更新モード） |
| UIスレッド負荷 | <0.5ms/frame（バックグラウンドスレッド処理） |
| メモリ確保 | ゼロアロケーション（バッファ再利用） |

### シートモードの原理

**乗算ブレンド（Multiply Blend）** を使用。実物のカラーフィルターと同じ原理。

```
結果.R = ピクセル.R × シート色.R / 255
結果.G = ピクセル.G × シート色.G / 255
結果.B = ピクセル.B × シート色.B / 255
```

### 消去モードの原理

**CIE Lab色空間**で色差を計算。HSVと異なり、黒と赤が同じ色相になる問題がない。

- **色相距離**で同系色を判定
- **色ファミリー自動拡張** — Lab色相角度±10°で同系色を自動検出
- **クロマキー方式のソフトアルファ** — 0.0〜1.0の連続値でスムーズな境界
- **段階的デスピル** — 消去境界の色カストを距離に応じて除去
- **BFS距離変換** — 背景色を最近傍の非キーイングピクセルから伝播

## ビルド方法

### 必要なもの

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### ビルド

```bash
git clone https://github.com/laxshmate/Veilr.git
cd Veilr
build.bat
```

`dist/Veilr.exe` が生成される（self-contained単一exe、ランタイム不要）。配布用の exe はリポジトリに入れず、リリースに載せます。

## プロジェクト構成

```
Veilr/
├── README.md / README.en.md
├── LICENSE
├── build.bat                       ビルドスクリプト（これ1つだけ）
├── docs/
│   ├── spec.md                     仕様書
│   ├── ui-design.md                UI設計書
│   ├── algorithm.md                アルゴリズム仕様
│   ├── development-journal.md      開発日誌
│   └── Veilr-sample.docx           デモ用Wordファイル
├── src/Veilr/
│   ├── Views/                      SheetWindow, SettingsWindow, ColorPicker
│   ├── ViewModels/                 SheetViewModel, SettingsViewModel
│   ├── Services/                   ScreenCapture, DxgiCapture, GpuProcessing, ColorDetector
│   ├── Shaders/Source/             HLSL Compute Shaders (8本)
│   ├── Helpers/                    Win32Interop, Loc
│   ├── Models/                     AppSettings
│   └── Resources/                  アイコン
└── tests/Veilr.Tests/              テスト (53件)
```

## ライセンス

[MIT License](LICENSE)
