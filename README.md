# Rimlight

Screen-driven ambient lighting for Windows: it samples the edges of a monitor and drives an
addressable LED strip over a serial link, using the Adalight protocol that existing
Arduino firmware already speaks.

The hardware side and the original idea come from AlexGyver's
[Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight) - this project replaces
only the PC half of that setup, and talks to the same firmware unchanged.

It was written to replace [Prismatik](https://github.com/psieg/Lightpack), which kept going
dark during games. The interesting part turned out to be *why* that happens, and most of the
design follows from it.

## What makes it different

**Capture never silently stops.** Prismatik picks one capture method and freezes when it
stops delivering. Rimlight runs Desktop Duplication as the primary path and falls back to
Windows Graphics Capture, then to GDI, whenever the fast paths go quiet - so the strip keeps
following the screen instead of freezing and then blanking.

**Idle is not starvation.** A still screen legitimately produces no frames. Telling that
apart from a genuinely stalled capture path is most of what the fallback logic does; getting
it wrong makes the source flap and the mouse cursor stutter.

**The GPU work stays on the GPU.** Frames are reduced through hardware mip generation and
read back non-blocking through a ring of staging buffers - roughly 4 KB per frame crosses
the bus instead of a 20 MB frame. A blocking readback stalls behind the game's own GPU work
and costs seconds under load.

**Colour maths happens in linear light.** Sampling, white balance, saturation and blending
are done on linear values and only encoded at the end, so bright scenes do not dim oddly.

## Requirements

- Windows 10 2004 or newer (Windows 11 recommended)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- An Adalight-compatible LED controller on a serial port - for example an Arduino running
  [Gyver_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight) or any other Adalight
  firmware. The firmware is not part of this repository.

The frame format on the wire is stock Adalight:

```
'A' 'd' 'a'  hi  lo  chk        chk = hi ^ lo ^ 0x55,  hi/lo encode (N - 1)
then N x (R, G, B)
```

At 1 Mbaud a 122-LED frame is 372 bytes, so the link tops out around 268 fps - far more
headroom than capture will ever need.

## Building

```bash
dotnet build src/Rimlight/Rimlight.csproj -c Release
```

A single-file build:

```bash
dotnet publish src/Rimlight -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

Add `--self-contained true` for a machine without the .NET runtime installed.

## Setting it up

1. **Device** - pick the monitor and the serial port, press *Apply and reconnect*.
2. **Layout** - enter the LED count per side, then the start corner and direction. Use
   *Show map on screen*: it draws the sampling zones over the real screen with numbers, and
   clicking a cell lights it green. Because the map is captured like anything else, a green
   LED confirms the whole chain - geometry, numbering and colour - not just the numbering.
3. Fine-tune with **Offset**, which shifts the mapping along the strip. A start corner alone
   cannot say where the first LED physically sits.
4. **Colour** - temperature and the per-channel gains match the wall behind the monitor;
   saturation, gamma and the darkness threshold are taste.

The LED total must match `NUM_LEDS` in the firmware. Stock Adalight sketches read a fixed
number of bytes regardless of the header, so a mismatch rotates the picture around the strip.

Settings, log and translations live in `%APPDATA%\Rimlight\`. Translations are plain JSON
and can be edited or extended.

## If capture stalls in games

Check **Hardware-accelerated GPU scheduling** first: Settings → System → Display → Graphics
→ Default graphics settings.

With it enabled, a demanding game can saturate the GPU to the point where the Windows
compositor cannot keep up. Desktop Duplication and Windows Graphics Capture both read the
composition result, so they starve - long stalls, fallbacks to GDI, sometimes no frames at
all. Turning it off resolved this completely on the machine this was developed against
(RTX 4080, 3440x1440 @ 165 Hz): the GPU sits at 97-98% with no frame cap and capture stays
steady.

Note that DLSS Frame Generation requires that setting enabled, so the trade is not free.
Capping the game's frame rate slightly below what the GPU can deliver has the same effect
and costs less.

## Repository layout

```
src/Rimlight        the application: layout, colour, serial, UI
src/Rimlight.Core   capture backends, colour pipeline, frame bus, localisation
tools/CaptureProbe  diagnostic tool: every capture method side by side, with metrics
```

`tools/CaptureProbe` runs all capture backends at once against one monitor and reports
frame rate, interval percentiles, per-frame cost and a second-by-second history strip. It
exists because measuring beat guessing every single time; its README records the findings.

## Frame bus

With *Publish frames* enabled the reduced frames are exposed through shared memory, so a
second process can drive other lighting from the same capture without opening its own.
The layout is documented in `src/Rimlight.Core/Frames/FrameBus.cs`.

The shared-memory name is deliberately still `Local\AmbilightFrameBus`: it is a contract
with an existing consumer, and renaming it for cosmetic reasons would break that silently.

## Credits

- [AlexGyver / Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight) - the
  hardware build, the firmware this talks to, and the idea in the first place.
- [psieg / Lightpack (Prismatik)](https://github.com/psieg/Lightpack) - the predecessor.
  Reading its capture code was how several of the problems here were understood, including
  the ones it turned out not to have.
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) - Direct3D 11 and DXGI
  bindings for .NET.
- [Spout2](https://github.com/leadedge/Spout2) - texture sharing, used by one of the
  experimental capture backends in the probe.

## Licence

MIT - see [LICENSE](LICENSE).
