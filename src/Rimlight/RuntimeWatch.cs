using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Rimlight;

/// <summary>
/// Notices that a .NET update has removed the runtime this process runs on, so the program
/// can restart onto the new patch before the missing files take it down.
///
/// An update installs the new patch next to the old one and then deletes the old folder.
/// Files the process has already loaded are locked and survive; anything it loads later is
/// gone, and the first such load ends the process. On 10.09.2026 that was PenImc, ten hours
/// after an update; on 26.10.2026, five hours after one, Rimlight, CaseLight and
/// HeadsetSwitch went down together when the tray overflow was opened.
///
/// The marker is the framework's deps.json: the host reads it at startup and closes it, so
/// it disappears with the folder while the loaded assemblies stay. The restart waits for a
/// newer patch of the same version to be in place, and for that to hold on two checks in a
/// row, so it never lands between the removal and the install.
/// </summary>
sealed class RuntimeWatch
{
    /// <summary>Command-line flag: the previous copy was in the tray or minimised.</summary>
    public const string MinimizedArg = "--minimized";

    readonly List<string> _frameworks = new();   // version folders, e.g. ...\Microsoft.NETCore.App\10.0.14
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    bool _readyOnce;

    /// <summary>Raised once, on the UI thread, with the old and the new version.</summary>
    public event Action<string, string>? Replaced;

    public RuntimeWatch()
    {
        AddFramework(typeof(object));
        AddFramework(typeof(System.Windows.Window));

        // a self-contained or otherwise unusual layout has nothing to watch
        if (_frameworks.Count == 0) return;

        // the first check runs now, while every file it touches is still on disk
        Check();
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    /// <summary>Versions being watched, for the log.</summary>
    public string Describe() => _frameworks.Count == 0
        ? "-"
        : string.Join(", ", _frameworks.ConvertAll(f => Path.GetFileName(Path.GetDirectoryName(f)) + " " + Path.GetFileName(f)));

    void AddFramework(Type type)
    {
        string? dir = Path.GetDirectoryName(type.Assembly.Location);
        if (string.IsNullOrEmpty(dir)) return;
        if (File.Exists(DepsFile(dir))) _frameworks.Add(dir);
    }

    static string DepsFile(string versionDir) =>
        Path.Combine(versionDir, Path.GetFileName(Path.GetDirectoryName(versionDir)) + ".deps.json");

    void Check()
    {
        string? removed = null, replacement = null;
        foreach (string dir in _frameworks)
        {
            if (File.Exists(DepsFile(dir))) continue;
            string? newer = NewerPatch(dir);
            // removed with nothing to move to yet: keep running on what is loaded
            if (newer == null) { _readyOnce = false; return; }
            removed = Path.GetFileName(dir);
            replacement = newer;
        }

        if (removed == null) { _readyOnce = false; return; }
        if (!_readyOnce) { _readyOnce = true; return; }

        _timer.Stop();
        Replaced?.Invoke(removed, replacement!);
    }

    /// <summary>The newest installed patch of the same major.minor, if it is complete.</summary>
    static string? NewerPatch(string versionDir)
    {
        if (!Version.TryParse(Path.GetFileName(versionDir), out var current)) return null;
        string? parent = Path.GetDirectoryName(versionDir);
        if (parent == null || !Directory.Exists(parent)) return null;

        Version? best = null;
        foreach (string sibling in Directory.GetDirectories(parent))
        {
            if (!Version.TryParse(Path.GetFileName(sibling), out var v)) continue;
            if (v.Major != current.Major || v.Minor != current.Minor || v <= current) continue;
            if (!File.Exists(DepsFile(sibling))) continue;
            if (best == null || v > best) best = v;
        }
        return best?.ToString();
    }

    /// <summary>
    /// Starts a new copy of the program. CreateProcess directly rather than Process.Start:
    /// System.Diagnostics.Process lives in the runtime folder that has just been removed, and
    /// is not loaded by anything else in the program.
    /// </summary>
    public static unsafe bool Launch(bool minimized)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;

        char[] cmd = ("\"" + exe + "\"" + (minimized ? " " + MinimizedArg : "") + "\0").ToCharArray();
        var si = new StartupInfo { cb = sizeof(StartupInfo) };
        ProcessInformation pi;
        fixed (char* p = cmd)
        {
            if (!CreateProcessW(null, p, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, &si, &pi))
                return false;
        }
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return true;
    }

    /// <summary>
    /// Ends this process on the spot, for after <see cref="Launch"/>. A normal exit unloads
    /// WPF's C++/CLI modules, and that needs a DLL from the removed folder: the test run
    /// failed there with DllNotFoundException after the new copy was already up.
    /// </summary>
    public static void EndNow() => TerminateProcess(GetCurrentProcess(), 0);

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern unsafe bool CreateProcessW(string? app, char* cmdLine, IntPtr procAttr, IntPtr threadAttr,
        bool inherit, uint flags, IntPtr env, string? cwd, StartupInfo* si, ProcessInformation* pi);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
