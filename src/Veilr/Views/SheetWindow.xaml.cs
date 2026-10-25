using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Veilr.Models;
using Veilr.Services;
using Veilr.ViewModels;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Rectangle = System.Drawing.Rectangle;

namespace Veilr.Views;

public partial class SheetWindow : Window
{
    private readonly SheetViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly ScreenCaptureService _captureService = new();
    private readonly DxgiCaptureService _dxgiCapture = new();
    private readonly ColorDetectorService _detectorService = new();
    private readonly GpuProcessingService _gpuService = new();
    private bool _gpuAvailable;

    // Debounce timer for resize
    private DispatcherTimer? _resizeDebounce;

    // ── Background capture pipeline ──────────────────────────
    private readonly FrameBuffer _front = new();   // UI reads
    private readonly FrameBuffer _back = new();    // Capture thread writes
    private readonly object _swapLock = new();
    private Thread? _captureThread;
    private volatile bool _captureRunning;
    private volatile bool _oneshotRequested;
    private volatile bool _frameReady;
    private WriteableBitmap? _writeableBitmap;
    private nint _hwndCache;

    public SheetWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _viewModel = new SheetViewModel(settingsService);
        DataContext = _viewModel;

        var last = settingsService.Settings.LastSession;
        if (last.Sheets.Count > 0)
        {
            var pos = last.Sheets[0];
            Left = pos.X;
            Top = pos.Y;
            Width = pos.W;
            Height = pos.H;
        }

        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnSizeChanged;

        // Register CompositionTarget.Rendering for smooth UI updates
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwndCache = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // Enable resize from all edges/corners (WindowStyle=None doesn't have default resize handles)
        var source = System.Windows.Interop.HwndSource.FromHwnd(_hwndCache);
        source?.AddHook(WndProc);

        EnsureExcludeFromCapture();
        RequestCapture();
        StartCaptureThreadIfEnabled();
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int RESIZE_BORDER = 6;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            int x = (short)(lParam.ToInt64() & 0xFFFF);
            int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

            GetWindowRect(_hwndCache, out RECT wr);
            int left = wr.Left, top = wr.Top, right = wr.Right, bottom = wr.Bottom;

            bool l = x >= left && x < left + RESIZE_BORDER;
            bool r = x > right - RESIZE_BORDER && x <= right;
            bool t = y >= top && y < top + RESIZE_BORDER;
            bool b = y > bottom - RESIZE_BORDER && y <= bottom;

