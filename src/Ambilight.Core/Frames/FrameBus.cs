using System;
using System.Text;

namespace Ambilight.Frames;

/// <summary>
/// Shared-memory layout for handing reduced screen frames to another process.
///
/// This is a contract between two separate repositories - the Ambilight application
/// publishes, the case-lighting module consumes - so the offsets are spelled out here
/// rather than left to whatever the compiler makes of a struct.
///
/// Two slots and a sequence counter, written in that order:
///
///   1. writer fills slot (seq and 1)
///   2. writer updates the metadata fields
///   3. writer publishes seq + 1
///
/// A reader takes slot ((seq - 1) and 1) and re-reads seq afterwards. One further frame
/// during the copy is harmless - it went into the other slot - so only a jump of two or
/// more means the data was overwritten underneath and the read has to be retried.
/// </summary>
public static class FrameBus
{
    /// <summary>Session-local, so no elevation is needed and other sessions cannot see it.</summary>
    public const string MapName = @"Local\AmbilightFrameBus";

    public const uint Magic = 0x42464C41;   // 'ALFB' little-endian
    public const uint Version = 1;

    public const int HeaderBytes = 256;

    /// <summary>
    /// A reduced frame is 256 x ~144 x 4 = about 147 KB, so this is a fourfold margin and
    /// still covers a 512-wide reduction should it ever be raised.
    /// </summary>
    public const int SlotBytes = 1 << 20;

    public const int TotalBytes = HeaderBytes + 2 * SlotBytes;

    /// <summary>The only pixel format the bus carries today: 8-bit BGRA, as captured.</summary>
    public const int FormatBgra32 = 1;

    internal const int OffMagic = 0;
    internal const int OffVersion = 4;
    internal const int OffSeq = 8;
    internal const int OffWidth = 16;
    internal const int OffHeight = 20;
    internal const int OffStride = 24;
    internal const int OffFormat = 28;
    internal const int OffTimestamp = 32;
    internal const int OffPid = 40;
    internal const int OffSlotBytes = 44;
    internal const int OffMonLeft = 48;
    internal const int OffMonTop = 52;
    internal const int OffMonWidth = 56;
    internal const int OffMonHeight = 60;
    internal const int OffMonName = 64;
    internal const int MonNameBytes = 64;

    internal static int SlotOffset(long seq) => HeaderBytes + (int)(seq & 1) * SlotBytes;

    internal static string ReadName(ReadOnlySpan<byte> raw)
    {
        int end = raw.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? raw : raw[..end]);
    }
}

/// <summary>What the frame is, alongside the pixels themselves.</summary>
public readonly record struct FrameInfo(
    int Width, int Height, int Stride,
    string MonitorDeviceName, int MonitorLeft, int MonitorTop,
    int MonitorWidth, int MonitorHeight,
    int PublisherPid, long AgeMs);
