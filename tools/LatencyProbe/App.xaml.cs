using System.Windows;

namespace LatencyProbe;

public partial class App : Application
{
    // No RenderMode.SoftwareOnly here, unlike the capture probe. That tool had to keep
    // drawing while a game starved the GPU; this one draws a handful of rectangles and a
    // line of digits, and its whole job is to put a colour on the glass as early as the
    // display allows. Software rendering would hand the frame to the compositor later and
    // cap the on-screen clock below the refresh rate, which is exactly the resolution the
    // measurement is read at.
}
