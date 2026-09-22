using System;

namespace AwaProbe;

/// <summary>
/// The controller's frame parser, transcribed from HyperSerialPico include/main.h, so that
/// the encoder can be checked with no hardware attached: a frame this accepts is a frame
/// the controller accepts.
///
/// The RGB path only. A 'w' 'A' header marks the calibrated variant, which carries four
/// calibration bytes after the pixels; they are counted into the checksum and skipped,
/// the same as the firmware does. The RGBW and 32-bit paths are not transcribed.
/// </summary>
sealed class FirmwareParser
{
    enum State { A, SmallW, SmallA, Hi, Lo, Crc, R, G, B, Calibration, F1, F2, FExt }

    State _s = State.A;
    int _count, _crc, _led, _leds, _calibration;
    int _f1, _f2, _ext;
    byte _position, _r, _g;
    bool _calibrated;
    byte[] _pixels = Array.Empty<byte>();

    public int Accepted { get; private set; }
    public int Queries { get; private set; }
    public byte[] LastFrame { get; private set; } = Array.Empty<byte>();

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) Step(b);
    }

    void Step(byte input)
    {
        switch (_s)
        {
            case State.A:
                if (input == 'A') _s = State.SmallW;
                break;

            case State.SmallW:
                _s = input == 'w' ? State.SmallA : State.A;
                break;

            case State.SmallA:
                _calibrated = input == 'A';
                _s = input == 'a' || input == 'A' ? State.Hi : State.A;
                break;

            case State.Hi:
                _count = input * 0x100;
                _crc = input;
                _f1 = _f2 = _ext = 0;
                _position = 0;
                _led = 0;
                _s = State.Lo;
                break;

            case State.Lo:
                _count += input;
                _crc = _crc ^ input ^ 0x55;
                _s = State.Crc;
                break;

            case State.Crc:
                if (_crc == input)
                {
                    _leds = _count + 1;
                    if (_leds > 4096) { _s = State.A; break; }
                    _pixels = new byte[_leds * 3];
                    _s = State.R;
                }
                else
                {
                    if (_count == 0x2aa2 && (input == 0x15 || input == 0x35)) Queries++;
                    _s = State.A;
                }
                break;

            case State.R:
                _r = input; Add(input); _s = State.G;
                break;

            case State.G:
                _g = input; Add(input); _s = State.B;
                break;

            case State.B:
                Add(input);
                _pixels[_led * 3] = _r;
                _pixels[_led * 3 + 1] = _g;
                _pixels[_led * 3 + 2] = input;
                _led++;
                if (_led < _leds) _s = State.R;
                else if (_calibrated) { _calibration = 0; _s = State.Calibration; }
                else _s = State.F1;
                break;

            case State.Calibration:
                Add(input);
                if (++_calibration == 4) _s = State.F1;
                break;

            case State.F1:
                _s = input == _f1 ? State.F2 : State.A;
                break;

            case State.F2:
                _s = input == _f2 ? State.FExt : State.A;
                break;

            case State.FExt:
                if (input == (_ext != 0x41 ? _ext : 0xaa))
                {
                    Accepted++;
                    LastFrame = _pixels;
                }
                _s = State.A;
                break;
        }
    }

    void Add(byte input)
    {
        _f1 = (_f1 + input) % 255;
        _f2 = (_f2 + _f1) % 255;
        _ext = (_ext + (input ^ _position++)) % 255;
    }
}