            if (t && l) { handled = true; return HTTOPLEFT; }
            if (t && r) { handled = true; return HTTOPRIGHT; }
            if (b && l) { handled = true; return HTBOTTOMLEFT; }
            if (b && r) { handled = true; return HTBOTTOMRIGHT; }
            if (l) { handled = true; return HTLEFT; }
            if (r) { handled = true; return HTRIGHT; }
            if (t) { handled = true; return HTTOP; }
            if (b) { handled = true; return HTBOTTOM; }
        }
        return 0;
    }

    // ── CompositionTarget.Rendering: UI update (memcpy only, ~0.3ms) ──
    private void OnRendering(object? sender, EventArgs e)
    {
        // Sync drag position with image update — same frame, no desync
        if (_isDragging)
        {
            GetCursorPos(out var cursor);
            Left = _dragStartLeft + (cursor.X - _dragStartCursor.X) / _dpiScale;
            Top = _dragStartTop + (cursor.Y - _dragStartCursor.Y) / _dpiScale;
        }

        if (!_frameReady) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (_swapLock)
        {
            int w = _front.Width, h = _front.Height;
            if (w <= 0 || h <= 0) return;

            double dpi;
            try { dpi = GetDpiForSystem(); } catch { dpi = 96; }

            if (_writeableBitmap == null
                || _writeableBitmap.PixelWidth != w
                || _writeableBitmap.PixelHeight != h)
            {
                _writeableBitmap = new WriteableBitmap(w, h, dpi, dpi, PixelFormats.Bgra32, null);
                _viewModel.ProcessedImageSource = _writeableBitmap;
            }

            _writeableBitmap.Lock();
            Marshal.Copy(_front.Dst, 0, _writeableBitmap.BackBuffer, _front.ByteCount);
            _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
            _writeableBitmap.Unlock();

        }
        sw.Stop();

        _renderCount++;
        _renderTotalMs += sw.Elapsed.TotalMilliseconds;
    }

    // ── Background capture thread ─────────────────────────────
    private void StartCaptureThreadIfEnabled()
    {
        // Set the flag first: if the previous thread is still alive (Stop was
        // just requested), it keeps running instead of exiting with no successor.
        _captureRunning = true;
        if (_captureThread?.IsAlive == true) return;
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "VeilrCapture",
            Priority = ThreadPriority.BelowNormal
        };
        _captureThread.Start();
    }

    private void StopCaptureThread()
    {
        _captureRunning = false;
        // Thread will exit on its own
    }

    // ── Profiling ──────────────────────────────────────────────
    private readonly System.Diagnostics.Stopwatch _profSw = new();
    private readonly System.Diagnostics.Stopwatch _frameIntervalSw = new();
    private int _profFrameCount;
    private const int PROF_MAX_FRAMES = 50;
    private readonly List<string> _profLog = new();
    private long _renderCount;
    private double _renderTotalMs;

    private void CaptureLoop()
    {
        // Work in physical pixels on this thread. The app itself is System-DPI aware,
        // so on a monitor with a different scale GetWindowRect would return
        // virtualized coordinates that don't match the screen (DXGI/GDI).
        if (SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) == nint.Zero)
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);

        // Initialize DXGI (GPU capture) + high-res timer
        _dxgiCapture.TryInitialize();
        EnsureHighResTimer();

        while (_captureRunning)
        {
            bool autoEnabled = _settingsService.Settings.AutoRefreshEnabled;
            bool shouldCapture = _oneshotRequested || autoEnabled;

            if (!shouldCapture)
            {
                Thread.Sleep(1);
                continue;
            }

            // Recover from ACCESS_LOST / device loss (lock screen, sleep, driver
            // update…), then (re)build GPU resources if the device changed.
            try
            {
                _dxgiCapture.Maintain();
                EnsureGpuForCurrentDevice();
            }
            catch (Exception ex) { AddProfLog($"Maintain ERROR: {ex.Message}"); }

            _oneshotRequested = false;
            _profSw.Restart();

            try
            {
                GetWindowRect(_hwndCache, out RECT wr);
                int x = wr.Left, y = wr.Top;
                int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
                if (w <= 0 || h <= 0) { Thread.Sleep(50); continue; }

                int stride = w * 4;
                _back.EnsureCapacity(w, h, stride);

                var settings = _settingsService.Settings;
                bool isErase;
                try { isErase = _viewModel.IsEraseMode; } catch { isErase = false; }
                bool useGpu = _gpuAvailable && settings.UseGpuProcessing && _gpuService.IsAvailable;

                long t0 = _profSw.ElapsedTicks;
                long t1, t2, t3;

                bool useGpuThisFrame = useGpu;

                if (useGpuThisFrame)
                {
                    try
                    {
                        _back.EnsureCapacity(w, h, stride);
                        _gpuService.EnsureTexturesPublic(w, h);
                        bool gpuCaptured = _dxgiCapture.TryCaptureToGpuTexture(
                            x, y, w, h, _gpuService.GetSrcTexture()!);
                        t1 = _profSw.ElapsedTicks;
                        t2 = t1;

                        if (!gpuCaptured)
                        {
                            // DXGI can't serve this region (rotated monitor, other GPU,
                            // off-screen part, recovering) → GDI capture, GPU processing
                            _back.CaptureX = x;
                            _back.CaptureY = y;
                            _dxgiCapture.CaptureGdiIntoBuffer(_back, x, y);
                            _gpuService.UploadSrcFromCpu(_back.Src, w, h, stride);
                            t1 = _profSw.ElapsedTicks;
                            t2 = t1;
                        }

                        {
                            if (isErase)
                            {
                                switch (settings.TargetColor.EraseAlgorithm)
                                {
                                    case "labmask":
                                        _gpuService.ProcessEraseLabMask(
                                            _gpuService.GetSrcTexture()!, settings.TargetColor, w, h);
                                        break;
                                    case "ycbcr":
                                        _gpuService.ProcessEraseYCbCr(
                                            _gpuService.GetSrcTexture()!, settings.TargetColor, w, h);
                                        break;
                                    default:
                                        _gpuService.ProcessEraseChromaKey(
                                            _gpuService.GetSrcTexture()!, settings.TargetColor, w, h);
                                        break;
                                }
                            }
                            else
                            {
                                _gpuService.ProcessMultiplyBlend(
                                    _gpuService.GetSrcTexture()!, settings.OverlayColor.Rgb, w, h);
                            }
                            _gpuService.ReadResultToCpu(_back.Dst, w, h);
                            t3 = _profSw.ElapsedTicks;
                        }
                    }
                    catch (Exception gpuEx)
                    {
                        // Disabled until the device is recreated (DeviceGeneration changes)
                        _gpuAvailable = false;
                        _dxgiCapture.CheckDeviceLost();
                        AddProfLog($"GPU ERROR (auto-fallback to CPU): {gpuEx.Message}");
                        goto cpuFallback;
                    }
                    goto afterProcess;
                    cpuFallback:
                    _dxgiCapture.CaptureIntoBuffer(_back, x, y);
                    t1 = _profSw.ElapsedTicks; t2 = t1;
                    if (isErase) _detectorService.EraseColorInto(_back, settings.TargetColor);
                    else _detectorService.MultiplyBlendInto(_back, settings.OverlayColor.Rgb);
                    t3 = _profSw.ElapsedTicks;
                    afterProcess:;
                }
                else
                {
                    // CPU path (fallback)
                    _back.CaptureX = x;
                    _back.CaptureY = y;
                    _dxgiCapture.CaptureIntoBuffer(_back, x, y);
                    t1 = _profSw.ElapsedTicks;

                    t2 = t1;
                    if (isErase)
                        _detectorService.EraseColorInto(_back, settings.TargetColor);
                    else
                        _detectorService.MultiplyBlendInto(_back, settings.OverlayColor.Rgb);
                    t3 = _profSw.ElapsedTicks;
                }

                // --- Swap (overwrite front buffer — UI picks up latest) ---
                lock (_swapLock)
                {
                    _front.EnsureCapacity(w, h, stride);
                    Buffer.BlockCopy(_back.Dst, 0, _front.Dst, 0, _back.ByteCount);
                    _front.CaptureX = _back.CaptureX;
                    _front.CaptureY = _back.CaptureY;
                    _frameReady = true;
                }
                long t4 = _profSw.ElapsedTicks;

                // --- Log ---
                double freq = System.Diagnostics.Stopwatch.Frequency;
                if (_profFrameCount < PROF_MAX_FRAMES)
                {
                    double capMs = (t1 - t0) / freq * 1000;
                    double procMs = (t3 - t2) / freq * 1000;
                    double swapMs = (t4 - t3) / freq * 1000;
                    double totalMs = (t4 - t0) / freq * 1000;
                    double intervalMs = _frameIntervalSw.Elapsed.TotalMilliseconds;
                    _frameIntervalSw.Restart();
                    bool useGpuLog = useGpuThisFrame;
                    _profLog.Add($"Frame {_profFrameCount:D3}  " +
                        $"Cap:{capMs,5:F1}ms  Proc:{procMs,5:F1}ms  " +
                        $"Total:{totalMs,5:F1}ms  Interval:{intervalMs,6:F1}ms  " +
                        $"({(intervalMs > 0 ? 1000/intervalMs : 0),5:F0}fps) " +
                        $"GPU:{useGpuLog}");
                    _profFrameCount++;

                    if (_profFrameCount == PROF_MAX_FRAMES)
                    {
                        if (_gpuService.DebugLog.Count > 0)
                            _profLog.AddRange(_gpuService.DebugLog);
                        FlushProfileLog(w, h);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_profFrameCount < PROF_MAX_FRAMES)
                    _profLog.Add($"ERROR: {ex.Message}");
            }

            if (!autoEnabled) continue;

            int interval = _settingsService.Settings.UpdateIntervalMs;
            if (interval > 1)
                PreciseSleep(interval, _profSw);
            // interval <= 1: no sleep, run at maximum speed
        }
    }

    private int _gpuDeviceGeneration = -1;

    /// <summary>Initialize GPU shaders on the current capture device (once per device).</summary>
    private void EnsureGpuForCurrentDevice()
    {
        if (!_settingsService.Settings.UseGpuProcessing) return;
        if (_gpuDeviceGeneration == _dxgiCapture.DeviceGeneration) return;
        if (!_dxgiCapture.IsUsingDxgi || _dxgiCapture.Device == null || _dxgiCapture.Context == null)
            return;

        _gpuDeviceGeneration = _dxgiCapture.DeviceGeneration;
        try
        {
            _gpuAvailable = _gpuService.Initialize(_dxgiCapture.Device, _dxgiCapture.Context);
            AddProfLog($"GPU init (device #{_gpuDeviceGeneration}): {_gpuAvailable}" +
                (_gpuAvailable ? "" : $" error: {_gpuService.InitError}"));
        }
        catch (Exception ex)
        {
            _gpuAvailable = false;
            AddProfLog($"GPU init FAILED: {ex.Message}");
        }
    }

    private void AddProfLog(string line)
    {
        if (_profLog.Count < 500) _profLog.Add(line); // bounded for long sessions
    }

    private void FlushProfileLog(int w, int h)
    {
        try
        {
            var lines = new List<string>
            {
                "=== Veilr Capture Profile ===",
                $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Window: {w}x{h}",
                $"WDA_EXCLUDEFROMCAPTURE: {_affinitySet}",
                $"DXGI Desktop Duplication: {_dxgiCapture.IsUsingDxgi}",
                $"GPU Compute Shaders: {_gpuAvailable} (requested: {_settingsService.Settings.UseGpuProcessing})",
                $"AutoRefresh: {_settingsService.Settings.AutoRefreshEnabled}",
                $"Interval: {_settingsService.Settings.UpdateIntervalMs}ms",
                $"Render frames: {_renderCount}, avg: {(_renderCount > 0 ? _renderTotalMs / _renderCount : 0):F2}ms",
                ""
            };
            lines.AddRange(_profLog);

            string logPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "capture-profile.log");
            File.WriteAllLines(logPath, lines);
        }
        catch { /* ignore */ }
    }

    /// <summary>Request a single frame capture (non-blocking).</summary>
    private void RequestCapture()
    {
        _oneshotRequested = true;
        // Ensure capture thread is running
        if (_captureThread?.IsAlive != true)
            StartCaptureThreadIfEnabled();
    }

    // ── Visibility ────────────────────────────────────────────
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            RequestCapture();
            StartCaptureThreadIfEnabled();
        }
        else
        {
            StopCaptureThread();
        }
    }

    // ── WDA_EXCLUDEFROMCAPTURE ────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private bool _affinitySet;

    private void EnsureExcludeFromCapture()
    {
        if (_affinitySet || _hwndCache == nint.Zero) return;
        _affinitySet = SetWindowDisplayAffinity(_hwndCache, WDA_EXCLUDEFROMCAPTURE);
    }

    // ── Triggers ───────────────────────────────────────────────

    // ── Non-blocking drag (position update synced to OnRendering) ──
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private bool _isDragging;
    private POINT _dragStartCursor;
    private double _dragStartLeft, _dragStartTop;
    private double _dpiScale = 1.0;

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _isDragging = true;
        GetCursorPos(out _dragStartCursor);
        _dragStartLeft = Left;
        _dragStartTop = Top;
        try { _dpiScale = GetDpiForSystem() / 96.0; } catch { _dpiScale = 1.0; }
        ((UIElement)sender).CaptureMouse();
    }

    private void DragBar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // MouseMove records nothing — position update happens in OnRendering
        // to stay synchronized with image updates
    }

    private void DragBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        // Final position snap
        GetCursorPos(out var cursor);
        Left = _dragStartLeft + (cursor.X - _dragStartCursor.X) / _dpiScale;
        Top = _dragStartTop + (cursor.Y - _dragStartCursor.Y) / _dpiScale;
        RequestCapture();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeDebounce?.Stop();
        _resizeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            RequestCapture();
        };
        _resizeDebounce.Start();
    }


    private void BtnMode_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleMode();
        RequestCapture();
    }

    private void BtnFullScreen_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleFullScreen(this);
        RequestCapture();
    }

    // ── Export ─────────────────────────────────────────────────

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            GetWindowRect(_hwndCache, out RECT ewr);
            double edpi;
            try { edpi = GetDpiForSystem() / 96.0; } catch { edpi = 1.0; }
            int x = ewr.Left;
            int y = ewr.Top + (int)(24 * edpi);
            int w = ewr.Right - ewr.Left;
            int h = (ewr.Bottom - ewr.Top) - (int)(24 * edpi) - (int)(32 * edpi);
            if (w <= 0 || h <= 0) return;

            using var captured = _captureService.CaptureRegion(x, y, w, h);
            using var processed = _detectorService.EraseColor(captured, _settingsService.Settings.TargetColor);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dlg = new SaveFileDialog
            {
                FileName = $"Veilr_{timestamp}",
                Filter = "PNG|*.png|JPEG|*.jpg|BMP|*.bmp",
                FilterIndex = _settingsService.Settings.Export.Format switch
                {
                    "jpeg" => 2, "bmp" => 3, _ => 1
                },
                InitialDirectory = string.IsNullOrEmpty(_settingsService.Settings.Export.SavePath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : _settingsService.Settings.Export.SavePath
            };

            if (dlg.ShowDialog() == true)
            {
                var format = Path.GetExtension(dlg.FileName).ToLower() switch
                {
                    ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                    ".bmp" => ImageFormat.Bmp,
                    _ => ImageFormat.Png
                };
                processed.Save(dlg.FileName, format);
                _settingsService.Settings.Export.SavePath = Path.GetDirectoryName(dlg.FileName) ?? "";
                _settingsService.Save();
            }
        }
        catch { /* swallow */ }
    }

    // ── Other handlers ────────────────────────────────────────

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        StopCaptureThread();
        var settingsWindow = new SettingsWindow(_settingsService);
        settingsWindow.Owner = this;
        settingsWindow.Topmost = true;
        settingsWindow.ShowDialog();
        _viewModel.RefreshFromSettings();
        RequestCapture();
        StartCaptureThreadIfEnabled();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        SavePosition();
        Hide();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.IsFullScreen)
        {
            _viewModel.ToggleFullScreen(this);
            RequestCapture();
            e.Handled = true;
        }
        if (e.Key == Key.F5 || e.Key == Key.Space)
        {
            RequestCapture();
            e.Handled = true;
        }
    }

    private void SavePosition()
    {
        _settingsService.Settings.LastSession.Sheets =
        [
            new() { X = Left, Y = Top, W = Width, H = Height }
        ];
        _settingsService.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopCaptureThread();
        ReleaseHighResTimer();
        _gpuService.Dispose();
        _dxgiCapture.Dispose();
        CompositionTarget.Rendering -= OnRendering;
        SavePosition();
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern int GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3;
    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    // High-resolution timer for smooth frame pacing
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uMilliseconds);
    private bool _timerPeriodSet;

    private void EnsureHighResTimer()
    {
        if (_timerPeriodSet) return;
        timeBeginPeriod(1); // Set system timer resolution to 1ms
        _timerPeriodSet = true;
    }

    private void ReleaseHighResTimer()
    {
        if (!_timerPeriodSet) return;
        timeEndPeriod(1);
        _timerPeriodSet = false;
    }

    /// <summary>
    /// Accurate sleep: Thread.Sleep for most of the time, spin-wait for the last 2ms.
    /// Windows Thread.Sleep has ~1-2ms jitter even with timeBeginPeriod(1).
    /// </summary>
    private static void PreciseSleep(int targetMs, System.Diagnostics.Stopwatch sw)
    {
        long targetTicks = targetMs * System.Diagnostics.Stopwatch.Frequency / 1000;
        long sleepUntil = targetTicks - 2 * System.Diagnostics.Stopwatch.Frequency / 1000; // stop sleep 2ms early

        // Coarse sleep
        while (sw.ElapsedTicks < sleepUntil)
            Thread.Sleep(1);

        // Spin-wait for remaining ~2ms (precise)
        while (sw.ElapsedTicks < targetTicks)
            Thread.SpinWait(10);
    }
}
