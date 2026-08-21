using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Rimlight.Capture;
using Rimlight.Text;

namespace Rimlight.Frames;

/// <summary>
/// Reads frames another process publishes onto <see cref="FrameBus"/>.
///
/// Attaching is optional and revocable by design: the publisher may not be running, may be
/// started later, or may have the option switched off. Every call either hands back a
/// frame or says why not, and the consumer decides whether to fall back to capturing the
/// screen itself.
/// </summary>
public sealed unsafe class FrameSubscriber : IDisposable
{
    /// <summary>A stale mapping outlives a dead publisher, so age is what proves liveness.</summary>
    public const long DefaultMaxAgeMs = 1000;

    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    byte* _ptr;

    long _lastSeq;
    long _lastAttachAttempt;

    public bool IsAttached => _ptr != null;
    public string Status { get; private set; } = Loc.P("не подключено", "not connected");
    public long FramesRead { get; private set; }
    public long Retries { get; private set; }

    /// <summary>
    /// Tries to attach, at most once every couple of seconds. Safe to call every tick:
    /// OpenExisting on a missing map throws, and throwing sixty times a second to learn
    /// something that changes once a minute is not free.
    /// </summary>
    public bool TryAttach()
    {
        if (IsAttached) return true;

        long now = Environment.TickCount64;
        if (now - _lastAttachAttempt < 2000) return false;
        _lastAttachAttempt = now;

        try
        {
            _mmf = MemoryMappedFile.OpenExisting(FrameBus.MapName, MemoryMappedFileRights.Read);
            _view = _mmf.CreateViewAccessor(0, FrameBus.TotalBytes, MemoryMappedFileAccess.Read);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
        }
        catch (Exception ex)
        {
            Status = ex is System.IO.FileNotFoundException
                ? Loc.P("издатель не запущен", "publisher is not running")
                : Loc.P("не удалось подключиться: ", "could not attach: ") + ex.Message;
            Detach();
            return false;
        }

        uint magic = *(uint*)(_ptr + FrameBus.OffMagic);
        uint version = *(uint*)(_ptr + FrameBus.OffVersion);

        if (magic != FrameBus.Magic || version != FrameBus.Version)
        {
            Status = Loc.P($"чужая или несовместимая шина (magic {magic:X8}, версия {version})",
                          $"foreign or incompatible bus (magic {magic:X8}, version {version})");
            ProbeLog.Log(Loc.P("шина кадров", "frame bus"), Status);
            Detach();
            return false;
        }

        // Whatever is sitting there now belongs to the past; start from the next frame.
        _lastSeq = Volatile.Read(ref *(long*)(_ptr + FrameBus.OffSeq));

        Status = Loc.P("подключено", "attached");
        ProbeLog.Log(Loc.P("шина кадров", "frame bus"), Loc.P($"подключились к {FrameBus.MapName}", $"attached to {FrameBus.MapName}"));
        return true;
    }

    /// <summary>
    /// Copies out the newest frame if there is one newer than the last returned.
    /// Mirrors ICaptureBackend.TryGetImage on purpose, so the two frame sources are
    /// interchangeable at the call site.
    /// </summary>
    public bool TryRead(ref byte[] dest, out FrameInfo info, long maxAgeMs = DefaultMaxAgeMs)
    {
        info = default;
        if (!IsAttached) return false;

        // Four attempts is generous: losing the race needs the publisher to write two whole
        // frames inside one 150 KB memcpy.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            long seq = Volatile.Read(ref *(long*)(_ptr + FrameBus.OffSeq));
            if (seq == 0 || seq == _lastSeq) return false;

            long stamp = Volatile.Read(ref *(long*)(_ptr + FrameBus.OffTimestamp));
            long age = stamp == 0 ? long.MaxValue : Environment.TickCount64 - stamp;

            if (age > maxAgeMs)
            {
                // The publisher stopped or retired the bus. Do not consume the sequence:
                // when it comes back, its next frame must still read as new.
                Status = Loc.P("издатель молчит", "publisher has gone quiet");
                return false;
            }

            int width = *(int*)(_ptr + FrameBus.OffWidth);
            int height = *(int*)(_ptr + FrameBus.OffHeight);
            int stride = *(int*)(_ptr + FrameBus.OffStride);
            int format = *(int*)(_ptr + FrameBus.OffFormat);

            int bytes = height * stride;
            if (format != FrameBus.FormatBgra32 || bytes <= 0 || bytes > FrameBus.SlotBytes)
            {
                Status = Loc.P($"непонятный кадр {width}x{height}, формат {format}", $"unusable frame {width}x{height}, format {format}");
                _lastSeq = seq;
                return false;
            }

            if (dest.Length != bytes) dest = new byte[bytes];

            // seq - 1 is the slot that was just completed; the publisher is now filling the other
            var slot = new ReadOnlySpan<byte>(_ptr + FrameBus.SlotOffset(seq - 1), bytes);
            slot.CopyTo(dest);

            string name = FrameBus.ReadName(
                new ReadOnlySpan<byte>(_ptr + FrameBus.OffMonName, FrameBus.MonNameBytes));

            long after = Volatile.Read(ref *(long*)(_ptr + FrameBus.OffSeq));
            if (after - seq >= 2)
            {
                // Two frames landed while we copied, so the second one reused our slot.
                Retries++;
                continue;
            }

            info = new FrameInfo(
                width, height, stride, name,
                *(int*)(_ptr + FrameBus.OffMonLeft),
                *(int*)(_ptr + FrameBus.OffMonTop),
                *(int*)(_ptr + FrameBus.OffMonWidth),
                *(int*)(_ptr + FrameBus.OffMonHeight),
                *(int*)(_ptr + FrameBus.OffPid),
                age);

            _lastSeq = seq;
            FramesRead++;
            Status = Loc.P("подключено", "attached");
            return true;
        }

        Status = Loc.P("издатель пишет быстрее, чем мы читаем", "publisher writes faster than we read");
        return false;
    }

    public void Detach()
    {
        if (_ptr != null)
        {
            _view?.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptr = null;
        }

        _view?.Dispose();
        _view = null;
        _mmf?.Dispose();
        _mmf = null;
    }

    public void Dispose() => Detach();
}
