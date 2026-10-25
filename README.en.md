# Veilr

**Screen Color Eraser** — Hide or remove specific colors from your screen

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/OS-Windows%2010%2F11-blue.svg)]()

[日本語](README.md)

---

## What is Veilr?

A Windows tool that hides or erases specific colors (default: red) from your screen.
Use it as a digital version of a red study sheet, or to remove unwanted color information.

## Features

- **Sheet Mode** — Multiply blend (same principle as a physical red sheet)
- **Erase Mode** — Detect target color pixels and replace with surrounding background
- **3 Erase Algorithms** — Chroma Key / Lab Mask / YCbCr
- **Color Family Auto-detection** — Specify one red, all shades are automatically erased
- **GPU Acceleration** — D3D11 Compute Shader + DXGI Desktop Duplication (optional)
- **120fps+ Real-time Update** — Auto-refresh mode for video content
- **Full Screen Support** — Expand sheet to cover entire monitor
- **Image Export** — Save color-erased images as PNG/JPEG/BMP
- **Color Picker** — Eyedropper with zoom preview
- **Portable** — Single exe, no installation required

## Requirements

- Windows 10 / 11
- GPU acceleration: DirectX 11 capable GPU (Feature Level 11_0+)

## Download

Download `Veilr.exe` from the [Releases](https://github.com/laxshmate/Veilr/releases) page.

## Safety

- **All source code is public** (MIT License). The released exe is built from this repository.
- **No network access.** There is no networking code.
- **No installer, no registry writes.** It only writes `settings.json` (settings) and `capture-profile.log` (capture diagnostics) next to the exe. Delete the folder to remove it.
- Screen capture is used only to process the area under the sheet. Nothing is saved or sent unless you use "Export image".

### Verify the downloaded exe

Since v1.0.2, the exe is built by GitHub Actions ([release.yml](.github/workflows/release.yml)) when a tag is pushed. Locally built binaries are not uploaded.

1. Compare the SHA-256 with `SHA256SUMS.txt` in the release:

   ```powershell
   Get-FileHash .\Veilr.exe -Algorithm SHA256
   ```

2. Verify the build provenance with [GitHub CLI](https://cli.github.com/):

   ```powershell
   gh attestation verify .\Veilr.exe -R laxshmate/Veilr
   ```

## Build

```bash
git clone https://github.com/laxshmate/Veilr.git
cd Veilr
build.bat
```

Produces `dist/Veilr.exe` (self-contained single exe).

## License

[MIT License](LICENSE)
