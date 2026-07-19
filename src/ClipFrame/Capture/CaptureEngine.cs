using System.Drawing;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using ClipFrame.Native;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClipFrame.Capture;

/// <summary>
/// Captures a whole monitor with Windows.Graphics.Capture and crops each frame
/// to the shared region. Consumers pull the latest BGRA frame via
/// <see cref="TryGetFrame"/> (pull model — no cross-thread WPF marshaling).
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private readonly object _sync = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _rtDevice;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;

    private IntPtr _monitor;
    private Rectangle _monitorRect;   // physical px, screen coords
    private Rectangle _region;        // physical px, screen coords

    private byte[] _latest = Array.Empty<byte>();
    private int _latestWidth;
    private int _latestHeight;
    private long _frameSeq;           // increments per new frame
    private long _consumedSeq;        // last sequence returned to a consumer
    private bool _frozen;

    // Frame-rate limiting: WGC delivers at the compositor rate; we drop frames
    // that arrive sooner than the target interval to cut CPU/GPU load (spec §6).
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastProcessedMs = long.MinValue;
    private int _targetFps = 15;
    private double _minIntervalMs = 1000.0 / 15;

    private bool _disposed;

    /// <summary>Total frames processed (produced) since start — for diagnostics.</summary>
    public long FramesProduced { get { lock (_sync) return _frameSeq; } }

    /// <summary>Target capture rate in fps (1–60). Frames beyond this are dropped.</summary>
    public int TargetFps
    {
        get => _targetFps;
        set
        {
            _targetFps = Math.Clamp(value, 1, 60);
            _minIntervalMs = 1000.0 / _targetFps;
        }
    }

    public void Start(Rectangle region)
    {
        lock (_sync)
        {
            EnsureDevice();
            _region = Sanitize(region);
            BuildPipelineForRegion(_region);
        }
    }

    public void Freeze() => _frozen = true;

    public void Unfreeze() => _frozen = false;

    /// <summary>Update the crop region; rebuilds capture if the monitor changed.</summary>
    public void UpdateRegion(Rectangle region)
    {
        lock (_sync)
        {
            if (_disposed) return;
            region = Sanitize(region);
            var (hmon, monRect) = NativeMethods.GetMonitorForRegion(region);
            if (hmon != _monitor)
            {
                _region = region;
                BuildPipelineForRegion(region);
            }
            else
            {
                _region = region;
                EnsureStaging(region.Width, region.Height);
            }
        }
    }

    private void EnsureDevice()
    {
        if (_device != null) return;

        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0,
        };

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out _device).CheckError();

        _context = _device!.ImmediateContext;
        _rtDevice = Direct3D11Interop.CreateDirect3DDevice(_device);
    }

    private void BuildPipelineForRegion(Rectangle region)
    {
        // Tear down previous capture (keep the D3D device).
        _session?.Dispose(); _session = null;
        _framePool?.Dispose(); _framePool = null;
        _item = null;

        var (hmon, monRect) = NativeMethods.GetMonitorForRegion(region);
        _monitor = hmon;
        _monitorRect = monRect;

        _item = Direct3D11Interop.CreateItemForMonitor(hmon);
        var size = _item.Size; // full monitor, physical px

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _rtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            size);
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        TryDisableCursorAndBorderDefaults(_session);
        EnsureStaging(region.Width, region.Height);
        _session.StartCapture();
    }

    private static void TryDisableCursorAndBorderDefaults(GraphicsCaptureSession session)
    {
        // Keep the OS yellow capture border (spec §6 welcomes it). Cursor capture
        // stays on by default. Guard property access for older OS builds.
        try { session.IsCursorCaptureEnabled = true; } catch { /* older build */ }
    }

    private void EnsureStaging(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_staging != null && _staging.Description.Width == width && _staging.Description.Height == height)
            return;

        _staging?.Dispose();
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        _staging = _device!.CreateTexture2D(desc);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
        if (frame == null) return;

        lock (_sync)
        {
            if (_disposed) { frame.Dispose(); return; }

            try
            {
                if (_frozen || _staging == null || _context == null)
                    return;

                // Throttle to the target frame rate — skip the GPU/CPU copy work
                // for frames that arrive too soon (the frame is still drained below).
                // Guard the first frame explicitly (avoid overflow vs the sentinel).
                long nowMs = _clock.ElapsedMilliseconds;
                if (_lastProcessedMs != long.MinValue && nowMs - _lastProcessedMs < _minIntervalMs)
                    return;
                _lastProcessedMs = nowMs;

                using ID3D11Texture2D src = Direct3D11Interop.GetTexture(frame.Surface);

                // Crop box: region relative to the monitor origin, clamped to bounds.
                int left = _region.Left - _monitorRect.Left;
                int top = _region.Top - _monitorRect.Top;
                int right = left + _region.Width;
                int bottom = top + _region.Height;

                int srcW = (int)src.Description.Width;
                int srcH = (int)src.Description.Height;
                left = Math.Clamp(left, 0, srcW);
                top = Math.Clamp(top, 0, srcH);
                right = Math.Clamp(right, left, srcW);
                bottom = Math.Clamp(bottom, top, srcH);

                int w = right - left;
                int h = bottom - top;
                if (w <= 0 || h <= 0) return;

                EnsureStaging(w, h);

                var box = new Vortice.Mathematics.Box(left, top, 0, right, bottom, 1);
                _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, src, 0, box);

                MappedSubresource map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    int stride = w * 4;
                    int needed = stride * h;
                    if (_latest.Length != needed)
                        _latest = new byte[needed];

                    unsafe
                    {
                        byte* srcBase = (byte*)map.DataPointer;
                        fixed (byte* dstBase = _latest)
                        {
                            for (int y = 0; y < h; y++)
                            {
                                Buffer.MemoryCopy(
                                    srcBase + (long)y * map.RowPitch,
                                    dstBase + (long)y * stride,
                                    stride, stride);
                            }
                        }
                    }

                    _latestWidth = w;
                    _latestHeight = h;
                    _frameSeq++;
                }
                finally
                {
                    _context.Unmap(_staging!, 0);
                }
            }
            catch
            {
                // Swallow transient frame errors (device reset, race with rebuild).
            }
            finally
            {
                frame.Dispose();
            }
        }
    }

    /// <summary>
    /// Copies the latest frame into <paramref name="dst"/> (reallocating if the
    /// size changed). Returns false when no new frame arrived since last call.
    /// </summary>
    public bool TryGetFrame(ref byte[]? dst, out int width, out int height)
    {
        lock (_sync)
        {
            width = _latestWidth;
            height = _latestHeight;
            if (_frameSeq == _consumedSeq || _latest.Length == 0)
                return false;

            if (dst == null || dst.Length != _latest.Length)
                dst = new byte[_latest.Length];
            Array.Copy(_latest, dst, _latest.Length);
            _consumedSeq = _frameSeq;
            return true;
        }
    }

    private static Rectangle Sanitize(Rectangle r)
    {
        int w = Math.Max(16, r.Width);
        int h = Math.Max(16, r.Height);
        return new Rectangle(r.Left, r.Top, w, h);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            if (_framePool != null)
                _framePool.FrameArrived -= OnFrameArrived;
            _session?.Dispose();
            _framePool?.Dispose();
            _staging?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
    }
}
