using System;
using System.Text;

namespace AwaProbe;

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
/// </summary>
static class AwaFrame
{
    /// <summary>
    /// Header 'A' 'w' 'A' with a count of 0x2aa2 and a deliberately wrong CRC, which the
    /// firmware reads as a request rather than a frame: 0x15 prints the statistics and the
    /// greeting and resets the counters, 0x35 prints the statistics alone. The trailing text
    /// is what HyperHDR sends; the parser skips it while hunting for the next 'A'.
    /// </summary>
    public static readonly byte[] HelloQuery = Query(0x15);
    public static readonly byte[] StatsQuery = Query(0x35);

    static byte[] Query(byte kind)
    {
        var tail = Encoding.ASCII.GetBytes("hyperhdr");
        var q = new byte[6 + tail.Length];
        q[0] = (byte)'A'; q[1] = (byte)'w'; q[2] = (byte)'A';
        q[3] = 0x2a; q[4] = 0xa2; q[5] = kind;
        tail.CopyTo(q, 6);
        return q;
    }

    /// <param name="rgb">Three bytes per LED, in strip order.</param>
    public static byte[] Build(ReadOnlySpan<byte> rgb)
    {
        int leds = rgb.Length / 3;
        if (leds < 1 || leds > 4096) throw new ArgumentOutOfRangeException(nameof(rgb));

        int payload = leds * 3;
        var frame = new byte[6 + payload + 3];

        int n = leds - 1;
        frame[0] = (byte)'A'; frame[1] = (byte)'w'; frame[2] = (byte)'a';
        frame[3] = (byte)(n >> 8);
        frame[4] = (byte)(n & 0xFF);
        frame[5] = (byte)(frame[3] ^ frame[4] ^ 0x55);

        rgb[..payload].CopyTo(frame.AsSpan(6));

        int f1 = 0, f2 = 0, ext = 0;
        byte position = 0;
        for (int i = 6; i < 6 + payload; i++)
        {
            byte b = frame[i];
            ext = (ext + (b ^ position++)) % 255;
            f1 = (f1 + b) % 255;
            f2 = (f2 + f1) % 255;
        }

        int t = 6 + payload;
        frame[t] = (byte)f1;
        frame[t + 1] = (byte)f2;

        // 0x41 is 'A': a checksum byte that reads as the start of a header would let the
        // parser resynchronise inside the trailer, so that one value is swapped for 0xaa
        frame[t + 2] = (byte)(ext != 0x41 ? ext : 0xaa);
        return frame;
    }
}
