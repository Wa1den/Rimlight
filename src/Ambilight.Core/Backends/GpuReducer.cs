using System;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Ambilight.Capture.Backends;

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
    int _writeIdx;

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
    public unsafe (byte r, byte g, byte b, bool isBlack, bool valid) Reduce(ID3D11Texture2D frame)
    {
        var fd = frame.Description;
        Ensure((int)fd.Width, (int)fd.Height);

        // queue this frame's downscale into the next ring slot
        _context.CopySubresourceRegion(_mipTex!, 0, 0, 0, 0, frame, 0);
        _context.GenerateMips(_srv!);
        _context.CopySubresourceRegion(_staging[_writeIdx]!, 0, 0, 0, 0, _mipTex!, (uint)_targetMip);
        _pending[_writeIdx] = true;
        _writeIdx = (_writeIdx + 1) % RingSize;

        // Walk newest-first and take the first slot the GPU has already finished with.
        // MapFlags.DoNotWait turns "not ready" into a failed Result instead of a stall.
        for (int k = RingSize - 1; k >= 0; k--)
        {
            int idx = (_writeIdx + k) % RingSize;
            if (!_pending[idx]) continue;

            var hr = _context.Map(_staging[idx]!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait,
                                  out MappedSubresource map);
            if (hr.Failure) continue;   // DXGI_ERROR_WAS_STILL_DRAWING

            try
            {
                var span = new ReadOnlySpan<byte>((void*)map.DataPointer, (int)map.RowPitch * _smallH);
                if (LastImage.Length != span.Length) LastImage = new byte[span.Length];
                span.CopyTo(LastImage);
                ImageWidth = _smallW; ImageHeight = _smallH; ImageStride = (int)map.RowPitch;

                var result = ColorMath.AverageBgra(span, _smallW, _smallH, (int)map.RowPitch);

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
                return (result.r, result.g, result.b, result.isBlack, true);
            }
            finally
            {
                _context.Unmap(_staging[idx]!, 0);
                // this slot and everything older than it is now spent
                for (int j = 0; j <= k; j++)
                    _pending[(_writeIdx + j) % RingSize] = false;
            }
        }

        Skipped++;
        return (_lastR, _lastG, _lastB, _lastBlack, _hasResult);
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
    public unsafe bool TryDrain(out byte r, out byte g, out byte b, out bool isBlack)
    {
        r = _lastR; g = _lastG; b = _lastB; isBlack = _lastBlack;
        if (_staging[0] == null) return false;

        for (int k = RingSize - 1; k >= 0; k--)
        {
            int idx = (_writeIdx + k) % RingSize;
            if (!_pending[idx]) continue;

            var hr = _context.Map(_staging[idx]!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait,
                                  out MappedSubresource map);
            if (hr.Failure) continue;

            try
            {
                var span = new ReadOnlySpan<byte>((void*)map.DataPointer, (int)map.RowPitch * _smallH);
                if (LastImage.Length != span.Length) LastImage = new byte[span.Length];
                span.CopyTo(LastImage);
                ImageWidth = _smallW; ImageHeight = _smallH; ImageStride = (int)map.RowPitch;

                var result = ColorMath.AverageBgra(span, _smallW, _smallH, (int)map.RowPitch);
                _lastR = result.r; _lastG = result.g; _lastB = result.b; _lastBlack = result.isBlack;
                _hasResult = true;

                r = result.r; g = result.g; b = result.b; isBlack = result.isBlack;
                return true;
            }
            finally
            {
                _context.Unmap(_staging[idx]!, 0);
                for (int j = 0; j <= k; j++)
                    _pending[(_writeIdx + j) % RingSize] = false;
            }
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
