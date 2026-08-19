using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CaptureProbe;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // WPF composites on the GPU by default, so while a game saturates the card the
        // render thread starves and WriteableBitmap.WritePixels blocks the UI thread -
        // the window appeared frozen even though capture threads were still running.
        // A diagnostic tool must not depend on the very GPU it is diagnosing.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);
    }
}
