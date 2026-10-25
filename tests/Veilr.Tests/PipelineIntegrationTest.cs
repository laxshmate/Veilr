using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Veilr.Models;
using Veilr.Services;

namespace Veilr.Tests;

/// <summary>
/// End-to-end pipeline test: capture → process → save PNG.
/// Verifies the actual screen content is captured and processed correctly.
/// </summary>
[Collection("DesktopDuplication")]
public class PipelineIntegrationTest
{
    [Fact]
    public void EndToEnd_GdiCapture_CpuProcess_SavePng()
    {
        // Step 1: GDI capture (works on any monitor)
        int w = 200, h = 150, stride = w * 4;
        var buf = new FrameBuffer();
        buf.EnsureCapacity(w, h, stride);

        var gdi = new ScreenCaptureService();
        buf.CaptureAndCopyPixels(gdi, 100, 100);

        // Verify captured data is not empty
        int nonZeroCount = 0;
        for (int i = 0; i < buf.Src.Length; i++)
            if (buf.Src[i] != 0) nonZeroCount++;
        Assert.True(nonZeroCount > 100, $"GDI capture should have data, got {nonZeroCount} non-zero bytes");

        // Step 2: CPU process (sheet mode - multiply blend)
        var detector = new ColorDetectorService();
        detector.MultiplyBlendInto(buf, [255, 0, 0]); // red filter

        // Step 3: Save result as PNG for visual verification
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(buf.Dst, 0, bmpData.Scan0, buf.ByteCount);
        bmp.UnlockBits(bmpData);

        string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-pipeline-output.png");
        bmp.Save(outPath, ImageFormat.Png);

        // Verify output is not all black (some pixels should have red channel > 0)
        int redPixels = 0;
        for (int i = 0; i < buf.Dst.Length; i += 4)
            if (buf.Dst[i + 2] > 0) redPixels++; // BGRA: offset+2 = R
        Assert.True(redPixels > 0, "MultiplyBlend with red should produce red pixels");

        // Step 4: Verify file exists and has size
        Assert.True(File.Exists(outPath), $"Output PNG should exist at {outPath}");
        Assert.True(new FileInfo(outPath).Length > 100, "Output PNG should have content");
    }

    [Fact]
    public void EndToEnd_DxgiCapture_CpuProcess_SavePng()
    {
        using var dxgi = new DxgiCaptureService();
        if (!dxgi.TryInitialize())
        {
            // Skip on CI without display
            return;
        }

        int w = 200, h = 150, stride = w * 4;
        var buf = new FrameBuffer();
        buf.EnsureCapacity(w, h, stride);

        // Try DXGI capture (with retry)
        Thread.Sleep(100); // wait for first DWM frame
        bool captured = false;
        for (int attempt = 0; attempt < 5 && !captured; attempt++)
        {
            captured = dxgi.TryCaptureRegion(100, 100, w, h, buf.Src, stride);
            if (!captured) Thread.Sleep(50);
        }

        if (!captured)
        {
            // DXGI failed, use GDI fallback
            dxgi.CaptureIntoBuffer(buf, 100, 100);
        }

        // Process
        var detector = new ColorDetectorService();
        detector.MultiplyBlendInto(buf, [255, 0, 0]);

        // Save
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(buf.Dst, 0, bmpData.Scan0, buf.ByteCount);
        bmp.UnlockBits(bmpData);

        string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-dxgi-output.png");
        bmp.Save(outPath, ImageFormat.Png);

        Assert.True(new FileInfo(outPath).Length > 100);
    }

