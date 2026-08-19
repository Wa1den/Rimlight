using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using Ambilight.Capture;

namespace Ambilight.Frames;

/// <summary>
/// Publishes reduced frames onto <see cref="FrameBus"/> for another process to pick up.
///
/// Costs one memcpy of about 150 KB per frame and nothing else - no encoding, no locks -
/// so it can sit directly in the output loop without disturbing the strip's cadence.
/// </summary>
public sealed unsafe class FramePublisher : IDisposable
{
    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    byte* _ptr;
    long _seq;
    long _lastOpenAttempt;

    public bool IsOpen => _ptr != null;
    public long Published { get; private set; }
    public long Dropped { get; private set; }
    public string Status { get; private set; } = "выключено";

    /// <summary>
    /// Safe to call every tick - the output loop follows the setting live. A failure is
    /// not retried for a couple of seconds: whatever stopped the mapping from opening will
    /// not have changed within one frame, and logging it sixty times a second would bury
    /// everything else.
    /// </summary>
    public bool Open()
    {
        if (IsOpen) return true;

        long now = Environment.TickCount64;
        if (now - _lastOpenAttempt < 2000) return false;
        _lastOpenAttempt = now;

        try
        {
            _mmf = MemoryMappedFile.CreateOrOpen(FrameBus.MapName, FrameBus.TotalBytes,
                                                 MemoryMappedFileAccess.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, FrameBus.TotalBytes, MemoryMappedFileAccess.ReadWrite);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
        }
        catch (Exception ex)
        {
            Status = "не удалось открыть: " + ex.Message;
            ProbeLog.Log("шина кадров", Status);
            Close();
            return false;
        }

        // Reusing an existing map means a previous run left one behind, or a second
        // Ambilight is running; either way the counter carries on from where it was so a
        // reader that is already attached never sees the sequence go backwards.
        _seq = Volatile.Read(ref *(long*)(_ptr + FrameBus.OffSeq));

        *(uint*)(_ptr + FrameBus.OffMagic) = FrameBus.Magic;
        *(uint*)(_ptr + FrameBus.OffVersion) = FrameBus.Version;
        *(int*)(_ptr + FrameBus.OffSlotBytes) = FrameBus.SlotBytes;
        *(int*)(_ptr + FrameBus.OffPid) = Environment.ProcessId;

        Status = "открыта";
        ProbeLog.Log("шина кадров", $"{FrameBus.MapName} открыта, слот {FrameBus.SlotBytes / 1024} КБ");
        return true;
    }

    /// <param name="bgra">The reduced frame exactly as the capture backend produced it.</param>
    public void Publish(ReadOnlySpan<byte> bgra, int width, int height, int stride, MonitorInfo? monitor)
    {
        if (!IsOpen) return;

        int bytes = height * stride;
        if (bytes <= 0 || bytes > FrameBus.SlotBytes || bgra.Length < bytes)
        {
            // Only worth a log line when it changes: a wrong-sized frame every tick would
            // otherwise fill the log faster than anything else in it.
            if (Dropped++ == 0)
                ProbeLog.Log("шина кадров", $"кадр {width}x{height} ({bytes} Б) не влез в слот, пропущен");
            return;
        }

        var slot = new Span<byte>(_ptr + FrameBus.SlotOffset(_seq), bytes);
        bgra[..bytes].CopyTo(slot);

        *(int*)(_ptr + FrameBus.OffWidth) = width;
        *(int*)(_ptr + FrameBus.OffHeight) = height;
        *(int*)(_ptr + FrameBus.OffStride) = stride;
        *(int*)(_ptr + FrameBus.OffFormat) = FrameBus.FormatBgra32;
        *(int*)(_ptr + FrameBus.OffMonLeft) = monitor?.Left ?? 0;
        *(int*)(_ptr + FrameBus.OffMonTop) = monitor?.Top ?? 0;
        *(int*)(_ptr + FrameBus.OffMonWidth) = monitor?.Width ?? 0;
        *(int*)(_ptr + FrameBus.OffMonHeight) = monitor?.Height ?? 0;

        WriteName(monitor?.DeviceName ?? "");

        // Timestamp before the counter: a reader that sees the new sequence must already be
        // able to see how old the frame is.
        Volatile.Write(ref *(long*)(_ptr + FrameBus.OffTimestamp), Environment.TickCount64);
        Volatile.Write(ref *(long*)(_ptr + FrameBus.OffSeq), ++_seq);

        Published++;
    }

    void WriteName(string name)
    {
        var dest = new Span<byte>(_ptr + FrameBus.OffMonName, FrameBus.MonNameBytes);
        dest.Clear();

        var utf8 = Encoding.UTF8.GetBytes(name);
        int n = Math.Min(utf8.Length, FrameBus.MonNameBytes - 1);   // keep the zero terminator

        // back off any half-written character, so the reader never decodes a broken tail
        while (n > 0 && n < utf8.Length && (utf8[n] & 0xC0) == 0x80) n--;

        utf8.AsSpan(0, n).CopyTo(dest);
    }

    /// <summary>
    /// Marks the bus stale so a reader attached to the leftover mapping does not keep
    /// showing the last frame as if it were live.
    /// </summary>
    void Retire()
    {
        if (!IsOpen) return;
        Volatile.Write(ref *(long*)(_ptr + FrameBus.OffTimestamp), 0);
    }

    public void Close()
    {
        Retire();

        if (_ptr != null)
        {
            _view?.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptr = null;
        }

        _view?.Dispose();
        _view = null;
        _mmf?.Dispose();
        _mmf = null;
        Status = "выключено";
    }

    public void Dispose() => Close();
}
