using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using MapMode = Vortice.Direct3D11.MapMode;

namespace Veilr.Services;

/// <summary>
/// GPU-accelerated screen capture via DXGI Desktop Duplication.
///
/// - Duplicates every output (monitor) of the adapter, so a region that spans
///   several monitors is composed from each of them.
/// - Keeps the latest full frame of each output in its own texture. When no new
///   frame arrives (static screen), the region is cut from that cache, so moving
///   the window over a static area still shows the correct content.
/// - Recovers automatically from ACCESS_LOST (lock screen, UAC, display mode
///   change, sleep) and from device loss (driver update / TDR).
/// - Regions not covered by this adapter's outputs fall back to GDI.
/// </summary>
public class DxgiCaptureService : IDisposable
{
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);

    private const int SLOT_RETRY_MS = 1000;   // retry a failed output duplication
    private const int REBUILD_RETRY_MS = 250; // rebuild all outputs after ACCESS_LOST
    private const int DEVICE_RETRY_MS = 1000; // recreate the D3D device after loss

    private sealed class OutputSlot
    {
        public int Index;
        public int Left, Top, Right, Bottom;
        public bool Rotated;
        public IDXGIOutputDuplication? Duplication;
        public ID3D11Texture2D? Cache;
        public bool HasFrame;
        public long NextRetryTick;

        public void DropDuplication()
        {
            Duplication?.Dispose();
            Duplication = null;
            HasFrame = false;
        }

        public void Dispose()
        {
            DropDuplication();
            Cache?.Dispose();
            Cache = null;
        }
    }

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIAdapter? _adapter;
    private readonly List<OutputSlot> _slots = new();
    private ID3D11Texture2D? _staging;
    private int _stagingW, _stagingH;

    private bool _initialized, _failed;
    private bool _deviceLost, _rebuildPending;
    private long _nextDeviceRetryTick, _nextRebuildTick;
    private int _currentOutputIndex;
    private readonly ScreenCaptureService _gdiFallback = new();

    public bool IsUsingDxgi => _initialized && !_failed;
    public int CurrentOutputIndex => _currentOutputIndex;
    public int OutputCount => _slots.Count;

    /// <summary>Incremented each time the D3D device is (re)created.
    /// Resources created on an older device must be recreated.</summary>
    public int DeviceGeneration { get; private set; }

    internal ID3D11Device? Device => _device;
    internal ID3D11DeviceContext? Context => _context;

    public bool TryInitialize()
    {
        if (_initialized) return !_failed;
        _initialized = true;

        if (!CreateDevice())
        {
            _failed = true;
            ReleaseDevice();
            return false;
        }
        return _slots.Any(s => s.Duplication != null);
    }

    /// <summary>
    /// Recover lost device / duplications. Call once per frame from the capture
    /// thread, BEFORE using textures that live on <see cref="Device"/>
    /// (check <see cref="DeviceGeneration"/> afterwards).
    /// </summary>
    public void Maintain()
    {
        if (!_initialized || _failed) return;
        long now = Environment.TickCount64;

        if (_deviceLost)
        {
            if (now < _nextDeviceRetryTick) return;
            _nextDeviceRetryTick = now + DEVICE_RETRY_MS;
            ReleaseDevice();
            if (CreateDevice()) _deviceLost = false;
            return;
        }

        if (_rebuildPending && now >= _nextRebuildTick)
        {
            _nextRebuildTick = now + REBUILD_RETRY_MS;
            // A display topology change can leave the adapter's output list stale.
            if (!IsFactoryCurrent()) { MarkDeviceLost(); return; }
            BuildSlots();
            _rebuildPending = false;
        }
    }

    /// <summary>Check whether the D3D device was removed (e.g. after a GPU error).</summary>
    public void CheckDeviceLost()
    {
        try
        {
            if (_device != null && _device.DeviceRemovedReason.Failure)
                MarkDeviceLost();
        }
        catch { MarkDeviceLost(); }
    }

    /// <summary>
    /// Capture directly to a GPU texture (no CPU copy).
    /// gpuTarget must be created on the current <see cref="Device"/>.
    /// Returns false when the region cannot be captured via DXGI (→ use GDI).
    /// </summary>
    internal bool TryCaptureToGpuTexture(int x, int y, int w, int h, ID3D11Texture2D gpuTarget)
    {
        if (!IsUsingDxgi || _deviceLost || _context == null) return false;
        try { return ComposeRegion(x, y, w, h, gpuTarget); }
        catch { CheckDeviceLost(); return false; }
    }

    /// <summary>
    /// Capture via DXGI into a CPU buffer. Returns false → GDI fallback.
    /// </summary>
    public bool TryCaptureRegion(int x, int y, int w, int h, byte[] dst, int stride)
    {
        Maintain();
        if (!IsUsingDxgi || _deviceLost || _context == null || w <= 0 || h <= 0) return false;

        try
        {
            EnsureStaging(w, h);
            if (!ComposeRegion(x, y, w, h, _staging!)) return false;

            var mapped = _context.Map(_staging!, 0, MapMode.Read);
            try
            {
                int srcPitch = (int)mapped.RowPitch;
                int copyBytes = Math.Min(w * 4, stride);
                if (srcPitch == stride)
                    Marshal.Copy(mapped.DataPointer, dst, 0, Math.Min(stride * h, dst.Length));
                else
                    for (int row = 0; row < h; row++)
                        Marshal.Copy(mapped.DataPointer + row * srcPitch, dst, row * stride, copyBytes);
            }
            finally { _context.Unmap(_staging!, 0); }
            return true;
        }
        catch
        {
            CheckDeviceLost();
            return false;
        }
    }

    /// <summary>DXGI capture → FrameBuffer.Src, with GDI fallback.</summary>
    public void CaptureIntoBuffer(FrameBuffer buf, int x, int y)
    {
        if (TryCaptureRegion(x, y, buf.Width, buf.Height, buf.Src, buf.Stride))
            return;

        // GDI fallback (works on any monitor)
        buf.CaptureAndCopyPixels(_gdiFallback, x, y);
    }

    /// <summary>GDI-only capture → FrameBuffer.Src (for regions DXGI can't serve).</summary>
    public void CaptureGdiIntoBuffer(FrameBuffer buf, int x, int y)
        => buf.CaptureAndCopyPixels(_gdiFallback, x, y);

    /// <summary>
    /// Kept for compatibility. All outputs are captured automatically now,
    /// so this only records the selected index.
    /// </summary>
    public void SwitchToOutput(int outputIndex)
    {
        if (outputIndex >= 0 && outputIndex < Math.Max(1, OutputCount))
            _currentOutputIndex = outputIndex;
    }

    /// <summary>Cycle to next output. Returns new output index.</summary>
    public int CycleOutput()
    {
        if (OutputCount <= 1) return 0;
        int next = (_currentOutputIndex + 1) % OutputCount;
        SwitchToOutput(next);
        return next;
    }

    // ── Region composition ────────────────────────────────────

    /// <summary>
    /// Copy screen region (x,y,w,h in desktop coordinates) into target at (0,0),
    /// composing from every output it overlaps. False if any part is not covered.
    /// </summary>
    private bool ComposeRegion(int x, int y, int w, int h, ID3D11Texture2D target)
    {
        if (w <= 0 || h <= 0 || _context == null) return false;
        int rx1 = x + w, ry1 = y + h;
        long covered = 0;

        // Pass 1: refresh caches of overlapping outputs
        foreach (var s in _slots)
        {
            if (!Intersect(s, x, y, rx1, ry1, out _, out _, out _, out _)) continue;
            if (!UpdateSlot(s) || !s.HasFrame) return false;
        }

        // Pass 2: copy each overlapping part into place
        foreach (var s in _slots)
        {
            if (!Intersect(s, x, y, rx1, ry1, out int ix0, out int iy0, out int ix1, out int iy1)) continue;
            _context.CopySubresourceRegion(
                target, 0, (uint)(ix0 - x), (uint)(iy0 - y), 0,
                s.Cache!, 0,
                new Box(ix0 - s.Left, iy0 - s.Top, 0, ix1 - s.Left, iy1 - s.Top, 1));
            covered += (long)(ix1 - ix0) * (iy1 - iy0);
        }

        // Parts outside this adapter's outputs (other GPU, off-screen) → GDI
        return covered >= (long)w * h;
    }

    private static bool Intersect(OutputSlot s, int x0, int y0, int x1, int y1,
        out int ix0, out int iy0, out int ix1, out int iy1)
    {
        ix0 = Math.Max(x0, s.Left); iy0 = Math.Max(y0, s.Top);
        ix1 = Math.Min(x1, s.Right); iy1 = Math.Min(y1, s.Bottom);
        return ix0 < ix1 && iy0 < iy1;
    }

    /// <summary>Pull a new frame (if any) into the slot cache. False = slot unusable now.</summary>
    private bool UpdateSlot(OutputSlot s)
    {
        if (s.Rotated) return false; // desktop image is unrotated; let GDI handle it

        if (s.Duplication == null)
        {
            long now = Environment.TickCount64;
            if (now < s.NextRetryTick) return false;
            s.NextRetryTick = now + SLOT_RETRY_MS;
            CreateDuplication(s);
            if (s.Duplication == null) return false;
        }

        var hr = s.Duplication.AcquireNextFrame(0, out _, out var resource);
        if (hr.Success && resource != null)
        {
            try
            {
                using var tex = resource.QueryInterface<ID3D11Texture2D>();
                EnsureCache(s, tex);
                _context!.CopyResource(s.Cache!, tex);
                s.HasFrame = true;
            }
            finally
            {
                resource.Dispose();
                s.Duplication.ReleaseFrame();
            }
            return true;
        }

        resource?.Dispose();
        if (hr.Code == DXGI_ERROR_WAIT_TIMEOUT)
            return true; // no change since last frame — cache is still valid

        s.DropDuplication();
        s.NextRetryTick = Environment.TickCount64 + SLOT_RETRY_MS;
        if (hr.Code == DXGI_ERROR_ACCESS_LOST)
            _rebuildPending = true; // mode/topology/desktop switch — re-enumerate outputs
        else
            CheckDeviceLost();
        return false;
    }

    private void EnsureCache(OutputSlot s, ID3D11Texture2D frame)
    {
        var fd = frame.Description;
        if (s.Cache != null)
        {
            var cd = s.Cache.Description;
            if (cd.Width == fd.Width && cd.Height == fd.Height && cd.Format == fd.Format) return;
            s.Cache.Dispose();
        }
        s.Cache = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = fd.Width, Height = fd.Height, MipLevels = 1, ArraySize = 1,
            Format = fd.Format, SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.None
        });
    }

    // ── Device / output lifetime ──────────────────────────────

    private bool CreateDevice()
    {
        try
        {
            D3D11CreateDevice(
                null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                Array.Empty<FeatureLevel>(), out _device, out _context);
            if (_device == null || _context == null) return false;

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            _adapter = dxgiDevice.GetAdapter();
            BuildSlots();
            DeviceGeneration++;
            return true;
        }
        catch
        {
            ReleaseDevice();
            return false;
        }
    }

    private void BuildSlots()
    {
        DisposeSlots();
        if (_adapter == null) return;

        for (uint i = 0; ; i++)
        {
            var hr = _adapter.EnumOutputs(i, out var output);
            if (hr.Failure || output == null) break;
            output.Dispose();
            var slot = new OutputSlot { Index = (int)i };
            CreateDuplication(slot);
            _slots.Add(slot);
        }
        if (_currentOutputIndex >= _slots.Count) _currentOutputIndex = 0;
    }

    /// <summary>(Re)read output bounds and create its duplication. Leaves Duplication null on failure.</summary>
    private void CreateDuplication(OutputSlot s)
    {
        s.DropDuplication();
        try
        {
            if (_adapter == null || _device == null) return;
            var hr = _adapter.EnumOutputs((uint)s.Index, out var output);
            if (hr.Failure || output == null) return;

            using (output)
            {
                var desc = output.Description;
                var b = desc.DesktopCoordinates;
                s.Left = b.Left; s.Top = b.Top; s.Right = b.Right; s.Bottom = b.Bottom;
                s.Rotated = desc.Rotation is ModeRotation.Rotate90 or ModeRotation.Rotate180
                    or ModeRotation.Rotate270;
                if (!desc.AttachedToDesktop) return;

                using var output1 = output.QueryInterface<IDXGIOutput1>();
                s.Duplication = output1.DuplicateOutput(_device);
            }
        }
        catch
        {
            // E_ACCESSDENIED on the secure desktop, UNSUPPORTED on hybrid GPUs, etc.
            s.DropDuplication();
        }
    }

    private bool IsFactoryCurrent()
    {
        try
        {
            if (_adapter == null) return false;
            using var factory = _adapter.GetParent<IDXGIFactory1>();
            return factory.IsCurrent;
        }
        catch { return true; } // unknown — try a normal rebuild
    }

    private void MarkDeviceLost()
    {
        if (_deviceLost) return;
        _deviceLost = true;
        _nextDeviceRetryTick = 0; // retry on next Maintain()
    }

    // ── Test hooks (fault injection) ──────────────────────────

    /// <summary>Test only: behave as if every output returned DXGI_ERROR_ACCESS_LOST.</summary>
    internal void SimulateAccessLost()
    {
        foreach (var s in _slots) s.DropDuplication();
        _rebuildPending = true;
        _nextRebuildTick = 0;
    }

    /// <summary>Test only: behave as if the D3D device was removed.</summary>
    internal void SimulateDeviceLost() => MarkDeviceLost();

    private void EnsureStaging(int w, int h)
    {
        if (w == _stagingW && h == _stagingH && _staging != null) return;
        _staging?.Dispose();
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
        });
        _stagingW = w; _stagingH = h;
    }

    private void DisposeSlots()
    {
        foreach (var s in _slots) s.Dispose();
        _slots.Clear();
    }

    private void ReleaseDevice()
    {
        DisposeSlots();
        _staging?.Dispose(); _staging = null; _stagingW = _stagingH = 0;
        _adapter?.Dispose(); _adapter = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose()
    {
        ReleaseDevice();
        GC.SuppressFinalize(this);
    }
}
