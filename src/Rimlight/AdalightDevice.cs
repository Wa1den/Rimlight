using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using Rimlight.Capture;
using Rimlight.Text;

namespace Rimlight;

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

    /// <summary>Frames thrown away because the previous one had not left the port yet.</summary>
    public long FramesDropped { get; private set; }

    /// <summary>
    /// How many frames in a row may be dropped before one is forced through.
    ///
    /// Dropping is right when the port is momentarily behind, but if it is permanently
    /// slower than we produce - the classic mismatched baud rate in the firmware - then
    /// dropping everything would blank the strip after the firmware's 10 s timeout.
    /// Letting one through periodically keeps it lit and the fault visible.
    /// </summary>
    const int MaxDropStreak = 3;

    int _dropStreak;

    /// <summary>
    /// Shortest gap the controller can survive between frames, worked out from the baud
    /// rate and the LED count in <see cref="Open"/>.
    ///
    /// The strip cannot absorb frames faster than it can read and show one, and sending
    /// faster is not merely wasted: FastLED drives WS2812 with interrupts off, the AVR
    /// receive buffer is 64 bytes, and a frame that arrives during those milliseconds
    /// loses bytes and is thrown away whole when the firmware hunts for the next header.
    /// Output is paced well below this, but it may hand over two frames close together
    /// when one arrives just as a period ends - which is exactly the case to swallow.
    /// </summary>
    long _minGapTicks;

    long _lastSendStamp;

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

        // 10 bits per byte on the wire, 30 us per WS2812 LED to latch
        double wireMs = _frame.Length * 10.0 * 1000.0 / Math.Max(1, baud);
        double showMs = ledCount * 0.030;
        _minGapTicks = (long)((wireMs + showMs) * Stopwatch.Frequency / 1000.0);

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
    /// <param name="force">
    /// Ignores both pacing guards. Blacking out the strip happens once, on the way out,
    /// and must not be the frame that gets dropped.
    /// </param>
    public bool Send(byte[] rgb, bool onlyOnChange, int keepAliveMs, bool force = false)
    {
        if (_port is not { IsOpen: true }) return false;

        int payload = _ledCount * 3;
        long now = Environment.TickCount64;
        long nowStamp = Stopwatch.GetTimestamp();

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

        // Closer together than the controller's own cycle is worse than not sending at
        // all - see _minGapTicks. TickCount64 would not do here: it moves in 15.6 ms steps.
        if (!force && _everSent && nowStamp - _lastSendStamp < _minGapTicks)
        {
            FramesDropped++;
            return true;
        }

        // Windows takes the write into the driver's queue and returns, so nothing here
        // notices when the strip has fallen behind: the buffer holds about eleven frames,
        // and every one of them sitting in it is latency the eye sees. If the previous
        // frame has not left the port yet, drop this one - the next tick carries newer
        // colours anyway, and stale colours are worth less than no colours.
        try
        {
            if (!force && _everSent && _dropStreak < MaxDropStreak && _port.BytesToWrite >= _frame.Length)
            {
                _dropStreak++;
                FramesDropped++;
                return true;
            }
        }
        catch { /* the port can disappear between the check and the write */ }
        _dropStreak = 0;

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
        _lastSendStamp = nowStamp;
        _everSent = true;
        FramesSent++;
        return true;
    }

    /// <summary>Sends one all-black frame, used on exit, lock, sleep and display off.</summary>
    public void Blackout()
    {
        if (_port is not { IsOpen: true }) return;
        var black = new byte[_ledCount * 3];
        Send(black, onlyOnChange: false, keepAliveMs: 0, force: true);
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
