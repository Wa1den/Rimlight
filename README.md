# Rimlight

Фоновая подсветка монитора для Windows. Программа захватывает изображение экрана,
усредняет цвет по зонам вдоль его краёв и отправляет результат на адресную светодиодную
ленту за монитором. Цвета на ленте повторяют края экрана в играх, фильмах и на рабочем
столе, в окне и на весь экран.

Лента подключается через контроллер на COM-порту. Поддерживаются два протокола:

- **Adalight** — стоковый протокол прошивок для Arduino, например
  [Gyver_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight);
- **AWA** — его расширение с контрольной суммой кадра, для плат с USB на кристалле:
  прошивки [HyperSerialPico](https://github.com/awawa-dev/HyperSerialPico) для RP2040 и
  [HyperSerialESP32](https://github.com/awawa-dev/HyperSerialESP32). Кадр идёт по USB без
  ограничений последовательного порта и поэтому доходит до ленты быстрее, а повреждённый в
  пути кадр отбрасывается и не показывается.

*[English version below](#rimlight-english)*

![Окно Rimlight: слева настройки, справа превью зон с номерами по краям и строка состояния с замерами захвата](pics/interface.jpg)

Железная часть и сама идея взяты из проекта AlexGyver
[Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight). По протоколу Adalight
Rimlight работает с той же прошивкой без изменений.

Написан на замену [Prismatik](https://github.com/psieg/Lightpack) — ради скорости и
гибкости захвата изображения. По замеру камерой на 240 кадров в секунду смена цвета доходит
до ленты за 10–20 мс с Arduino, а с контроллером AWA лента меняет цвет на несколько
миллисекунд раньше самого экрана.

## Возможности

**Два протокола контроллера.** Протокол выбирается в разделе «Устройство». Плата AWA
подключается без паузы на перезагрузку, число диодов берёт из заголовка кадра, а при
подключении сообщает версию своей прошивки. Раскладка, яркость, цвет и порт запоминаются для
каждого протокола отдельно, так что между двумя контроллерами можно переключаться без
перенастройки. В списке портов остаются только платы, подходящие протоколу: они различаются
по производителю USB, без опроса чужих устройств.

**Задержка.** Кадр забирается в момент, когда Windows его собрала, то есть раньше, чем его
показывает монитор: тому ещё предстоят ожидание развёртки, сама развёртка сверху вниз,
обработка и переключение пикселей матрицы. С Arduino на 1 Мбод путь до ленты длиннее этого,
с контроллером AWA — короче, поэтому лента опережает экран.

**Резервные методы захвата.** Основной метод — Desktop Duplication. Если он перестаёт
выдавать кадры, источник переключается на Windows Graphics Capture, затем на GDI, и
возвращается к предыдущему, когда тот снова выдаёт кадры. Текущий источник показан в
строке состояния.

**Различение простоя и сбоя.** Неподвижный экран не производит новых кадров, и это
нормальная ситуация, не требующая переключения. Логика разделяет два случая: кадров нет,
потому что изображение не меняется, и кадров нет, потому что метод перестал работать. В
неоднозначных случаях выполняется контрольный снимок через GDI.

**Обработка кадра на видеокарте.** Кадр уменьшается аппаратной генерацией мип-уровней,
результат читается через кольцо промежуточных буферов без блокировки: по шине передаётся
около 4 КБ на кадр вместо 20 МБ, а чтение не ожидает освобождения видеокарты.

**Расчёт цвета в линейном пространстве.** Усреднение, баланс белого, насыщенность и
сглаживание выполняются над линейными значениями, гамма-коррекция применяется в конце.
Без этого усреднение занижает яркость на контрастных сценах.

**Адаптивное кадрирование.** Фильм шире экрана идёт с чёрными полосами сверху и снизу, и
диоды вдоль них не светят. Границы картинки определяются по кадру, и зоны выборки
переносятся внутрь неё; картинка при этом может раскладываться по всей ленте, чтобы на ней
не оставалось тёмных участков. Субтитры и панель плеера рисуются поверх полосы, поэтому
строка проверяется по краям, отдельно от центра, а короткий светлый участок внутри полосы
границей не считается. Новые границы применяются, только если продержались заданное время,
иначе за полосу принималась бы тёмная сцена.

## Что нужно

- Windows 10 2004 или новее (лучше Windows 11)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- Контроллер ленты на COM-порту: Arduino с прошивкой Adalight, например
  [Gyver_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight), или плата на RP2040 с
  прошивкой [HyperSerialPico](https://github.com/awawa-dev/HyperSerialPico). Выход RP2040 даёт
  3,3 В, а ленте нужны 5, поэтому между ними ставится сдвигатель уровня — буфер вроде
  SN74AHCT125 — или берётся плата, где он уже есть. Прошивки в этот репозиторий не входят.

Формат кадра на проводе:

```
Adalight  'A' 'd' 'a'  hi  lo  chk     chk = hi ^ lo ^ 0x55,  hi/lo кодируют (N - 1)
          далее N x (R, G, B)

AWA       'A' 'w' 'a'  hi  lo  chk     то же
          далее N x (R, G, B), затем f1 f2 fext — суммы Флетчера по байтам пикселей
```

На скорости 1 Мбод кадр Adalight для 122 диодов занимает 372 байта, то есть 3,7 мс на
проводе; вместе с 3,7 мс на защёлкивание ленты это даёт максимум около 135 кадров в
секунду. Плата AWA получает кадр по USB за доли миллисекунды и выводит его на ленту, пока
принимает следующий, поэтому её период — около 4 мс.

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

1. **Устройство** — выбрать монитор, протокол контроллера и COM-порт, нажать «Применить и
   переподключиться». Монитор запоминается по модели из EDID, поэтому перестановка кабеля
   между разъёмами видеокарты не переводит захват на другой экран. В списке портов только
   платы, подходящие протоколу; скорость порта задаётся только для Adalight. Смена протокола
   сразу переподключает ленту и подставляет запомненные для него раскладку, яркость, цвет и
   порт.
2. **Раскладка** — задать число диодов по сторонам, стартовый угол и направление. Кнопка
   **«Показать схему на экране»** накладывает пронумерованные зоны выборки поверх экрана;
   клик по ячейке подсвечивает её зелёным. Схема попадает в захват как обычное
   изображение, поэтому зелёный диод на ленте подтверждает сразу геометрию, нумерацию и
   цветопередачу.
3. Точная подгонка выполняется ползунком **«Смещение»**: стартовый угол сам по себе не
   определяет положение первого диода — оно зависит ещё и от того, по какой стороне лента
   уходит из угла.
4. **Яркость** — общий предел яркости и три настройки тёмного конца кадра.
   **«Порог темноты»** гасит зоны ниже заданного уровня, **«Обесцвечивание тёмного»**
   убирает с них оттенок баланса белого — без него чёрная полоса плеера при тёплом
   балансе светит тёмно-красным, — а **«Минимальная подсветка»** не даёт ленте гаснуть
   на чёрном кадре.
5. **Цвет** — температура и усиления по каналам подбираются под цвет стены за монитором.
6. **Кадрирование** — включает поиск чёрных полос, по умолчанию выключенный. Значения
   рассчитаны на фильмы 2.35:1 и 21:9. Если полосы теряются при всплывающей панели плеера,
   увеличивается **«Пропускать помехи»**; если кадрирование срабатывает на тёмных сценах —
   **«Задержка подтверждения»** и **«Минимальная полоса»**.
7. **Захват** — ползунок **«Максимум кадров в секунду»** задаёт, как часто кадр экрана
   сводится к цветам зон и уходит на ленту. Кадр, пришедший раньше этого срока,
   отбрасывается, поэтому ограничение добавляет к задержке вывода до одного своего
   периода: замер камерой на 240 кадров в секунду даёт около 30 мс от смены цвета на
   экране до смены на ленте при пределе 60 и 10–20 мс в положении **«без ограничения»**,
   которое стоит по умолчанию. Без ограничения частота упирается в период контроллера —
   около 7 мс на 122 диода у Arduino на 1 Мбод и около 4 мс у платы AWA. Ограничение уменьшает число сводов кадра на
   видеокарте, так что на слабой карте или в тяжёлой игре оно может оказаться полезным.
   Ползунок **«Размытие и резкость»** работает по кадру до выборки зон: влево кадр
   расфокусируется, и соседние участки ленты переливаются друг в друга, вправо соседние
   зоны расходятся по яркости. В нуле, как стоит по умолчанию, кадр не обрабатывается
   вовсе; на краю шкалы обработка добавляет к задержке около 2 мс.
8. **Питание** — ползунок **«Предел тока»** ограничивает ток всей ленты. Срабатывает
   только на светлых кадрах, поэтому на цветных сценах разницы обычно не видно.
   Выставляется по блоку питания: 120 диодов WS2812 на полном белом берут около 7 А.

Кнопка **«Стоп»** в полосе внизу окна останавливает вывод и гасит ленту, не закрывая
программу; повторное нажатие возвращает подсветку. Рядом с ней написано, что происходит
сейчас: идёт ли вывод и с какой частотой, остановлен ли он и почему, открылся ли порт.

Кнопка **«По умолчанию»** в разделе «Основное» возвращает настройки к стандартным, не
трогая монитор, протокол и порт, раскладку ленты, язык, положение окна и настройки другого
протокола.

Галка **«Проверять обновления при запуске»** в разделе «О программе» по умолчанию
выключена: это единственное обращение программы в сеть, и наружу уходит только номер
текущей версии.

Для проверки на настоящем кадре в `pics/Rainbow.jpg` лежит изображение с насыщенными
цветами во всех частях кадра. Установленное фоном рабочего стола, оно показывает
раскладку целиком: каждый участок ленты должен повторять цвет ближайшего к нему края
экрана.

Для Adalight суммарное число диодов должно совпадать с `NUM_LEDS` в прошивке: стоковые
скетчи читают фиксированное число байт независимо от заголовка, поэтому при расхождении
изображение смещается вдоль ленты. Прошивки AWA берут число диодов из заголовка кадра.

Настройки, лог и файлы переводов хранятся в `%APPDATA%\Rimlight\`. Когда лог дорастает
до 5 МБ, он переименовывается в `rimlight.old.log` и начинается заново, поэтому больше
10 МБ он не занимает.

## Локализация

В программу встроены два языка, русский и английский. При первом запуске они
записываются в `%APPDATA%\Rimlight\lang\` как `ru.json` и `en.json`, дальше читаются
именно эти файлы: правка в файле видна после перезапуска. При обновлении программы оба
встроенных файла перезаписываются, чтобы старый файл не скрыл новые формулировки;
признак этого — поле `_version`. Добавленные языки остаются как есть.

Свой перевод — это ещё один файл в той же папке:

1. Скопировать `en.json` под именем с кодом языка, например `de.json`: имя файла и есть
   код языка. Шаблоны обоих встроенных языков лежат в папке [lang](lang) репозитория.
2. Заменить значения на переведённые, ключи слева оставить как есть. `{0}` внутри
   строки — подстановка (версия, путь, число диодов), её нужно сохранить. В поле `_name`
   пишется название языка так, как оно должно выглядеть в списке, например `Deutsch`.
   Поле `_version` можно удалить: оно относится только к встроенным файлам.
3. Запустить программу — язык появится в списке в разделе «Основное».

Файл проверяется только на базовую структуру: он должен читаться как JSON вида
«строка: строка» и содержать хотя бы несколько знакомых ключей, иначе он не считается
переводом и пропускается с записью в лог. Переводить всё сразу не обязательно:
пропущенные ключи берутся из английского. У добавленного языка часть служебного текста
тоже остаётся английской — сообщения лога и подписи в статистике заданы в коде, а не в
JSON.

## Захват под нагрузкой GPU

Desktop Duplication и Windows Graphics Capture читают результат работы композитора
Windows (DWM). Композиция выполняется на видеокарте, и при полной загрузке GPU игрой
композитору может не хватать времени на выполнение. В этом случае оба метода перестают
выдавать кадры, при этом ошибок не возникает. Это проявляется как рост p99 интервала
между кадрами и частые переключения на GDI в строке состояния.

На частоту композиции влияют два фактора:

- **Планирование GPU с аппаратным ускорением** (Параметры → Система → Дисплей → Графика →
  Настройки графики по умолчанию). При включённой настройке распределением задач
  занимается видеокарта, и задачи композитора конкурируют с кадрами игры на общих
  основаниях. При выключенной распределением занимается планировщик Windows, который
  выделяет им время чаще. Измерение на одной конфигурации (RTX 4080, 3440×1440, 165 Гц):
  при выключенной настройке захват сохраняет стабильность при загрузке GPU 97–98%.
  Генерация кадров DLSS требует включённой настройки, поэтому такой вариант доступен не
  всегда.
- **Запас по загрузке GPU.** Ограничение частоты кадров игры ниже фактически достижимой
  оставляет композитору время независимо от планировщика.

## Структура репозитория

```
src/Rimlight        приложение: раскладка, цвет, COM-порт, интерфейс
src/Rimlight.Core   бэкенды захвата, конвейер цвета, шина кадров, локализация
tools/CaptureProbe  диагностика: все методы захвата рядом, с замерами
tools/LatencyProbe  замер задержки «экран → лента» камерой на 240 к/с
tools/AwaProbe      проверка контроллера AWA без основной программы
lang                шаблоны переводов, они же пишутся в %APPDATA%
```

`tools/CaptureProbe` запускает все бэкенды захвата одновременно на одном мониторе и
показывает частоту кадров, перцентили интервалов, стоимость кадра по стадиям и
посекундную историю. Результаты измерений собраны в его README.

`tools/LatencyProbe` выводит на экран ту же сетку зон, что читает движок, и раз в
несколько секунд перекрашивает её в очередной базовый цвет, показывая рядом таймер до
миллисекунд. Съёмка экрана и ленты одним кадром на 240 к/с даёт сквозную задержку
снаружи — вместе с прошивкой и матрицей, там где внутренняя статистика заканчивается на
записи в порт.

`tools/AwaProbe` проверяет плату с прошивкой HyperSerialPico отдельно от Rimlight:
запрашивает приветствие прошивки, проводит ленту через сплошные цвета и бегущую точку и
читает счётчики самого контроллера — сколько кадров принято и сколько пришло битыми. Так
неисправность железа отделяется от ошибки в том, что шлёт программа. Порядок проверки — в
его README.

## Шина кадров

Если включена опция «Отдавать кадры», уменьшенные кадры публикуются в разделяемую память:
второй процесс может использовать тот же захват для другой подсветки, не открывая
собственный. Формат описан в `src/Rimlight.Core/Frames/FrameBus.cs`.

Имя разделяемой памяти оставлено прежним — `Local\AmbilightFrameBus`. Его использует
существующий потребитель, и переименование нарушило бы совместимость.

## Благодарности

- [AlexGyver / Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight) —
  конструкция железа, прошивка и сама идея.
- [awawa-dev / HyperHDR](https://github.com/awawa-dev/HyperHDR) — протокол AWA и прошивки
  [HyperSerialPico](https://github.com/awawa-dev/HyperSerialPico) и
  [HyperSerialESP32](https://github.com/awawa-dev/HyperSerialESP32) для контроллеров ленты; из
  разбора HyperHDR взяты предел тока, минимальная подсветка и запас от полосы при
  кадрировании.
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
colours over zones along its edges and sends the result to an addressable LED strip behind
the monitor. The colours on the strip repeat the edges of the screen in games, films and on
the desktop, windowed or full screen.

The strip is driven by a controller on a serial port. Two protocols are supported:

- **Adalight** — the stock protocol of the Arduino firmwares, for example
  [Gyver_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight);
- **AWA** — its extension with a frame checksum, for boards with USB on the chip: the
  [HyperSerialPico](https://github.com/awawa-dev/HyperSerialPico) firmware for RP2040 and
  [HyperSerialESP32](https://github.com/awawa-dev/HyperSerialESP32). The frame travels over USB
  without the limits of a serial line, so it reaches the strip sooner, and a frame damaged
  on the way is dropped rather than shown.

![The Rimlight window: settings on the left, the zone preview with numbered cells along the edges and a status area with capture metrics on the right](pics/interface.jpg)

The hardware side and the original idea come from AlexGyver's
[Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight). Over Adalight, Rimlight
works with the same firmware unchanged.

Written as a replacement for [Prismatik](https://github.com/psieg/Lightpack), aiming at
faster and more flexible screen capture. Measured with a camera at 240 fps, a colour change
reaches the strip in 10-20 ms with an Arduino, and with an AWA controller the strip changes a
few milliseconds before the screen itself.

## Features

**Two controller protocols.** The protocol is chosen in the Device section. An AWA board
connects without a pause for a reboot, takes the LED count from the frame header and reports
its firmware version on connecting. Layout, brightness, colour and port are kept separately
for each protocol, so the two controllers can be switched between without setting anything
up again. The port list only offers boards that fit the protocol: they are told apart by
their USB vendor, without querying anyone else's devices.

**Latency.** A frame is taken the moment Windows has composed it, before the monitor shows
it: the monitor still has to wait for its refresh, scan the frame out top to bottom, process
it and switch the pixels of the panel. With an Arduino at 1 Mbaud the way to the strip is
longer than that; with an AWA controller it is shorter, and the strip gets ahead of the
screen.

**Fallback capture methods.** Desktop Duplication is the primary method. If it stops
delivering frames, the source switches to Windows Graphics Capture and then to GDI, and
returns to the previous method once that one delivers frames again. The current source is
shown in the status area.

**Distinguishing idle from failure.** A still screen produces no new frames, which is
normal and does not require switching. The logic separates two cases: no frames because
the image is not changing, and no frames because the method stopped working. Ambiguous
cases are resolved with a GDI probe.

**Frame processing on the GPU.** Frames are downscaled with hardware mip generation and
read back through a non-blocking ring of staging buffers: about 4 KB per frame crosses the
bus instead of 20 MB, and the readback does not wait for the GPU to become free.

**Colour maths in linear space.** Averaging, white balance, saturation and smoothing are
done on linear values; gamma encoding is applied at the end. Without this, averaging
understates brightness on high-contrast scenes.

**Adaptive cropping.** Material wider than the screen comes with black bars above and below
it, and the LEDs along them do not light. The edges of the picture are found in the frame
and the sampling zones move inside them; the picture can be spread across the whole strip
so that no part of it is left dark. Subtitles and player controls are drawn over the bar, so
each row is judged by its ends rather than its middle, and a short lit run inside the bar is
not taken for an edge. New edges are acted on only after they have held for a set time, or a
dark scene would be read as a bar.

## Requirements

- Windows 10 2004 or newer (Windows 11 recommended)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- An LED controller on a serial port: an Arduino running Adalight firmware, for example
  [Gyver_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight), or an RP2040 board
  running [HyperSerialPico](https://github.com/awawa-dev/HyperSerialPico). The RP2040 output is
  3.3 V and the strip wants 5, so a level shifter goes between them - a buffer such as the
  SN74AHCT125 - or a board that already has one is used. The firmware is not part of this
  repository.

The frame format on the wire:

```
Adalight  'A' 'd' 'a'  hi  lo  chk     chk = hi ^ lo ^ 0x55,  hi/lo encode (N - 1)
          then N x (R, G, B)

AWA       'A' 'w' 'a'  hi  lo  chk     the same
          then N x (R, G, B), then f1 f2 fext - Fletcher sums over the pixel bytes
```

At 1 Mbaud a 122-LED Adalight frame takes 372 bytes, that is 3.7 ms on the wire; together
with another 3.7 ms to latch into the strip that allows up to about 135 frames per second.
An AWA board receives the frame over USB in a fraction of a millisecond and drives the strip
while it takes in the next one, so its period is about 4 ms.

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

1. **Device** — pick the monitor, the controller protocol and the serial port, press
   *Apply and reconnect*. The monitor is remembered by its EDID model, so moving a cable
   between ports of the graphics card does not point the capture at a different screen. The
   port list only offers boards that fit the protocol; the baud rate is set for Adalight
   only. Switching the protocol reconnects the strip at once and brings back the layout,
   brightness, colour and port kept for it.
2. **Layout** — enter the LED count per side, the start corner and the direction. The
   *Show map on screen* button overlays numbered sampling zones on the screen; clicking a
   cell highlights it in green. The map is captured like any other image, so a green LED on
   the strip confirms the geometry, the numbering and the colour path at once.
3. Fine-tuning is done with the **Offset** slider: the start corner alone does not
   determine the position of the first LED — it also depends on which side the strip leaves
   the corner on.
4. **Brightness** — the overall brightness limit and three settings for the dark end of
   the frame. **Darkness threshold** puts out zones below a given level, **Shadow
   desaturation** removes the white balance tint from them - without it a black player bar
   lights the strip dark red under a warm balance - and **Minimum backlight** keeps the
   strip from going out on a black frame.
5. **Colour** — temperature and the per-channel gains are matched to the wall behind the
   monitor.
6. **Cropping** — switches on the search for black bars, which is off by default. The
   values suit 2.35:1 and 21:9 films. If the bars are lost when the player controls appear,
   raise **Step over interference**; if the crop reacts to dark scenes, raise **Confirmation
   delay** and **Smallest bar**.
7. **Capture** — the **Maximum frames per second** slider sets how often a screen frame
   is reduced to zone colours and sent to the strip. A frame arriving inside that window is
   dropped, so a limit adds up to one of its own periods to the output delay: measured with
   a camera at 240 fps, a change takes about 30 ms to reach the strip with the limit at 60
   and 10-20 ms at **no limit**, which is the default. With no limit the rate meets the
   controller's own period instead - about 7 ms for 122 LEDs on an Arduino at 1 Mbaud and
   about 4 ms on an AWA board. A limit cuts the
   number of frame reductions done on the graphics card, which can be worth having on a
   weak card or in a demanding game. The **Blur and sharpness** slider works on the frame
   before the zones are read off it: to the left the frame is defocused and neighbouring
   stretches of the strip run into each other, to the right neighbouring zones are pushed
   apart in brightness. At zero, the default, the frame is not worked over at all; at
   either end of the scale the work adds about 2 ms to the delay.
8. **Power** — the **Current ceiling** slider caps the current the whole strip draws. It
   only engages on bright frames, so coloured scenes usually show no difference. Set it by
   the supply: 120 WS2812 at full white draw about 7 A.

The **Stop** button in the bar along the bottom of the window stops the output and darkens
the strip without closing the program; pressing it again brings the light back. Beside it
stands what is happening now: whether the output is running and at what rate, whether it is
stopped and why, and whether the port opened.

The **Defaults** button in the General section puts the settings back to their standard
values, leaving the monitor, the protocol and port, the strip layout, the language, the
window position and the other protocol's settings alone.

The **Check for updates at startup** box in the About section is off by default: it is the
only request the program makes outside the machine, and the only thing sent out is the
current version number.

For a check against a real frame, `pics/Rainbow.jpg` is an image with saturated colours
in every part of the frame. Set as the desktop background, it shows the whole layout at
once: every part of the strip should repeat the colour of the screen edge nearest to it.

With Adalight the LED total must match `NUM_LEDS` in the firmware: stock sketches read a
fixed number of bytes regardless of the header, so a mismatch shifts the picture along the
strip. AWA firmwares take the LED count from the frame header.

Settings, the log and translation files are stored in `%APPDATA%\Rimlight\`. Once the log
reaches 5 MB it is renamed to `rimlight.old.log` and a new one is started, so it never takes
more than 10 MB.

## Localisation

Two languages are built into the program, Russian and English. On the first run they are
written to `%APPDATA%\Rimlight\lang\` as `ru.json` and `en.json`, and those files are
what gets read from then on, so an edit to a file takes effect on the next start. An
update of the program rewrites both built-in files so that an old file cannot hide the
new wording; the `_version` entry is what marks it. Added languages are left as they are.

A translation of your own is one more file in the same folder:

1. Copy `en.json` to a name holding the language code, `de.json` for instance: the file
   name is the language code. Templates of both built-in languages are in the
   [lang](lang) folder of the repository.
2. Replace the values with the translated text and leave the keys on the left as they
   are. A `{0}` inside a string is a substitution (a version, a path, an LED count) and
   has to be kept. The `_name` entry is the language as it should appear in the list,
   `Deutsch` for instance. The `_version` entry can be deleted: it concerns the built-in
   files only.
3. Start the program — the language appears in the list in the *General* section.

Only the basic structure is checked: the file has to read as a JSON map of string to
string and to hold at least a few known keys, otherwise it is not taken for a translation
and is skipped with a line in the log. There is no need to translate everything at once:
the keys left out are taken from English. An added language also keeps part of the
incidental text in English — log messages and the words in the statistics line are
written in the code rather than in the JSON.

## Capture under GPU load

Desktop Duplication and Windows Graphics Capture both read the output of the Windows
compositor (DWM). Composition runs on the GPU, and when a game fully loads the GPU the
compositor may not get enough time to run. Both methods then stop delivering frames, and
no error is reported. This appears as a growing p99 frame interval and frequent switches
to GDI in the status area.

Two factors affect how often composition runs:

- **Hardware-accelerated GPU scheduling** (Settings → System → Display → Graphics →
  Default graphics settings). With the setting enabled, work scheduling is handled by the
  GPU, and compositor tasks compete with game frames on equal terms. With it disabled,
  scheduling is handled by Windows, which allocates time to them more often. Measured on
  one configuration (RTX 4080, 3440×1440, 165 Hz): with the setting disabled, capture
  remains stable at 97–98% GPU load. DLSS Frame Generation requires the setting enabled,
  so this option is not always available.
- **GPU headroom.** Capping the game's frame rate below the rate the GPU can sustain
  leaves time for the compositor regardless of the scheduler.

## Repository layout

```
src/Rimlight        the application: layout, colour, serial, UI
src/Rimlight.Core   capture backends, colour pipeline, frame bus, localisation
tools/CaptureProbe  diagnostic tool: every capture method side by side, with metrics
tools/LatencyProbe  screen-to-strip latency, measured with a 240 fps phone camera
tools/AwaProbe      AWA controller check without the main program
lang                translation templates, also written to %APPDATA%
```

`tools/CaptureProbe` runs all capture backends at once on a single monitor and reports
frame rate, interval percentiles, per-stage frame cost and a second-by-second history. The
measurement results are collected in its README.

`tools/LatencyProbe` puts the engine's own zone grid on screen, repaints it in a primary
colour every few seconds and runs a millisecond clock beside it. Filming the screen and
the strip in one shot at 240 fps gives the end-to-end latency from outside - firmware and
panel included, where the built-in statistics stop at the serial write.

`tools/AwaProbe` checks a board running HyperSerialPico apart from Rimlight: it asks the
firmware to introduce itself, walks the strip through solid colours and a running dot, and
reads the controller's own counters - how many frames arrived and how many were damaged.
That separates a hardware fault from a mistake in what the program sends. The procedure is
in its README.

## Frame bus

With the *Publish frames* option enabled, the reduced frames are published to shared
memory: a second process can drive other lighting from the same capture without opening its
own. The format is described in `src/Rimlight.Core/Frames/FrameBus.cs`.

The shared-memory name is kept as `Local\AmbilightFrameBus`. An existing consumer uses it,
and renaming it would break compatibility.

## Credits

- [AlexGyver / Arduino_Ambilight](https://github.com/AlexGyver/Arduino_Ambilight) — the
  hardware build, the firmware and the original idea.
- [awawa-dev / HyperHDR](https://github.com/awawa-dev/HyperHDR) — the AWA protocol and the
  [HyperSerialPico](https://github.com/awawa-dev/HyperSerialPico) and
  [HyperSerialESP32](https://github.com/awawa-dev/HyperSerialESP32) controller firmwares; the
  current ceiling, the minimum backlight and the crop margin come from studying HyperHDR.
- [psieg / Lightpack (Prismatik)](https://github.com/psieg/Lightpack) — the predecessor;
  its capture code helped in understanding some of the problems described here.
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) — Direct3D 11 and DXGI
  bindings for .NET.
- [Spout2](https://github.com/leadedge/Spout2) — texture sharing; used by one of the
  experimental capture backends in CaptureProbe.

## Licence

MIT — see [LICENSE](LICENSE).