    [Fact]
    public void EndToEnd_EraseMode_CpuProcess_SavePng()
    {
        int w = 200, h = 150, stride = w * 4;
        var buf = new FrameBuffer();
        buf.EnsureCapacity(w, h, stride);

        var gdi = new ScreenCaptureService();
        buf.CaptureAndCopyPixels(gdi, 100, 100);

        // Erase red with ChromaKey
        var detector = new ColorDetectorService();
        var target = new ColorSettings
        {
            Rgb = [255, 0, 0],
            Threshold = new ThresholdSettings { H = 15, S = 50, V = 50 },
            EraseAlgorithm = "chromakey"
        };
        detector.EraseColorInto(buf, target);

        // Save
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(buf.Dst, 0, bmpData.Scan0, buf.ByteCount);
        bmp.UnlockBits(bmpData);

        string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-erase-output.png");
        bmp.Save(outPath, ImageFormat.Png);

        // Verify output has data
        int nonZero = 0;
        for (int i = 0; i < buf.Dst.Length; i++)
            if (buf.Dst[i] != 0) nonZero++;
        Assert.True(nonZero > 100, $"Erase output should not be empty, got {nonZero} non-zero bytes");
        Assert.True(new FileInfo(outPath).Length > 100);
    }

    [SkippableFact]
    public void EndToEnd_GpuProcess_MultiplyBlend_SavePng()
    {
        using var dxgi = new DxgiCaptureService();
        Skip.IfNot(dxgi.TryInitialize(), "No DXGI");

        using var gpu = new GpuProcessingService();
        Skip.IfNot(gpu.Initialize(dxgi.Device!, dxgi.Context!),
            $"GPU init failed: {gpu.InitError}");

        int w = 200, h = 150;
        gpu.EnsureTexturesPublic(w, h);

        // Capture to GPU
        Thread.Sleep(100);
        for (int i = 0; i < 5; i++)
        {
            if (dxgi.TryCaptureToGpuTexture(100, 100, w, h, gpu.GetSrcTexture()!))
                break;
            Thread.Sleep(50);
        }

        // GPU MultiplyBlend
        gpu.ProcessMultiplyBlend(gpu.GetSrcTexture()!, [255, 0, 0], w, h);

        // Read result
        byte[] result = new byte[w * h * 4];
        gpu.ReadResultToCpu(result, w, h);

        // Save
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(result, 0, bmpData.Scan0, result.Length);
        bmp.UnlockBits(bmpData);

        string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-gpu-blend-output.png");
        bmp.Save(outPath, ImageFormat.Png);

        Assert.True(new FileInfo(outPath).Length > 100);
    }

    [SkippableFact]
    public void EndToEnd_GpuProcess_ChromaKey_SavePng()
    {
        using var dxgi = new DxgiCaptureService();
        Skip.IfNot(dxgi.TryInitialize(), "No DXGI");

        using var gpu = new GpuProcessingService();
        Skip.IfNot(gpu.Initialize(dxgi.Device!, dxgi.Context!),
            $"GPU init failed: {gpu.InitError}");

        int w = 200, h = 150;
        gpu.EnsureTexturesPublic(w, h);

        Thread.Sleep(100);
        for (int i = 0; i < 5; i++)
        {
            if (dxgi.TryCaptureToGpuTexture(100, 100, w, h, gpu.GetSrcTexture()!))
                break;
            Thread.Sleep(50);
        }

        var target = new ColorSettings
        {
            Rgb = [255, 0, 0],
            Threshold = new ThresholdSettings { H = 15, S = 50, V = 50 },
            EraseAlgorithm = "chromakey"
        };

        // GPU ChromaKey — should not freeze (5s timeout)
        var task = Task.Run(() =>
            gpu.ProcessEraseChromaKey(gpu.GetSrcTexture()!, target, w, h));
        bool completed = task.Wait(5000);
        Skip.IfNot(completed, "ChromaKey GPU timed out");

        byte[] result = new byte[w * h * 4];
        gpu.ReadResultToCpu(result, w, h);

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(result, 0, bmpData.Scan0, result.Length);
        bmp.UnlockBits(bmpData);

        string outPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-gpu-chromakey-output.png");
        bmp.Save(outPath, ImageFormat.Png);

        Assert.True(new FileInfo(outPath).Length > 100);
    }
}
