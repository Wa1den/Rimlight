using System;
using System.Diagnostics;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rimlight.Capture.Backends;

/// <summary>
/// Reduces a full-screen GPU texture to an average colour without ever pulling a full
/// frame across the bus: hardware mip generation does the downscale, and only the small
/// tail mip is read back.
///
/// The readback is deliberately non-blocking. A plain Map() waits until the GPU has
/// drained everything queued ahead of it, so while a game saturates the card it stalls
/// for hundreds of milliseconds - measured at up to 6.4 s here before this was fixed.
/// Instead each frame issues its copy into one slot of a ring and reads back only a slot
/// the GPU has already finished with. If nothing is ready the previous colour is reused
/// and the pipeline never blocks.
/// </summary>
public sealed class GpuReducer : IDisposable
{
    const int RingSize = 3;

    /// <summary>
    /// Width the frame is reduced to before readback. 64 is plenty for one average colour,
    /// but per-zone extraction needs more: at 3440 wide with 43 zones across the top, a
    /// 64-wide image gives each zone barely one pixel.
    /// </summary>
    readonly int _targetMaxWidth;

    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _context;

    ID3D11Texture2D? _mipTex;
    ID3D11ShaderResourceView? _srv;
    ID3D11Texture2D? _snapStaging;
    int _snapMip, _snapW, _snapH;

    readonly ID3D11Texture2D?[] _staging = new ID3D11Texture2D?[RingSize];
    readonly bool[] _pending = new bool[RingSize];
    readonly long[] _slotStamp = new long[RingSize];
    int _writeIdx;

    /// <summary>
    /// How long the frame just queued is given to finish before an older one is taken.
    ///
    /// Without this the ring always hands back the previous frame, because the map is
    /// attempted the instant the copy is queued and the GPU has obviously not finished
    /// yet - a whole capture frame of latency, every frame. A short wait recovers it
    /// whenever the card has headroom, which is the case for video and the desktop.
    /// </summary>
    static readonly long FreshWaitTicks = (long)(Stopwatch.Frequency * 0.0015);   // 1.5 ms

    /// <summary>
    /// Under a game that keeps the GPU saturated the wait never pays off, and paying it
    /// on every frame would make the fallback frame staler than it used to be. Misses in
    /// a row switch the wait off; a periodic probe switches it back on when the load ends.
    /// </summary>
    const int FreshMissLimit = 4;
    const int FreshProbeEvery = 120;

    int _freshMisses, _sinceProbe;

    /// <summary>Frames whose own reduction was read back rather than the previous one.</summary>
    public long FreshHits { get; private set; }

    int _width, _height, _targetMip, _smallW, _smallH;

    byte _lastR, _lastG, _lastB;
    bool _lastBlack;
    bool _hasResult;   // until the ring warms up there is nothing honest to report

    /// <summary>Number of frames whose reduction was still in flight when asked for.</summary>
    public long Skipped { get; private set; }

    /// <summary>
    /// Checksum of the last reduced image. Comparing average colour alone is too coarse -
    /// very different frames can average identically - so change detection uses this.
    /// </summary>
    public ulong LastHash { get; private set; }

    /// <summary>Last reduced image, BGRA. Valid until the next Reduce on this instance.</summary>
    public byte[] LastImage { get; private set; } = Array.Empty<byte>();

