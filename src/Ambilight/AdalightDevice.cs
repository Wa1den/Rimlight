using System;
using System.IO.Ports;
using System.Threading;
using Ambilight.Capture;
using Ambilight.Text;

namespace Ambilight;

/// <summary>
/// Speaks the protocol the existing Gyver_Ambilight firmware already uses, unchanged:
///
///   'A' 'd' 'a'  hi  lo  chk        chk = hi ^ lo ^ 0x55,  hi/lo encode (N - 1)
///   then N x (R, G, B)
///
/// At 1 Mbaud a 122-LED frame is 372 bytes, so the wire tops out around 268 fps - four
/// times more headroom than the capture side will ever use.
/// </summary>
public sealed class AdalightDevice : IDisposable
{
    /// <summary>Opening the port pulses DTR and reboots the Nano into its bootloader.</summary>
    const int BootloaderWaitMs = 2500;

    SerialPort? _port;
    byte[] _frame = Array.Empty<byte>();
    byte[] _lastSent = Array.Empty<byte>();
    int _ledCount;
    long _lastSendTicks;
    bool _everSent;
    string _lastFailure = "";

    public bool IsOpen => _port?.IsOpen == true;

    /// <summary>
    /// Set when the port could not be opened or dropped mid-stream.
    ///
    /// A flag rather than a substring check on the status: the warning used to look for the
    /// Russian word for "error" inside the message, which quietly stopped working the
    /// moment the interface could be switched to English.
    /// </summary>
    public bool HasError { get; private set; }
    public string Status { get; private set; } = Loc.P("не подключено", "not connected");
    public long Reconnects { get; private set; }
    public long FramesSent { get; private set; }
    public long FramesSkipped { get; private set; }

    /// <param name="waitBootloader">
    /// Opening a closed port can pulse DTR and reboot the Nano, so a first connection waits
    /// it out. Reopening only because the LED count changed does not need that pause: the
    /// firmware resynchronises on the next "Ada" header anyway, and a 2.5 s blackout on
    /// every keystroke in the count field would be unusable.
    /// </param>
    public bool Open(string portName, int baud, int ledCount, bool waitBootloader = true)
    {
        Close();
        _ledCount = ledCount;

        int payload = ledCount * 3;
        _frame = new byte[6 + payload];
        _lastSent = new byte[payload];
        _everSent = false;

        // header is constant for a given LED count, so build it once
        int n = ledCount - 1;
        _frame[0] = (byte)'A'; _frame[1] = (byte)'d'; _frame[2] = (byte)'a';
        _frame[3] = (byte)((n >> 8) & 0xFF);
        _frame[4] = (byte)(n & 0xFF);
        _frame[5] = (byte)(_frame[3] ^ _frame[4] ^ 0x55);

        try
        {
            _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            {
                // leaving these asserted resets the board on every open
                DtrEnable = false,
                RtsEnable = false,
                Handshake = Handshake.None,
                WriteTimeout = 500,
                WriteBufferSize = Math.Max(4096, _frame.Length * 4)
            };
            _port.Open();
        }
        catch (Exception ex)
        {
            Status = Loc.P("ошибка открытия: ", "could not open: ") + ex.Message;
            HasError = true;

            // retries run every couple of seconds; logging each one buries everything else
            if (_lastFailure != ex.Message)
            {
                _lastFailure = ex.Message;
                ProbeLog.Log(Loc.P("порт", "port"),
                             $"{portName} " + Loc.P("не открылся: ", "failed to open: ") + ex.Message +
                             (ex.Message.Contains("denied") ? Loc.P(" (порт занят другой программой?)", " (held by another program?)") : ""));
            }
            _port = null;
            return false;
        }

        _lastFailure = "";
        HasError = false;
        if (waitBootloader)
        {
            Status = $"{portName}: " + Loc.P("жду загрузчик", "waiting for bootloader");
            ProbeLog.Log(Loc.P("порт", "port"), $"{portName} " + Loc.P($"открыт на {baud} бод, пауза {BootloaderWaitMs} мс на загрузчик", $"opened at {baud} baud, {BootloaderWaitMs} ms bootloader pause"));
            Thread.Sleep(BootloaderWaitMs);
        }
        else
        {
            ProbeLog.Log(Loc.P("порт", "port"), $"{portName} " + Loc.P($"переоткрыт под {ledCount} диодов", $"reopened for {ledCount} LEDs"));
        }

        Status = $"{portName} " + Loc.P("готов", "ready");
        return true;
    }

    /// <param name="rgb">3 bytes per LED, already colour-corrected.</param>
    /// <param name="onlyOnChange">
    /// Skips identical frames - but the firmware blanks the strip after OFF_TIME (10 s)
    /// of silence, so a keepalive resend is required, not a nicety.
    /// </param>
    public bool Send(byte[] rgb, bool onlyOnChange, int keepAliveMs)
    {
        if (_port is not { IsOpen: true }) return false;

        int payload = _ledCount * 3;
        long now = Environment.TickCount64;

        if (onlyOnChange && _everSent)
        {
            bool same = true;
            int check = Math.Min(rgb.Length, payload);
            for (int i = 0; i < check; i++)
                if (_lastSent[i] != rgb[i]) { same = false; break; }

            if (same && now - _lastSendTicks < keepAliveMs)
            {
                FramesSkipped++;
                return true;
            }
        }

        // the caller's buffer can briefly disagree with ours while the layout is being
        // edited; send what fits rather than throwing
        int copy = Math.Min(rgb.Length, payload);
        Buffer.BlockCopy(rgb, 0, _frame, 6, copy);
        if (copy < payload) Array.Clear(_frame, 6 + copy, payload - copy);

        try
        {
            _port.Write(_frame, 0, _frame.Length);
        }
        catch (Exception ex)
        {
            Status = Loc.P("обрыв: ", "link lost: ") + ex.Message;
            HasError = true;
            ProbeLog.Log(Loc.P("порт", "port"), Loc.P("запись не удалась: ", "write failed: ") + ex.Message);
            Close();
            return false;
        }

        Buffer.BlockCopy(rgb, 0, _lastSent, 0, copy);
        _lastSendTicks = now;
        _everSent = true;
        FramesSent++;
        return true;
    }

    /// <summary>Sends one all-black frame, used on exit, lock, sleep and display off.</summary>
    public void Blackout()
    {
        if (_port is not { IsOpen: true }) return;
        var black = new byte[_ledCount * 3];
        Send(black, onlyOnChange: false, keepAliveMs: 0);
    }

    public bool TryReconnect(string portName, int baud, int ledCount)
    {
        foreach (var name in SerialPort.GetPortNames())
            if (string.Equals(name, portName, StringComparison.OrdinalIgnoreCase))
            {
                bool ok = Open(portName, baud, ledCount);
                if (ok)
                {
                    Reconnects++;
                    ProbeLog.Log(Loc.P("порт", "port"), $"{portName} " + Loc.P($"переподключён (#{Reconnects})", $"reconnected (#{Reconnects})"));
                }
                return ok;
            }
        return false;
    }

    public void Close()
    {
        try { _port?.Close(); } catch { /* already gone */ }
        _port?.Dispose();
        _port = null;
        Status = Loc.P("не подключено", "not connected");
    }

    public void Dispose() => Close();
}
