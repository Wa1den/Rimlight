using System;
using System.Windows;

using Rimlight.Capture;
using Rimlight.Text;

namespace Rimlight;

public partial class App : Application
{
    /// <summary>
    /// Writes an exception that is about to end the process into the program's own log.
    ///
    /// Without it such a crash left nothing in rimlight.log: on 10.09.2026 the pen thread
    /// failed on a runtime removed by an update, and the stack was only found in the Windows
    /// event log.
    /// </summary>
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ProbeLog.Log(Loc.P("сбой", "crash"),
                Loc.P("необработанное исключение, программа завершается: ",
                      "unhandled exception, the program is terminating: ") + e.ExceptionObject);
    }
}
