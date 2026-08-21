# Rimlight

Фоновая подсветка монитора для Windows. Программа захватывает изображение экрана,
усредняет цвет по зонам вдоль краёв и отправляет результат на адресную светодиодную ленту
через COM-порт по протоколу Adalight. Этот протокол поддерживают распространённые прошивки
для Arduino.

*[English version below](#rimlight-english)*

Железная часть и сама идея взяты из проекта AlexGyver
[Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight). Rimlight заменяет
только программу на компьютере и работает с той же прошивкой без изменений.

Проект написан на замену [Prismatik](https://github.com/psieg/Lightpack), у которого в
играх регулярно гасла подсветка. Разбор причин этого определил основную часть архитектуры.

## Отличия от Prismatik

**Резервные методы захвата.** Prismatik использует один метод захвата и перестаёт
обновлять подсветку, когда тот прекращает выдавать кадры. Rimlight по умолчанию работает
через Desktop Duplication, а при отсутствии кадров переключается на Windows Graphics
Capture и затем на GDI, поэтому подсветка продолжает обновляться.

**Различение простоя и сбоя.** Неподвижный экран не производит новых кадров, и это
нормальная ситуация. Основная задача логики переключения — отличать её от действительно
неработающего метода захвата: ошибка приводит к лишним сменам источника и заметна по
подёргиванию курсора мыши.

**Обработка кадра на видеокарте.** Кадр уменьшается аппаратной генерацией мип-уровней, а
результат читается через кольцо промежуточных буферов без блокировки: по шине передаётся
около 4 КБ на кадр вместо 20 МБ. Блокирующее чтение ждёт в очереди за работой игры и под
нагрузкой занимает секунды.

**Расчёт цвета в линейном пространстве.** Усреднение, баланс белого, насыщенность и
сглаживание выполняются над линейными значениями, гамма-коррекция применяется в конце.
Без этого яркие сцены выглядят тусклее, чем должны.

## Что нужно

- Windows 10 2004 или новее (лучше Windows 11)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- Контроллер ленты на COM-порту с прошивкой, поддерживающей Adalight — например Arduino с
  [Gyver_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight). Прошивка в этот
  репозиторий не входит.

Формат кадра на проводе — стоковый Adalight:

```
'A' 'd' 'a'  hi  lo  chk        chk = hi ^ lo ^ 0x55,  hi/lo кодируют (N - 1)
далее N x (R, G, B)
```

На скорости 1 Мбод кадр для 122 диодов занимает 372 байта, что даёт максимум около
268 кадров в секунду — значительно больше, чем выдаёт захват.

## Сборка

```bash
dotnet build src/Rimlight/Rimlight.csproj -c Release
```

Сборка в один файл:

```bash
dotnet publish src/Rimlight -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

Для машины без установленного рантайма .NET добавить `--self-contained true`.

## Настройка

1. **Устройство** — выбрать монитор и COM-порт, нажать «Применить и переподключиться».
2. **Раскладка** — задать число диодов по сторонам, стартовый угол и направление. Кнопка
   **«Показать схему на экране»** накладывает пронумерованные зоны выборки поверх экрана;
   клик по ячейке подсвечивает её зелёным. Схема попадает в захват как обычное
   изображение, поэтому зелёный диод на ленте подтверждает сразу геометрию, нумерацию и
   цветопередачу.
3. Точная подгонка выполняется ползунком **«Смещение»**: стартовый угол сам по себе не
   определяет положение первого диода — оно зависит ещё и от того, по какой стороне лента
   уходит из угла.
4. **Цвет** — температура и усиления по каналам подбираются под цвет стены за монитором;
   насыщенность, гамма и порог темноты настраиваются по вкусу.

Суммарное число диодов должно совпадать с `NUM_LEDS` в прошивке: стоковые скетчи Adalight
читают фиксированное число байт независимо от заголовка, поэтому при расхождении
изображение смещается вдоль ленты.

Настройки, лог и файлы переводов хранятся в `%APPDATA%\Rimlight\`. Переводы — обычные
JSON-файлы, их можно править и дополнять.

## Проблемы захвата в играх

В первую очередь проверить настройку **«Планирование графического процессора с аппаратным
ускорением»**: Параметры → Система → Дисплей → Графика → Настройки графики по умолчанию.

Когда она включена, требовательная игра может загрузить видеокарту настолько, что
композитор Windows перестаёт успевать. Desktop Duplication и Windows Graphics Capture
читают результат композиции, поэтому остаются без кадров: появляются длинные паузы,
переходы на GDI, иногда кадры пропадают полностью. На машине, где велась разработка
(RTX 4080, 3440×1440, 165 Гц), отключение настройки устранило проблему: видеокарта
загружена на 97–98% без ограничения частоты кадров, захват при этом стабилен.

Генерация кадров DLSS работает только при включённой настройке. В этом случае вместо её
отключения можно ограничить частоту кадров в игре чуть ниже возможностей видеокарты —
эффект тот же.

## Структура репозитория

```
src/Rimlight        приложение: раскладка, цвет, COM-порт, интерфейс
src/Rimlight.Core   бэкенды захвата, конвейер цвета, шина кадров, локализация
tools/CaptureProbe  диагностика: все методы захвата рядом, с замерами
```

`tools/CaptureProbe` запускает все бэкенды захвата одновременно на одном мониторе и
показывает частоту кадров, перцентили интервалов, стоимость кадра по стадиям и
посекундную историю. Результаты измерений собраны в его README.

## Шина кадров

Если включена опция «Отдавать кадры», уменьшенные кадры публикуются в разделяемую память:
второй процесс может использовать тот же захват для другой подсветки, не открывая
собственный. Формат описан в `src/Rimlight.Core/Frames/FrameBus.cs`.

Имя разделяемой памяти оставлено прежним — `Local\AmbilightFrameBus`. Его использует
существующий потребитель, и переименование нарушило бы совместимость.

## Благодарности

- [AlexGyver / Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight) —
  конструкция железа, прошивка и сама идея.
- [psieg / Lightpack (Prismatik)](https://github.com/psieg/Lightpack) — предшественник;
  его код захвата помог разобраться в части описанных здесь проблем.
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) — привязки Direct3D 11
  и DXGI для .NET.
- [Spout2](https://github.com/leadedge/Spout2) — обмен текстурами; используется одним из
  экспериментальных бэкендов в CaptureProbe.

## Лицензия

MIT — см. [LICENSE](LICENSE).

---

<a name="rimlight-english"></a>

# Rimlight (English)

Screen-driven ambient lighting for Windows. The program captures the screen, averages
colours over zones along the edges and sends the result to an addressable LED strip over a
serial port using the Adalight protocol. The protocol is supported by common Arduino
firmware.

The hardware side and the original idea come from AlexGyver's
[Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight). Rimlight replaces only
the PC program and works with the same firmware unchanged.

The project was written to replace [Prismatik](https://github.com/psieg/Lightpack), which
regularly went dark during games. Working out the causes of that shaped most of the
architecture.

## Differences from Prismatik

**Fallback capture methods.** Prismatik uses a single capture method and stops updating the
lights when it stops delivering frames. Rimlight uses Desktop Duplication by default and,
when frames stop arriving, switches to Windows Graphics Capture and then to GDI, so the
lighting keeps updating.

**Telling idle from failure.** A still screen produces no new frames, which is normal. The
main job of the switching logic is to distinguish that from a capture method that has
actually stopped working: getting it wrong causes unnecessary source switches, visible as
mouse cursor stutter.

**Frame processing on the GPU.** The frame is downscaled with hardware mip generation and
the result is read back through a ring of staging buffers without blocking: about 4 KB per
frame crosses the bus instead of 20 MB. A blocking readback waits in line behind the game's
GPU work and takes seconds under load.

**Colour maths in linear space.** Averaging, white balance, saturation and smoothing are
done on linear values; gamma encoding is applied at the end. Without this, bright scenes
look dimmer than they should.

## Requirements

- Windows 10 2004 or newer (Windows 11 recommended)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- An Adalight-compatible LED controller on a serial port — for example an Arduino running
  [Gyver_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight). The firmware is not
  part of this repository.

The frame format on the wire is stock Adalight:

```
'A' 'd' 'a'  hi  lo  chk        chk = hi ^ lo ^ 0x55,  hi/lo encode (N - 1)
then N x (R, G, B)
```

At 1 Mbaud a 122-LED frame takes 372 bytes, which allows up to about 268 frames per
second — considerably more than capture produces.

## Building

```bash
dotnet build src/Rimlight/Rimlight.csproj -c Release
```

A single-file build:

```bash
dotnet publish src/Rimlight -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

Add `--self-contained true` for a machine without the .NET runtime installed.

## Setup

1. **Device** — pick the monitor and the serial port, press *Apply and reconnect*.
2. **Layout** — enter the LED count per side, the start corner and the direction. The
   *Show map on screen* button overlays numbered sampling zones on the screen; clicking a
   cell highlights it in green. The map is captured like any other image, so a green LED on
   the strip confirms the geometry, the numbering and the colour path at once.
3. Fine-tuning is done with the **Offset** slider: the start corner alone does not
   determine the position of the first LED — it also depends on which side the strip leaves
   the corner on.
4. **Colour** — temperature and the per-channel gains are matched to the wall behind the
   monitor; saturation, gamma and the darkness threshold are set to preference.

The LED total must match `NUM_LEDS` in the firmware: stock Adalight sketches read a fixed
number of bytes regardless of the header, so a mismatch shifts the picture along the strip.

Settings, the log and translation files are stored in `%APPDATA%\Rimlight\`. Translations
are plain JSON files and can be edited or added.

## Capture problems in games

Check **Hardware-accelerated GPU scheduling** first: Settings → System → Display → Graphics
→ Default graphics settings.

With it enabled, a demanding game can load the GPU to the point where the Windows
compositor cannot keep up. Desktop Duplication and Windows Graphics Capture read the
composition result, so they receive no frames: long gaps, fallbacks to GDI, sometimes no
frames at all. On the development machine (RTX 4080, 3440×1440, 165 Hz), disabling the
setting fixed the problem: the GPU runs at 97–98% with no frame cap and capture stays
stable.

DLSS Frame Generation only works with the setting enabled. In that case, instead of
disabling it, cap the game's frame rate slightly below what the GPU can deliver — the
effect is the same.

## Repository layout

```
src/Rimlight        the application: layout, colour, serial, UI
src/Rimlight.Core   capture backends, colour pipeline, frame bus, localisation
tools/CaptureProbe  diagnostic tool: every capture method side by side, with metrics
```

`tools/CaptureProbe` runs all capture backends at once on a single monitor and reports
frame rate, interval percentiles, per-stage frame cost and a second-by-second history. The
measurement results are collected in its README.

## Frame bus

With the *Publish frames* option enabled, the reduced frames are published to shared
memory: a second process can drive other lighting from the same capture without opening its
own. The format is described in `src/Rimlight.Core/Frames/FrameBus.cs`.

The shared-memory name is kept as `Local\AmbilightFrameBus`. An existing consumer uses it,
and renaming it would break compatibility.

## Credits

- [AlexGyver / Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight) — the
  hardware build, the firmware and the original idea.
- [psieg / Lightpack (Prismatik)](https://github.com/psieg/Lightpack) — the predecessor;
  its capture code helped in understanding some of the problems described here.
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) — Direct3D 11 and DXGI
  bindings for .NET.
- [Spout2](https://github.com/leadedge/Spout2) — texture sharing; used by one of the
  experimental capture backends in CaptureProbe.

## Licence

MIT — see [LICENSE](LICENSE).