    /// <summary>
    /// When the picture in <see cref="LastImage"/> was put on screen, on the Stopwatch
    /// clock. Carried per ring slot, because the slot read back is usually not the one
    /// just queued - stamping at readback would hide exactly the delay worth measuring.
    /// </summary>
    public long LastImageStamp { get; private set; }
    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }
    public int ImageStride { get; private set; }

    public GpuReducer(ID3D11Device device, ID3D11DeviceContext context, int targetMaxWidth = 64)
    {
        _device = device;
        _context = context;
        _targetMaxWidth = targetMaxWidth;
    }

    void Ensure(int width, int height)
    {
        if (_mipTex != null && _width == width && _height == height) return;

        DisposeResources();
        _width = width;
        _height = height;

        _mipTex = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 0,          // 0 asks D3D for the full chain
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips
        });
        _srv = _device.CreateShaderResourceView(_mipTex);

        int levels = (int)_mipTex.Description.MipLevels;
        _targetMip = 0;
        while (_targetMip < levels - 1 && (width >> _targetMip) > _targetMaxWidth)
            _targetMip++;

        _smallW = Math.Max(1, width >> _targetMip);
        _smallH = Math.Max(1, height >> _targetMip);

        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)_smallW,
            Height = (uint)_smallH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        for (int i = 0; i < RingSize; i++)
        {
            _staging[i] = _device.CreateTexture2D(stagingDesc);
            _pending[i] = false;
            _slotStamp[i] = 0;
        }
        _writeIdx = 0;
        _hasResult = false;

        // a coarser reduction, big enough to recognise by eye
        _snapMip = 0;
        while (_snapMip < levels - 1 && (width >> _snapMip) > 256) _snapMip++;
        _snapW = Math.Max(1, width >> _snapMip);
        _snapH = Math.Max(1, height >> _snapMip);
        _snapStaging = _device.CreateTexture2D(stagingDesc with
        {
            Width = (uint)_snapW,
            Height = (uint)_snapH
        });

        ProbeLog.Log("gpu", $"редьюсер {width}x{height} -> mip {_targetMip} = {_smallW}x{_smallH}, кольцо {RingSize}");
    }

    /// <summary>
    /// Returns valid=false while the ring is still warming up, so the first frames are
    /// not miscounted as black - black frames are the signal this probe exists to catch.
    /// </summary>
    /// <param name="stampQpc">
    /// When this frame was presented, on the Stopwatch clock. Travels with the ring slot
    /// so the consumer can tell how old the picture it finally gets actually is.
    /// </param>
    public (byte r, byte g, byte b, bool isBlack, bool valid) Reduce(ID3D11Texture2D frame, long stampQpc = 0)
    {
        var fd = frame.Description;
        Ensure((int)fd.Width, (int)fd.Height);

        // queue this frame's downscale into the next ring slot
        _context.CopySubresourceRegion(_mipTex!, 0, 0, 0, 0, frame, 0);
        _context.GenerateMips(_srv!);
        _context.CopySubresourceRegion(_staging[_writeIdx]!, 0, 0, 0, 0, _mipTex!, (uint)_targetMip);
        _pending[_writeIdx] = true;
        _slotStamp[_writeIdx] = stampQpc != 0 ? stampQpc : Stopwatch.GetTimestamp();
        _writeIdx = (_writeIdx + 1) % RingSize;

        // The slot just queued is the one worth having: everything else in the ring is at
        // least one capture frame stale by definition.
        _sinceProbe++;
        if (_freshMisses < FreshMissLimit || _sinceProbe >= FreshProbeEvery)
        {
            _sinceProbe = 0;
            long deadline = Stopwatch.GetTimestamp() + FreshWaitTicks;
            while (true)
            {
                if (TryReadSlot(RingSize - 1, out var fresh))
                {
                    _freshMisses = 0;
                    FreshHits++;
                    return (fresh.r, fresh.g, fresh.b, fresh.isBlack, true);
                }
                if (Stopwatch.GetTimestamp() >= deadline) break;

                // the GPU is the one working here, so back off rather than burn the core
                Thread.SpinWait(128);
            }
            _freshMisses++;
        }

        // Walk newest-first and take the first slot the GPU has already finished with.
        // MapFlags.DoNotWait turns "not ready" into a failed Result instead of a stall.
        for (int k = RingSize - 1; k >= 0; k--)
            if (TryReadSlot(k, out var result))
                return (result.r, result.g, result.b, result.isBlack, true);

        Skipped++;
        return (_lastR, _lastG, _lastB, _lastBlack, _hasResult);
    }

    /// <summary>
    /// Reads back ring slot <paramref name="k"/> counted from the oldest, where
    /// RingSize - 1 is the one queued most recently. Fails without blocking when the GPU
    /// has not finished with it.
    /// </summary>
    unsafe bool TryReadSlot(int k, out (byte r, byte g, byte b, bool isBlack) result)
    {
        result = default;

        int idx = (_writeIdx + k) % RingSize;
        if (_staging[idx] == null || !_pending[idx]) return false;

        var hr = _context.Map(_staging[idx]!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait,
                              out MappedSubresource map);
        if (hr.Failure) return false;   // DXGI_ERROR_WAS_STILL_DRAWING

        try
        {
            var span = new ReadOnlySpan<byte>((void*)map.DataPointer, (int)map.RowPitch * _smallH);
            if (LastImage.Length != span.Length) LastImage = new byte[span.Length];
            span.CopyTo(LastImage);
            ImageWidth = _smallW; ImageHeight = _smallH; ImageStride = (int)map.RowPitch;
            LastImageStamp = _slotStamp[idx];

            result = ColorMath.AverageBgra(span, _smallW, _smallH, (int)map.RowPitch);

            // FNV-1a over the reduced image; a few KB, so the cost is irrelevant
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < span.Length; i++)
            {
                h ^= span[i];
                h *= 1099511628211UL;
            }
            LastHash = h;

            _lastR = result.r; _lastG = result.g; _lastB = result.b; _lastBlack = result.isBlack;
            _hasResult = true;
            return true;
        }
        finally
        {
            _context.Unmap(_staging[idx]!, 0);
            // this slot and everything older than it is now spent
            for (int j = 0; j <= k; j++)
                _pending[(_writeIdx + j) % RingSize] = false;
        }
    }

    /// <summary>
    /// Grabs the last generated mip pyramid at snapshot resolution. Blocking is fine
    /// here - it happens only when a human presses the button.
    /// </summary>
    public unsafe byte[]? TryGrabSnapshot(out int width, out int height, out int stride)
    {
        width = _snapW; height = _snapH; stride = 0;
        if (_mipTex == null || _snapStaging == null) return null;

        _context.CopySubresourceRegion(_snapStaging, 0, 0, 0, 0, _mipTex, (uint)_snapMip);
        var map = _context.Map(_snapStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            stride = (int)map.RowPitch;
            var bytes = new byte[stride * _snapH];
            new ReadOnlySpan<byte>((void*)map.DataPointer, bytes.Length).CopyTo(bytes);
            return bytes;
        }
        finally
        {
            _context.Unmap(_snapStaging, 0);
        }
    }

    /// <summary>
    /// Reads back a reduction that is already finished, without queueing new work.
    ///
    /// The ring deliberately hands out the previous frame's result so the CPU never waits
    /// on the GPU - but that leaves the newest frame sitting unread until another one
    /// arrives to push it through. On a still screen there is no next frame, so a change
    /// would linger in the ring until something else happened to move. Callers drain here
    /// when capture reports no new frames.
    /// </summary>
    public bool TryDrain(out byte r, out byte g, out byte b, out bool isBlack)
    {
        r = _lastR; g = _lastG; b = _lastB; isBlack = _lastBlack;
        if (_staging[0] == null) return false;

        for (int k = RingSize - 1; k >= 0; k--)
        {
            if (!TryReadSlot(k, out var result)) continue;

            // the ring is empty now, so waiting on a fresh slot is worth trying again
            _freshMisses = 0;

            r = result.r; g = result.g; b = result.b; isBlack = result.isBlack;
            return true;
        }

        return false;
    }

    void DisposeResources()
    {
        _snapStaging?.Dispose(); _snapStaging = null;
        for (int i = 0; i < RingSize; i++)
        {
            _staging[i]?.Dispose(); _staging[i] = null;
            _pending[i] = false;
        }
        _srv?.Dispose(); _srv = null;
        _mipTex?.Dispose(); _mipTex = null;
    }

    public void Dispose() => DisposeResources();
}
