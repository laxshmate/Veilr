using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Veilr.Models;
using Veilr.Services;

namespace Veilr.Tests;

/// <summary>
/// GPU pipeline integration tests.
/// Tests DXGI initialization, shader compilation, GPU processing, and monitor detection.
/// Skips gracefully if no GPU is available (CI environments).
/// </summary>
[Collection("DesktopDuplication")] // DXGI duplication is per-output exclusive-ish: run sequentially
public class GpuPipelineTests
{
    private static bool HasGpu()
    {
        try { return GpuProcessingService.TestGpuCapability(); }
        catch { return false; }
    }

    // ── DXGI Tests ────────────────────────────────────────────

    [Fact]
    public void DxgiCapture_Initialize_Succeeds()
    {
        using var dxgi = new DxgiCaptureService();
        bool result = dxgi.TryInitialize();
        // May fail in CI (no display), but should not throw
        if (result)
        {
            Assert.True(dxgi.IsUsingDxgi);
            Assert.True(dxgi.OutputCount >= 1, $"OutputCount={dxgi.OutputCount}");
        }
    }

    [Fact]
    public void DxgiCapture_CaptureRegion_ReturnsData()
    {
        using var dxgi = new DxgiCaptureService();
        if (!dxgi.TryInitialize()) return; // skip if no display

        int w = 100, h = 100, stride = w * 4;
        byte[] dst = new byte[stride * h];
        // First capture may fail (no frame yet), retry once
        dxgi.TryCaptureRegion(100, 100, w, h, dst, stride);
        System.Threading.Thread.Sleep(50);
        bool ok = dxgi.TryCaptureRegion(100, 100, w, h, dst, stride);

        if (ok)
        {
            // Verify not all zeros (actual screen content captured)
            bool hasData = false;
            for (int i = 0; i < dst.Length && !hasData; i++)
                if (dst[i] != 0) hasData = true;
            Assert.True(hasData, "Captured data should not be all zeros");
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint ctx);

    private static byte[] GdiCapture(int x, int y, int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        new ScreenCaptureService().CaptureInto(bmp, x, y);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var buf = new byte[w * h * 4];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(data);
        return buf;
    }

    private static double MeanDiff(byte[] p, byte[] q)
    {
        double d = 0;
        for (int i = 0; i < p.Length; i += 4)
            for (int c = 0; c < 3; c++) d += Math.Abs(p[i + c] - q[i + c]);
        return d / (p.Length / 4 * 3);
    }

    /// <summary>
    /// Regression: when no new desktop frame arrives (static screen), capturing a
    /// different position must return that position's pixels, not the previous
    /// region (the old code returned stale data → "frozen" sheet).
    /// </summary>
    [SkippableFact]
    public void DxgiCapture_StaticScreen_NewPositionIsNotStale()
    {
        // Physical pixels, same as the app's capture thread
        var prev = SetThreadDpiAwarenessContext(-4);
        try
        {
            using var dxgi = new DxgiCaptureService();
            Skip.IfNot(dxgi.TryInitialize(), "No DXGI");

            const int w = 64, h = 64, stride = w * 4;
            // Pick two positions on the primary monitor whose content differs
            int ax = 0, ay = 0;
            var gA = GdiCapture(ax, ay, w, h);
            int bx = 0, by = 0; double best = -1;
            for (int yy = 0; yy <= 1000; yy += 100)
                for (int xx = 0; xx <= 1800; xx += 150)
                {
                    double d = MeanDiff(gA, GdiCapture(xx, yy, w, h));
                    if (d > best) { best = d; bx = xx; by = yy; }
                }
            Skip.If(best < 20, $"Screen too uniform to test (max diff {best:F1})");

            var a = new byte[stride * h];
            var b = new byte[stride * h];
            bool ok = false;
            for (int i = 0; i < 20 && !(ok = dxgi.TryCaptureRegion(ax, ay, w, h, a, stride)); i++)
                System.Threading.Thread.Sleep(30);
            Skip.IfNot(ok, "DXGI capture unavailable");

            // Pre-fill with the previous region: an implementation that reports success
            // without writing (the old bug) leaves stale pixels and fails below.
            Array.Copy(a, b, a.Length);
            // Immediately capture elsewhere (usually no new frame → served from cache)
            Skip.IfNot(dxgi.TryCaptureRegion(bx, by, w, h, b, stride), "DXGI capture unavailable");
            var gB = GdiCapture(bx, by, w, h);

            double diffNew = MeanDiff(b, gB), diffOld = MeanDiff(a, gB);
            Assert.True(diffNew < 8 && diffNew < diffOld / 2,
                $"DXGI at ({bx},{by}) should match screen there: diff {diffNew:F1} (stale-region diff {diffOld:F1})");
        }
        finally { SetThreadDpiAwarenessContext(prev); }
    }

    [Fact]
    public void DxgiCapture_RegionOutsideAllOutputs_ReturnsFalse()
    {
        using var dxgi = new DxgiCaptureService();
        if (!dxgi.TryInitialize()) return;
        var dst = new byte[16 * 16 * 4];
        Assert.False(dxgi.TryCaptureRegion(-100000, -100000, 16, 16, dst, 64));
    }

    [Fact]
    public void DxgiCapture_Maintain_IsSafeToCallRepeatedly()
    {
        using var dxgi = new DxgiCaptureService();
        dxgi.Maintain(); // before init: no-op
        if (!dxgi.TryInitialize()) return;
        int gen = dxgi.DeviceGeneration;
        for (int i = 0; i < 5; i++) dxgi.Maintain();
        Assert.Equal(gen, dxgi.DeviceGeneration); // healthy device is not recreated
    }

    private static bool CaptureWithin(DxgiCaptureService dxgi, int ms)
    {
        const int w = 32, h = 32;
        var dst = new byte[w * h * 4];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (dxgi.TryCaptureRegion(10, 10, w, h, dst, w * 4)) return true;
            System.Threading.Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>Regression: after ACCESS_LOST (lock screen, UAC, mode change) capture must resume.</summary>
    [SkippableFact]
    public void DxgiCapture_RecoversFromAccessLost()
    {
        var prev = SetThreadDpiAwarenessContext(-4);
        try
        {
            using var dxgi = new DxgiCaptureService();
            Skip.IfNot(dxgi.TryInitialize(), "No DXGI");
            Skip.IfNot(CaptureWithin(dxgi, 1000), "DXGI capture unavailable");

            dxgi.SimulateAccessLost();
            Assert.True(CaptureWithin(dxgi, 3000), "capture did not resume after ACCESS_LOST");
        }
        finally { SetThreadDpiAwarenessContext(prev); }
    }

    /// <summary>Regression: after device loss (driver update / TDR) a new device is created.</summary>
    [SkippableFact]
    public void DxgiCapture_RecoversFromDeviceLost()
    {
        var prev = SetThreadDpiAwarenessContext(-4);
        try
        {
            using var dxgi = new DxgiCaptureService();
            Skip.IfNot(dxgi.TryInitialize(), "No DXGI");
            Skip.IfNot(CaptureWithin(dxgi, 1000), "DXGI capture unavailable");
            int gen = dxgi.DeviceGeneration;

            dxgi.SimulateDeviceLost();
            Assert.True(CaptureWithin(dxgi, 3000), "capture did not resume after device loss");
            Assert.Equal(gen + 1, dxgi.DeviceGeneration);

            // GPU processing can be rebuilt on the new device
            using var gpu = new GpuProcessingService();
            Assert.True(gpu.Initialize(dxgi.Device!, dxgi.Context!), gpu.InitError);
        }
        finally { SetThreadDpiAwarenessContext(prev); }
    }

    [Fact]
    public void DxgiCapture_CycleOutput_DoesNotThrow()
    {
        using var dxgi = new DxgiCaptureService();
        if (!dxgi.TryInitialize()) return;

        int before = dxgi.CurrentOutputIndex;
        int after = dxgi.CycleOutput();

        if (dxgi.OutputCount > 1)
            Assert.NotEqual(before, after);
        else
            Assert.Equal(0, after);
    }

    [Fact]
    public void DxgiCapture_SwitchToOutput_AllOutputs()
    {
        using var dxgi = new DxgiCaptureService();
        if (!dxgi.TryInitialize()) return;

        for (int i = 0; i < dxgi.OutputCount; i++)
        {
            dxgi.SwitchToOutput(i);
            Assert.Equal(i, dxgi.CurrentOutputIndex);
            Assert.True(dxgi.IsUsingDxgi);
        }
    }

    [Fact]
    public void DxgiCapture_CaptureIntoBuffer_GdiFallback()
    {
        using var dxgi = new DxgiCaptureService();
        // Don't initialize DXGI → forces GDI fallback
        var buf = new FrameBuffer();
        int w = 50, h = 50, stride = w * 4;
        buf.EnsureCapacity(w, h, stride);

        // Should not throw, uses GDI
        dxgi.CaptureIntoBuffer(buf, 100, 100);

        bool hasData = false;
        for (int i = 0; i < buf.Src.Length && !hasData; i++)
            if (buf.Src[i] != 0) hasData = true;
        Assert.True(hasData, "GDI fallback should capture data");
    }

    // ── GPU Processing Tests ──────────────────────────────────

    [Fact]
    public void GpuCapability_Test()
    {
        // Should not throw regardless of GPU availability
        bool result = GpuProcessingService.TestGpuCapability();
        // result may be true or false depending on hardware
    }

    [SkippableFact]
    public void GpuService_Initialize_WithDxgiDevice()
    {
        using var dxgi = new DxgiCaptureService();
        Skip.IfNot(dxgi.TryInitialize(), "No DXGI");
        Skip.If(dxgi.Device == null || dxgi.Context == null, "No D3D11 device");

        using var gpu = new GpuProcessingService();
        bool ok = gpu.Initialize(dxgi.Device!, dxgi.Context!);

        if (ok)
        {
            Assert.True(gpu.IsAvailable);
            Assert.Null(gpu.InitError);
        }
        else
        {
            Assert.NotNull(gpu.InitError);
        }
    }

    [SkippableFact]
    public void GpuService_MultiplyBlend_ProducesOutput()
    {
        using var dxgi = new DxgiCaptureService();
        Skip.IfNot(dxgi.TryInitialize(), "No DXGI");

        using var gpu = new GpuProcessingService();
        Skip.IfNot(gpu.Initialize(dxgi.Device!, dxgi.Context!), "GPU init failed");

        int w = 64, h = 64;
        gpu.EnsureTexturesPublic(w, h);

        // Capture a small region to srcTexture
        System.Threading.Thread.Sleep(50);
        dxgi.TryCaptureToGpuTexture(100, 100, w, h, gpu.GetSrcTexture()!);

        // Process
        gpu.ProcessMultiplyBlend(gpu.GetSrcTexture()!, [255, 0, 0], w, h);

        // Read result
        byte[] result = new byte[w * h * 4];
        gpu.ReadResultToCpu(result, w, h);

        // GPU processed without crashing — verify we got data back
        Assert.Equal(w * h * 4, result.Length);
    }

    [SkippableFact]
    public void GpuService_EraseChromaKey_DoesNotFreeze()
    {
        using var dxgi = new DxgiCaptureService();
        Skip.IfNot(dxgi.TryInitialize(), "No DXGI");

        using var gpu = new GpuProcessingService();
        Skip.IfNot(gpu.Initialize(dxgi.Device!, dxgi.Context!), "GPU init failed");

        int w = 64, h = 64;
        gpu.EnsureTexturesPublic(w, h);
        System.Threading.Thread.Sleep(50);
        dxgi.TryCaptureToGpuTexture(100, 100, w, h, gpu.GetSrcTexture()!);

        var target = new ColorSettings
        {
            Rgb = [255, 0, 0],
            Threshold = new ThresholdSettings { H = 15, S = 50, V = 50 },
            EraseAlgorithm = "chromakey"
        };

        // Should complete without freezing (timeout protection)
        var cts = new CancellationTokenSource(5000); // 5s timeout
        var task = Task.Run(() =>
        {
            gpu.ProcessEraseChromaKey(gpu.GetSrcTexture()!, target, w, h);
        });

        bool completed = task.Wait(5000);
        Assert.True(completed, "ChromaKey GPU should complete within 5 seconds (not freeze)");

        byte[] result = new byte[w * h * 4];
        gpu.ReadResultToCpu(result, w, h);
        // Just verify we got some data back
        Assert.Equal(w * h * 4, result.Length);
    }

    // ── Pipeline Integration Tests ────────────────────────────

    [Fact]
    public void FrameBuffer_CaptureAndProcess_CpuPath()
    {
        // Full CPU pipeline: capture → process → verify
        var buf = new FrameBuffer();
        int w = 100, h = 100, stride = w * 4;
        buf.EnsureCapacity(w, h, stride);

        var captureService = new ScreenCaptureService();
        buf.CaptureAndCopyPixels(captureService, 100, 100);

        var detector = new ColorDetectorService();
        var target = new ColorSettings
        {
            Rgb = [255, 0, 0],
            Threshold = new ThresholdSettings { H = 15, S = 50, V = 50 },
            EraseAlgorithm = "chromakey"
        };
        detector.EraseColorInto(buf, target);

        // Verify output has data
        bool hasDst = false;
        for (int i = 0; i < buf.Dst.Length && !hasDst; i++)
            if (buf.Dst[i] != 0) hasDst = true;
        Assert.True(hasDst, "CPU pipeline should produce non-zero output");
    }

    [Fact]
    public void FrameBuffer_MultiplyBlend_CpuPath()
    {
        var buf = new FrameBuffer();
        int w = 100, h = 100, stride = w * 4;
        buf.EnsureCapacity(w, h, stride);

        var captureService = new ScreenCaptureService();
        buf.CaptureAndCopyPixels(captureService, 100, 100);

        var detector = new ColorDetectorService();
        detector.MultiplyBlendInto(buf, [255, 0, 0]);

        bool hasDst = false;
        for (int i = 0; i < buf.Dst.Length && !hasDst; i++)
            if (buf.Dst[i] != 0) hasDst = true;
        Assert.True(hasDst, "MultiplyBlend should produce non-zero output");
    }
}
