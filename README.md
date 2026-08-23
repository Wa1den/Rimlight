# Rimlight

Фоновая подсветка монитора для Windows. Программа захватывает изображение экрана,
усредняет цвет по зонам вдоль краёв и отправляет результат на адресную светодиодную ленту
через COM-порт по протоколу Adalight. Этот протокол поддерживают распространённые прошивки
для Arduino.

*[English version below](#rimlight-english)*

![Окно Rimlight: слева настройки, справа превью зон с номерами по краям и строка состояния с замерами захвата](pics/interface.jpg)

Железная часть и сама идея взяты из проекта AlexGyver
[Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight). Rimlight заменяет
только программу на компьютере и работает с той же прошивкой без изменений.

Написан на замену [Prismatik](https://github.com/psieg/Lightpack) — ради скорости и
гибкости захвата изображения.

## Как это работает

**Захват — лестницей из трёх методов.** Основной путь — Desktop Duplication; при
прекращении потока кадров источник переключается на Windows Graphics Capture, затем на
GDI, и возвращается обратно, когда быстрый путь оживает. Смена источника происходит
автоматически и видна в строке состояния.

**Простой отличается от сбоя.** Неподвижный экран не производит кадров — это штатное
состояние, а не повод для переключения. Логика различает «кадров нет, потому что картинка
не меняется» и «кадров нет, потому что метод перестал работать»; спорные случаи
разрешаются дешёвым контрольным снимком через GDI.

**Обработка кадра — на видеокарте.** Кадр уменьшается аппаратной генерацией мип-уровней,
результат читается через кольцо промежуточных буферов без блокировки: по шине передаётся
около 4 КБ на кадр вместо 20 МБ, и конвейер не останавливается, даже когда видеокарта
занята игрой.

**Цвет считается в линейном пространстве.** Усреднение, баланс белого, насыщенность и
сглаживание выполняются над линейными значениями, гамма-коррекция применяется в конце —
без этого усреднение занижает яркость на контрастных сценах.

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

Для проверки на настоящем кадре в `pics/Rainbow.jpg` лежит изображение с насыщенными
цветами во всех частях кадра. Установленное фоном рабочего стола, оно показывает
раскладку целиком: каждый участок ленты должен повторять цвет ближайшего к нему края
экрана.

Суммарное число диодов должно совпадать с `NUM_LEDS` в прошивке: стоковые скетчи Adalight
читают фиксированное число байт независимо от заголовка, поэтому при расхождении
изображение смещается вдоль ленты.

Настройки, лог и файлы переводов хранятся в `%APPDATA%\Rimlight\`. Переводы — обычные
JSON-файлы, их можно править и дополнять.

## Захват под нагрузкой GPU

Desktop Duplication и Windows Graphics Capture читают результат работы композитора
Windows (DWM). Композиция — обычная задача на видеокарте, и при полной загрузке GPU игрой
ей может не доставаться времени: оба метода тогда перестают выдавать кадры, оставаясь
формально исправными. Симптомы — рост p99 интервала между кадрами и частые переходы на
GDI в строке состояния.

На частоту композиции влияют два фактора:

- **Планирование GPU с аппаратным ускорением** (Параметры → Система → Дисплей → Графика →
  Настройки графики по умолчанию). При включённом планировании распределением задач
  занимается сама видеокарта, и композитор конкурирует с кадрами игры на общих
  основаниях; при выключенном — планировщик Windows, который вклинивает его задачи
  охотнее. Наблюдение с одной конфигурации (RTX 4080, 3440×1440, 165 Гц): при выключенной
  настройке захват стабилен при загрузке GPU 97–98%. Генерация кадров DLSS требует
  включённого аппаратного планирования, поэтому размен доступен не всегда.
- **Запас по загрузке GPU.** Ограничение частоты кадров игры ниже фактического потолка
  оставляет композитору время независимо от планировщика.

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

![The Rimlight window: settings on the left, the zone preview with numbered cells along the edges and a status area with capture metrics on the right](pics/interface.jpg)

The hardware side and the original idea come from AlexGyver's
[Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight). Rimlight replaces only
the PC program and works with the same firmware unchanged.

Written as a replacement for [Prismatik](https://github.com/psieg/Lightpack), aiming at
faster and more flexible screen capture.

## How it works

**Capture is a ladder of three methods.** Desktop Duplication is the primary path; when
frames stop arriving the source switches to Windows Graphics Capture, then to GDI, and
returns once the fast path recovers. Switching is automatic and visible in the status area.

**Idle is distinguished from failure.** A still screen produces no frames — that is a
normal state, not a reason to switch. The logic separates "no frames because nothing
changes" from "no frames because the method stopped working"; ambiguous cases are resolved
with a cheap GDI probe.

**Frame processing stays on the GPU.** Frames are downscaled with hardware mip generation
and read back through a non-blocking ring of staging buffers: about 4 KB per frame crosses
the bus instead of 20 MB, and the pipeline keeps moving even while the GPU is busy with a
game.

**Colour maths in linear space.** Averaging, white balance, saturation and smoothing are
done on linear values; gamma encoding is applied at the end — otherwise averaging
understates brightness on high-contrast scenes.

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

For a check against a real frame, `pics/Rainbow.jpg` is an image with saturated colours
in every part of the frame. Set as the desktop background, it shows the whole layout at
once: every part of the strip should repeat the colour of the screen edge nearest to it.

The LED total must match `NUM_LEDS` in the firmware: stock Adalight sketches read a fixed
number of bytes regardless of the header, so a mismatch shifts the picture along the strip.

Settings, the log and translation files are stored in `%APPDATA%\Rimlight\`. Translations
are plain JSON files and can be edited or added.

## Capture under GPU load

Desktop Duplication and Windows Graphics Capture both read the output of the Windows
compositor (DWM). Composition is ordinary GPU work, and when a game saturates the GPU it
can be starved of time: both methods then stop delivering frames while remaining formally
healthy. The symptoms are a growing p99 frame interval and frequent GDI fallbacks in the
status area.

Two factors affect how often composition gets to run:

- **Hardware-accelerated GPU scheduling** (Settings → System → Display → Graphics →
  Default graphics settings). With it enabled, work scheduling is handled by the GPU
  itself and the compositor competes with game frames on equal terms; with it disabled,
  the Windows scheduler slots its work in more readily. Observed on one configuration
  (RTX 4080, 3440×1440, 165 Hz): with the setting off, capture stays stable at 97–98% GPU
  load. DLSS Frame Generation requires the setting enabled, so this trade-off is not
  always available.
- **GPU headroom.** Capping the game's frame rate below its actual ceiling leaves the
  compositor time regardless of the scheduler.

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
