using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ambilight.Capture;

/// <summary>
/// Writes what a backend actually sees to a PNG. Judging capture by one averaged colour
/// cannot tell "sees the game" from "sees the desktop behind it" - this can.
/// </summary>
public static class Snapshot
{
    public static string Directory =>
        Path.Combine(AppContext.BaseDirectory, "snapshots");

    public static string Save(string backendName, byte[] bgra, int width, int height, int stride)
    {
        System.IO.Directory.CreateDirectory(Directory);

        string safe = backendName.Replace('\\', '_').Replace('/', '_');
        string path = Path.Combine(Directory, $"{DateTime.Now:HHmmss}_{safe}.png");

        var src = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
        src.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            encoder.Save(fs);

        ProbeLog.Log(backendName, $"кадр сохранён: {path} ({width}x{height})");
        return path;
    }
}
