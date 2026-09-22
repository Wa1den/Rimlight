using System;
using System.Text;

namespace Rimlight;

/// <summary>
/// Frames in the AWA variant of Adalight, the protocol the HyperSerial firmwares accept.
///
///   'A' 'w' 'a'  hi lo crc      crc = hi ^ lo ^ 0x55, hi/lo encode (N - 1)
///   N x (R, G, B)
///   f1 f2 fext                  Fletcher sums over the pixel bytes only
///
/// Stock Adalight is not understood by these firmwares at all: the parser expects 'w' as
/// the second byte and drops 'Ada' on it. Colour order on the wire is always R, G, B; the
/// firmware reorders to the strip's own GRB when it sets the pixel.
///
/// Shared with tools/AwaProbe, which checks this encoder against a transcription of the
/// firmware's parser - keep it free of anything else from the application.
/// </summary>
public static class AwaFrame
{
    public const int HeaderSize = 6;
    public const int TrailerSize = 3;

    /// <summary>
    /// Header 'A' 'w' 'A' with a count of 0x2aa2 and a deliberately wrong CRC, which the
    /// firmware reads as a request rather than a frame: 0x15 prints the statistics and the
    /// greeting and resets the counters, 0x35 prints the statistics alone. The trailing text
    /// is what HyperHDR sends; the parser skips it while hunting for the next 'A'.
    /// </summary>
    public static readonly byte[] HelloQuery = Query(0x15);
    public static readonly byte[] StatsQuery = Query(0x35);

    /// <summary>What the firmware says after the statistics when greeted, and nothing else does.</summary>
    public const string GreetingMark = "Awa driver";

    static byte[] Query(byte kind)
    {
        var tail = Encoding.ASCII.GetBytes("hyperhdr");
        var q = new byte[HeaderSize + tail.Length];
        q[0] = (byte)'A'; q[1] = (byte)'w'; q[2] = (byte)'A';
        q[3] = 0x2a; q[4] = 0xa2; q[5] = kind;
        tail.CopyTo(q, HeaderSize);
        return q;
    }

    public static int Size(int leds) => HeaderSize + leds * 3 + TrailerSize;

    /// <summary>Constant for a given LED count, so a caller reusing one buffer writes it once.</summary>
    public static void WriteHeader(Span<byte> frame, int leds)
    {
        int n = leds - 1;
        frame[0] = (byte)'A'; frame[1] = (byte)'w'; frame[2] = (byte)'a';
        frame[3] = (byte)(n >> 8);
        frame[4] = (byte)(n & 0xFF);
        frame[5] = (byte)(frame[3] ^ frame[4] ^ 0x55);
    }

    /// <summary>Computes the checksums over the pixels already in the frame and writes them after them.</summary>
    public static void WriteTrailer(Span<byte> frame, int leds)
    {
        int end = HeaderSize + leds * 3;

        int f1 = 0, f2 = 0, ext = 0;
        byte position = 0;
        for (int i = HeaderSize; i < end; i++)
        {
            byte b = frame[i];
            ext = (ext + (b ^ position++)) % 255;
            f1 = (f1 + b) % 255;
            f2 = (f2 + f1) % 255;
        }

        frame[end] = (byte)f1;
        frame[end + 1] = (byte)f2;

        // 0x41 is 'A': a checksum byte that reads as the start of a header would let the
        // parser resynchronise inside the trailer, so that one value is swapped for 0xaa
        frame[end + 2] = (byte)(ext != 0x41 ? ext : 0xaa);
    }

    /// <param name="rgb">Three bytes per LED, in strip order.</param>
    public static byte[] Build(ReadOnlySpan<byte> rgb)
    {
        int leds = rgb.Length / 3;
        if (leds < 1 || leds > 4096) throw new ArgumentOutOfRangeException(nameof(rgb));

        var frame = new byte[Size(leds)];
        WriteHeader(frame, leds);
        rgb[..(leds * 3)].CopyTo(frame.AsSpan(HeaderSize));
        WriteTrailer(frame, leds);
        return frame;
    }
}
