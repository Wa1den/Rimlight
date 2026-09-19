using System;
using System.Collections.Generic;
using System.IO.Ports;
using Microsoft.Win32;

namespace AwaProbe;

/// <summary>
/// Serial ports with the USB identity behind them, so the controller can be picked out of
/// the list instead of guessed at.
///
/// Read from the registry rather than WMI: every USB serial device Windows has seen records
/// its COM name under Enum\USB\VID_xxxx&amp;PID_xxxx\&lt;instance&gt;\Device Parameters,
/// readable without elevation and without pulling in System.Management. Only names that
/// SerialPort reports as present right now are kept - the registry also remembers devices
/// that were unplugged long ago.
/// </summary>
static class Ports
{
    /// <summary>Raspberry Pi's vendor ID, which every RP2040 running the stock USB stack uses.</summary>
    public const string RaspberryPiVid = "2E8A";

    public sealed record Port(string Name, string Vid, string Pid)
    {
        public bool IsRp2040 => Vid == RaspberryPiVid;
    }

    public static List<Port> Present()
    {
        var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, Port>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb != null)
                foreach (var device in usb.GetSubKeyNames())
                {
                    // VID_2E8A&PID_000A, or a composite child such as VID_2E8A&PID_000A&MI_00
                    string vid = Field(device, "VID_"), pid = Field(device, "PID_");
                    if (vid.Length == 0) continue;

                    using var deviceKey = usb.OpenSubKey(device);
                    if (deviceKey == null) continue;

                    foreach (var instance in deviceKey.GetSubKeyNames())
                    {
                        using var parameters = deviceKey.OpenSubKey(instance + @"\Device Parameters");
                        if (parameters?.GetValue("PortName") is string name && present.Contains(name))
                            found[name] = new Port(name, vid, pid);
                    }
                }
        }
        catch
        {
            // без реестра остаётся просто список имён
        }

        foreach (var name in present)
            if (!found.ContainsKey(name)) found[name] = new Port(name, "", "");

        var list = new List<Port>(found.Values);
        list.Sort((a, b) => Number(a.Name).CompareTo(Number(b.Name)));
        return list;
    }

    static string Field(string s, string key)
    {
        int i = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        return i < 0 || i + key.Length + 4 > s.Length ? "" : s.Substring(i + key.Length, 4).ToUpperInvariant();
    }

    static int Number(string name) =>
        int.TryParse(name.AsSpan(3), out int n) ? n : int.MaxValue;
}
