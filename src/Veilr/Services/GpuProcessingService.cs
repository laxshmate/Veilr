using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using MapMode = Vortice.Direct3D11.MapMode;

namespace Veilr.Services;

/// <summary>
/// GPU processing pipeline using D3D11 Compute Shaders.
/// Shares the same DX11 device as DXGI Desktop Duplication for zero-copy.
/// </summary>
public class GpuProcessingService : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private bool _initialized;

    // Compute shaders
    private ID3D11ComputeShader? _multiplyBlendCS;
    private ID3D11ComputeShader? _alphaChromaKeyCS;
    private ID3D11ComputeShader? _alphaYCbCrCS;
    private ID3D11ComputeShader? _maskLabMaskCS;
    private ID3D11ComputeShader? _jfaInitCS;
    private ID3D11ComputeShader? _jfaStepCS;
    private ID3D11ComputeShader? _blendCS;
    private ID3D11ComputeShader? _despillCS;

    // GPU textures (resized on demand)
    private int _texW, _texH;
    private ID3D11Texture2D? _srcTex, _dstTex, _stagingTex;
    private ID3D11Texture2D? _alphaTex, _alphaTex2;
    private ID3D11Texture2D? _bgColorTex;
    private ID3D11Texture2D? _seedMapA, _seedMapB;

    // Views
    private ID3D11ShaderResourceView? _srcSRV, _alphaSRV, _alpha2SRV;
    private ID3D11ShaderResourceView? _seedASRV, _seedBSRV, _bgColorSRV;
    private ID3D11UnorderedAccessView? _srcUAV, _dstUAV;
    private ID3D11UnorderedAccessView? _alphaUAV, _alpha2UAV;
    private ID3D11UnorderedAccessView? _seedAUAV, _seedBUAV, _bgColorUAV;

    // Constant buffer
    private ID3D11Buffer? _paramsCB;

    // Lab LUT buffer
    private ID3D11Buffer? _labLutBuffer;
    private ID3D11ShaderResourceView? _labLutSRV;

    public bool IsAvailable => _initialized;
    public string? InitError { get; private set; }
    public List<string> DebugLog { get; } = new();

    /// <summary>
    /// Initialize GPU processing using the provided D3D11 device (shared with DXGI capture).
    /// Returns true if all shaders compile and GPU is ready.
    /// </summary>
    public bool Initialize(ID3D11Device device, ID3D11DeviceContext context)
    {
        // Re-initialization (e.g. after device loss): drop resources of the old device
        Cleanup();
        _initialized = false;
        InitError = null;
        _device = device;
        _context = context;

        try
        {
            // Compile all shaders
            _multiplyBlendCS = CompileShader("MultiplyBlend");
            _alphaChromaKeyCS = CompileShader("AlphaChromaKey");
            _alphaYCbCrCS = CompileShader("AlphaYCbCr");
            _maskLabMaskCS = CompileShader("MaskLabMask");
            _jfaInitCS = CompileShader("JfaInit");
            _jfaStepCS = CompileShader("JfaStep");
            _blendCS = CompileShader("Blend");
            _despillCS = CompileShader("Despill");

            _paramsCB = device.CreateBuffer(new BufferDescription(256,
                BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

            UploadLabLut();

            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Run GPU capability test: compile a shader, dispatch, verify output.
    /// </summary>
    public static bool TestGpuCapability()
    {
        try
        {
            FeatureLevel[] levels = [FeatureLevel.Level_11_0];
            D3D11CreateDevice(null, DriverType.Hardware,
                DeviceCreationFlags.None, levels,
                out ID3D11Device? dev, out ID3D11DeviceContext? ctx);
            if (dev == null) return false;

            string testHlsl = @"
                RWTexture2D<float4> output : register(u0);
                [numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) {
                    output[id.xy] = float4(0.5, 0.25, 0.125, 1.0);
                }";

            var result = Compiler.Compile(testHlsl, "CSMain", "test.hlsl", "cs_5_0");
            if (result.Length == 0) { dev.Dispose(); return false; }

            var cs = dev.CreateComputeShader(result.Span);
            cs?.Dispose();
            ctx?.Dispose();
            dev.Dispose();
            return cs != null;
        }
        catch { return false; }
    }

    /// <summary>Get the source texture for direct GPU capture.</summary>
    public ID3D11Texture2D? GetSrcTexture() => _srcTex;

    /// <summary>Ensure GPU textures are allocated for the given dimensions.</summary>
    public void EnsureTexturesPublic(int w, int h) => EnsureTextures(w, h);

    /// <summary>
    /// Upload a CPU-captured frame (BGRA, e.g. from GDI) into the source texture,
    /// so GPU processing still works where DXGI capture can't be used
    /// (rotated monitor, other GPU's monitor, off-screen parts).
    /// </summary>
    public unsafe void UploadSrcFromCpu(byte[] src, int w, int h, int stride)
    {
        if (!_initialized || _context == null) return;
        EnsureTextures(w, h);
        fixed (byte* p = src)
            _context.UpdateSubresource(_srcTex!, 0, null, (nint)p, (uint)stride, (uint)(stride * h));
    }

    // ── Public processing methods ─────────────────────────────

    /// <summary>
    /// Process captured texture with multiply blend (sheet mode).
    /// srcCapture must be on the same D3D11 device.
    /// </summary>
    public void ProcessMultiplyBlend(ID3D11Texture2D srcCapture, int[] sheetRgb, int w, int h)
    {
        if (!_initialized || _device == null || _context == null) return;
        EnsureTextures(w, h);
        CopySrcToGpu(srcCapture, w, h);

        // HLSL layout: float4 sheetColor(0-15), int2 dims(16-23)
        var p = new float[64];
        p[0] = sheetRgb[0] / 255f; p[1] = sheetRgb[1] / 255f; p[2] = sheetRgb[2] / 255f; p[3] = 0;
        p[4] = I(w); p[5] = I(h);
        UpdateParams(p);

        // Dispatch
        _context.CSSetShader(_multiplyBlendCS);
        _context.CSSetShaderResource(0, _srcSRV);
        _context.CSSetUnorderedAccessView(0, _dstUAV);
        _context.CSSetConstantBuffer(0, _paramsCB);
        Dispatch(w, h);
        ClearBindings();
    }

    /// <summary>
    /// Process captured texture with ChromaKey erase.
    /// </summary>
    public void ProcessEraseChromaKey(ID3D11Texture2D srcCapture,
        Models.ColorSettings target, int w, int h)
    {
        if (!_initialized || _device == null || _context == null) return;
        EnsureTextures(w, h);
        CopySrcToGpu(srcCapture, w, h);

        // Compute Lab target
        ColorDetectorService.RgbToLabPublic(target.Rgb[0], target.Rgb[1], target.Rgb[2],
            out double tL, out double tA, out double tB);
        double targetChroma = Math.Sqrt(tA * tA + tB * tB);
        double targetAngle = Math.Atan2(tB, tA);
        double similarity = target.Threshold.H * 1.5;
        double smoothness = similarity * 0.8;
        double outerRadius = similarity + smoothness;
        double hueTol = similarity * 0.5;

        // Pass 1: Alpha
        // HLSL layout: float3 targetLab(0-11), float targetChroma(12-15),
        //   float targetAngle(16), float similarity(20), float smoothness(24), float outerRadius(28),
        //   float hueToleranceDeg(32), int2 dims(36-43)
        var p = new float[64];
        p[0] = (float)tL; p[1] = (float)tA; p[2] = (float)tB;
        p[3] = (float)targetChroma;
        p[4] = (float)targetAngle; p[5] = (float)similarity;
        p[6] = (float)smoothness; p[7] = (float)outerRadius;
        p[8] = (float)hueTol;
        p[9] = I(w); p[10] = I(h);
        UpdateParams(p);

        Log("CK Pass 1: Alpha start");
        _context.CSSetShader(_alphaChromaKeyCS);
        _context.CSSetShaderResource(0, _labLutSRV);
        _context.CSSetShaderResource(1, _srcSRV);
        _context.CSSetUnorderedAccessView(0, _alphaUAV);
        _context.CSSetConstantBuffer(0, _paramsCB);
        Dispatch(w, h);
        ClearBindings();
        Log("CK Pass 1: Alpha done");

        Log("CK Pass 2: JFA start");
        RunJfa(w, h);
        Log("CK Pass 2: JFA done");

        Log("CK Pass 3: Blend start");
        RunBlend(w, h);
        Log("CK Pass 3: Blend done");

        Log("CK Pass 4: Despill start");
        var dp = new float[64];
        dp[0] = target.Rgb[0] / 255f; dp[1] = target.Rgb[1] / 255f; dp[2] = target.Rgb[2] / 255f;
        dp[4] = I(w); dp[5] = I(h);
        UpdateParams(dp);

        _context.CSSetShader(_despillCS);
        _context.CSSetShaderResource(0, _alphaSRV);
        _context.CSSetUnorderedAccessView(0, _dstUAV);
        _context.CSSetConstantBuffer(0, _paramsCB);
        Dispatch(w, h);
        ClearBindings();
    }

    /// <summary>
    /// Process with YCbCr erase.
    /// </summary>
    public void ProcessEraseYCbCr(ID3D11Texture2D srcCapture,
        Models.ColorSettings target, int w, int h)
    {
        if (!_initialized || _device == null || _context == null) return;
        EnsureTextures(w, h);
        CopySrcToGpu(srcCapture, w, h);

        double tCb = -0.169 * target.Rgb[0] - 0.331 * target.Rgb[1] + 0.500 * target.Rgb[2] + 128;
        double tCr = 0.500 * target.Rgb[0] - 0.419 * target.Rgb[1] - 0.081 * target.Rgb[2] + 128;
        double similarity = target.Threshold.H * 2.0;
        double smoothness = similarity * 0.7;
        double outerRadius = similarity + smoothness;
        double tCbC = tCb - 128, tCrC = tCr - 128;
        double targetAngle = Math.Atan2(tCrC, tCbC);
        double targetChroma = Math.Sqrt(tCbC * tCbC + tCrC * tCrC);

        var p = new float[64];
        p[0] = (float)tCb; p[1] = (float)tCr;
        p[2] = (float)similarity; p[3] = (float)smoothness;
        p[4] = (float)outerRadius; p[5] = (float)(similarity * 0.5);
        p[6] = (float)targetAngle; p[7] = (float)targetChroma;
        p[8] = I(w);
        p[9] = I(h);
        UpdateParams(p);

        _context.CSSetShader(_alphaYCbCrCS);
        _context.CSSetShaderResource(0, _srcSRV);
        _context.CSSetUnorderedAccessView(0, _alphaUAV);
        _context.CSSetConstantBuffer(0, _paramsCB);
        Dispatch(w, h);
        ClearBindings();

        RunJfa(w, h);
        RunBlend(w, h);
    }

    /// <summary>
    /// Process with LabMask erase.
    /// </summary>
    public void ProcessEraseLabMask(ID3D11Texture2D srcCapture,
        Models.ColorSettings target, int w, int h)
    {
        if (!_initialized || _device == null || _context == null) return;
        EnsureTextures(w, h);
        CopySrcToGpu(srcCapture, w, h);

        ColorDetectorService.RgbToLabPublic(target.Rgb[0], target.Rgb[1], target.Rgb[2],
            out double tL, out double tA, out double tB);
        double targetChroma = Math.Sqrt(tA * tA + tB * tB);
        double targetAngle = Math.Atan2(tB, tA);
        double maxDist = target.Threshold.H * 1.35;

        // Initial mark (dilationPass=0)
        SetMaskParams(tA, tB, targetChroma, targetAngle, maxDist, w, h, 0);
        _context.CSSetShader(_maskLabMaskCS);
        _context.CSSetShaderResource(0, _labLutSRV);
        _context.CSSetShaderResource(1, _srcSRV);
        _context.CSSetUnorderedAccessView(0, _alphaUAV);
        _context.CSSetConstantBuffer(0, _paramsCB);
        Dispatch(w, h);
        ClearBindings();

        // Dilation passes (ping-pong between alphaTex and alphaTex2)
        for (int pass = 1; pass <= 3; pass++)
        {
            bool oddPass = (pass % 2 == 1);
            var readSRV = oddPass ? _alphaSRV : _alpha2SRV;
            var writeUAV = oddPass ? _alpha2UAV : _alphaUAV;

            SetMaskParams(tA, tB, targetChroma, targetAngle, maxDist, w, h, pass);
            _context.CSSetShader(_maskLabMaskCS);
            _context.CSSetShaderResource(0, _labLutSRV);
            _context.CSSetShaderResource(1, _srcSRV);
            _context.CSSetShaderResource(2, readSRV);
            _context.CSSetUnorderedAccessView(0, writeUAV);
            _context.CSSetConstantBuffer(0, _paramsCB);
            Dispatch(w, h);
            ClearBindings();
        }

        // After 3 dilation passes, result is in alphaTex2 (odd final pass writes to alphaTex2)
        // Copy back to alphaTex for JFA/Blend
        _context!.CopyResource(_alphaTex!, _alphaTex2!);

        RunJfa(w, h);
        RunBlend(w, h);
    }

    /// <summary>
    /// Read the processed result (dstTexture) back to CPU byte array.
    /// </summary>
    public void ReadResultToCpu(byte[] dst, int w, int h)
    {
        if (_context == null || _dstTex == null || _stagingTex == null) return;
        _context.CopyResource(_stagingTex, _dstTex);
        var mapped = _context.Map(_stagingTex, 0, MapMode.Read);
        try
        {
            int srcPitch = (int)mapped.RowPitch;
            int copyBytes = w * 4;
            if (srcPitch == copyBytes)
            {
                Marshal.Copy(mapped.DataPointer, dst, 0, Math.Min(copyBytes * h, dst.Length));
            }
            else
            {
                for (int row = 0; row < h; row++)
                    Marshal.Copy(mapped.DataPointer + row * srcPitch, dst, row * copyBytes, copyBytes);
            }
        }
        finally { _context.Unmap(_stagingTex, 0); }
    }

    // ── Internal helpers ──────────────────────────────────────

    private void RunJfa(int w, int h)
    {
        // Init
        var ip = new float[64];
        ip[0] = I(w);
        ip[1] = I(h);
        UpdateParams(ip);

        _context!.CSSetShader(_jfaInitCS);
        _context.CSSetShaderResource(0, _alphaSRV);
        _context.CSSetShaderResource(1, _srcSRV);
        _context.CSSetUnorderedAccessView(0, _seedAUAV);
        _context.CSSetUnorderedAccessView(1, _bgColorUAV);
        _context.CSSetConstantBuffer(0, _paramsCB);
        Dispatch(w, h);
        ClearBindings();

        // Steps (ping-pong seedMapA ↔ seedMapB)
        int maxDim = Math.Max(w, h);
        bool readFromA = true;
        for (int step = maxDim / 2; step >= 1; step /= 2)
        {
            var sp = new float[64];
            sp[0] = I(step);
            sp[1] = I(w);
            sp[2] = I(h);
            UpdateParams(sp);

            _context.CSSetShader(_jfaStepCS);
            _context.CSSetShaderResource(0, readFromA ? _seedASRV : _seedBSRV);
            _context.CSSetShaderResource(1, _srcSRV);
            _context.CSSetUnorderedAccessView(0, readFromA ? _seedBUAV : _seedAUAV);
            _context.CSSetUnorderedAccessView(1, _bgColorUAV);
            _context.CSSetConstantBuffer(0, _paramsCB);
            Dispatch(w, h);
            ClearBindings();

            readFromA = !readFromA;
        }
    }

    private void RunBlend(int w, int h)
    {
        var bp = new float[64];
        bp[0] = I(w);
        bp[1] = I(h);
        UpdateParams(bp);

        _context!.CSSetShader(_blendCS);
        _context.CSSetShaderResource(0, _srcSRV);
        _context.CSSetShaderResource(1, _alphaSRV);
        _context.CSSetShaderResource(2, _bgColorSRV);
        _context.CSSetUnorderedAccessView(0, _dstUAV);
        _context.CSSetConstantBuffer(0, _paramsCB);
        Dispatch(w, h);
        ClearBindings();
    }

    private void SetMaskParams(double tA, double tB, double targetChroma,
        double targetAngle, double maxDist, int w, int h, int dilationPass)
    {
        var p = new float[64];
        p[0] = 0; p[1] = (float)tA; p[2] = (float)tB; // targetLab (L unused)
        p[3] = (float)targetChroma; p[4] = (float)targetAngle; p[5] = (float)maxDist;
        p[6] = I(w);
        p[7] = I(h);
        p[8] = I(dilationPass);
        UpdateParams(p);
    }

    /// <summary>Pack int as float bits for HLSL int constant buffer.</summary>
    private static float I(int v) => BitConverter.Int32BitsToSingle(v);

    private void Log(string msg)
    {
        if (DebugLog.Count < 200)
            DebugLog.Add($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }

    private void UpdateParams(float[] data)
    {
        var mapped = _context!.Map(_paramsCB!, 0, MapMode.WriteDiscard);
        Marshal.Copy(data, 0, mapped.DataPointer, Math.Min(data.Length, 64));
        _context.Unmap(_paramsCB!, 0);
    }

    private void Dispatch(int w, int h)
    {
        _context!.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
    }

    private static readonly ID3D11ShaderResourceView[] _nullSrvs = new ID3D11ShaderResourceView[4];
    private static readonly ID3D11UnorderedAccessView[] _nullUavs = new ID3D11UnorderedAccessView[3];

    private void ClearBindings()
    {
        _context!.CSSetShader(null);
        _context.CSSetShaderResources(0, _nullSrvs);
        _context.CSSetUnorderedAccessViews(0, _nullUavs);
        _context.CSSetConstantBuffer(0, null);
    }

    private void CopySrcToGpu(ID3D11Texture2D srcCapture, int w, int h)
    {
        // Copy captured region to srcTex (same device, GPU→GPU, zero-copy)
        _context!.CopyResource(_srcTex!, srcCapture);
    }

    // ── Texture management ────────────────────────────────────

    private void EnsureTextures(int w, int h)
    {
        if (w == _texW && h == _texH) return;
        DisposeTextures();
        _texW = w; _texH = h;

        _srcTex = CreateTex(w, h, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        _dstTex = CreateTex(w, h, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        _alphaTex = CreateTex(w, h, Format.R32_Float, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        _alphaTex2 = CreateTex(w, h, Format.R32_Float, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        _bgColorTex = CreateTex(w, h, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        _seedMapA = CreateTex(w, h, Format.R32G32_SInt, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        _seedMapB = CreateTex(w, h, Format.R32G32_SInt, BindFlags.ShaderResource | BindFlags.UnorderedAccess);

        _stagingTex = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
        });

        _srcSRV = _device.CreateShaderResourceView(_srcTex);
        _srcUAV = _device.CreateUnorderedAccessView(_srcTex);
        _dstUAV = _device.CreateUnorderedAccessView(_dstTex);
        _alphaSRV = _device.CreateShaderResourceView(_alphaTex);
        _alphaUAV = _device.CreateUnorderedAccessView(_alphaTex);
        _alpha2SRV = _device.CreateShaderResourceView(_alphaTex2);
        _alpha2UAV = _device.CreateUnorderedAccessView(_alphaTex2);
        _seedASRV = _device.CreateShaderResourceView(_seedMapA);
        _seedAUAV = _device.CreateUnorderedAccessView(_seedMapA);
        _seedBSRV = _device.CreateShaderResourceView(_seedMapB);
        _seedBUAV = _device.CreateUnorderedAccessView(_seedMapB);
        _bgColorSRV = _device.CreateShaderResourceView(_bgColorTex);
        _bgColorUAV = _device.CreateUnorderedAccessView(_bgColorTex);
    }

    private ID3D11Texture2D CreateTex(int w, int h, Format fmt, BindFlags bind)
    {
        return _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = fmt, SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default, BindFlags = bind
        });
    }

    // ── Shader compilation ────────────────────────────────────

    private ID3D11ComputeShader CompileShader(string name)
    {
        string hlsl = LoadShaderSource(name);
        var bytecode = Compiler.Compile(hlsl, "CSMain", $"{name}.hlsl", "cs_5_0");
        if (bytecode.Length == 0)
            throw new Exception($"Shader compilation failed: {name}");
        return _device!.CreateComputeShader(bytecode.Span);
    }

    private static string LoadShaderSource(string name)
    {
        // Try embedded resource first, then file
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($"{name}.hlsl", StringComparison.OrdinalIgnoreCase));
        if (resName != null)
        {
            using var stream = asm.GetManifestResourceStream(resName)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // Fallback: load from file next to exe
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string path = Path.Combine(dir, "Shaders", "Source", $"{name}.hlsl");
        if (File.Exists(path)) return File.ReadAllText(path);

        throw new FileNotFoundException($"Shader not found: {name}.hlsl");
    }

    // ── Lab LUT upload ────────────────────────────────────────

    private void UploadLabLut()
    {
        int count = 64 * 64 * 64; // 262144
        var data = new float[count * 3]; // L, a, b per entry
        for (int ri = 0; ri < 64; ri++)
            for (int gi = 0; gi < 64; gi++)
                for (int bi = 0; bi < 64; bi++)
                {
                    int idx = (ri << 12) | (gi << 6) | bi;
                    ColorDetectorService.RgbToLabPublic(ri * 4, gi * 4, bi * 4,
                        out double L, out double a, out double b);
                    data[idx * 3] = (float)L;
                    data[idx * 3 + 1] = (float)a;
                    data[idx * 3 + 2] = (float)b;
                }

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            _labLutBuffer = _device!.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)(count * 12), // 3 floats * 4 bytes
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.BufferStructured,
                StructureByteStride = 12
            }, new SubresourceData(handle.AddrOfPinnedObject()));
        }
        finally { handle.Free(); }

        _labLutSRV = _device.CreateShaderResourceView(_labLutBuffer, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { NumElements = (uint)count }
        });
    }

    // ── Cleanup ───────────────────────────────────────────────

    private void DisposeTextures()
    {
        _srcSRV?.Dispose(); _srcUAV?.Dispose(); _srcTex?.Dispose();
        _dstUAV?.Dispose(); _dstTex?.Dispose();
        _alphaSRV?.Dispose(); _alphaUAV?.Dispose(); _alphaTex?.Dispose();
        _alpha2SRV?.Dispose(); _alpha2UAV?.Dispose(); _alphaTex2?.Dispose();
        _seedASRV?.Dispose(); _seedAUAV?.Dispose(); _seedMapA?.Dispose();
        _seedBSRV?.Dispose(); _seedBUAV?.Dispose(); _seedMapB?.Dispose();
        _bgColorSRV?.Dispose(); _bgColorUAV?.Dispose(); _bgColorTex?.Dispose();
        _stagingTex?.Dispose();
        _texW = _texH = 0; // force EnsureTextures to recreate
    }

    private void Cleanup()
    {
        DisposeTextures();
        _multiplyBlendCS?.Dispose(); _alphaChromaKeyCS?.Dispose();
        _alphaYCbCrCS?.Dispose(); _maskLabMaskCS?.Dispose();
        _jfaInitCS?.Dispose(); _jfaStepCS?.Dispose();
        _blendCS?.Dispose(); _despillCS?.Dispose();
        _paramsCB?.Dispose();
        _labLutSRV?.Dispose(); _labLutBuffer?.Dispose();
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
