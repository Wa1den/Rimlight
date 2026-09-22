using System;
using System.Collections.Generic;
using System.IO.Ports;
using Microsoft.Win32;

namespace Rimlight;

/// <summary>
/// Serial ports with the USB identity behind them, so each protocol lists only the boards
/// that can speak it.
///
/// Sorted by vendor rather than by asking the device: sending a query to every port on the
/// machine means writing unasked-for bytes into whatever else is plugged in - a printer, a
/// modem, the Nano itself, which a raised DTR would reboot. The vendor is enough for the
/// split that matters here. RP2040 and ESP32-S2 carry USB on the chip and cannot be running
/// stock Adalight; everything behind a USB-to-UART bridge is left under Adalight.
///
/// Read from the registry rather than WMI: every USB serial device Windows has seen records
/// its COM name under Enum\USB\VID_xxxx&amp;PID_xxxx\&lt;instance&gt;\Device Parameters,
/// readable without elevation and without pulling in System.Management. Only names that
/// SerialPort reports as present right now are kept - the registry also remembers devices
/// unplugged long ago.
///
/// Shared with tools/AwaProbe; keep it free of anything else from the application.
/// </summary>
public static class SerialDevices
{
    /// <summary>Raspberry Pi: RP2040 and RP2350 on the stock USB stack.</summary>
    public const string RaspberryPiVid = "2E8A";

    /// <summary>Espressif: the ESP32-S2 and later with USB on the chip, no bridge.</summary>
    public const string EspressifVid = "303A";

    public sealed record Port(string Name, string Vid, string Pid)
    {
        public DeviceProtocol Protocol =>
            Vid is RaspberryPiVid or EspressifVid ? DeviceProtocol.Awa : DeviceProtocol.Adalight;
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

    /// <summary>Names of the ports present now whose board fits the protocol.</summary>
    public static List<string> For(DeviceProtocol protocol)
    {
        var names = new List<string>();
        foreach (var p in Present())
            if (p.Protocol == protocol) names.Add(p.Name);
        return names;
    }

    static string Field(string s, string key)
    {
        int i = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        return i < 0 || i + key.Length + 4 > s.Length ? "" : s.Substring(i + key.Length, 4).ToUpperInvariant();
    }

    static int Number(string name) =>
        name.Length > 3 && int.TryParse(name.AsSpan(3), out int n) ? n : int.MaxValue;
}
