# AK980 Studio

A replacement for the AJAZZ AK980 V2 driver software (`DeviceDriver.exe`), built from its reverse-engineered USB protocol.

The stock app re-sends the LCD clock **and an LCD slot number** every time it starts (and at every boot, if autostart is on). The slot it sends is "whichever animation is selected in its list + 1", so the screen jumps to a different slot on every launch. AK980 Studio never sends anything on startup unless you turn that on.

## Run it

- App: `publish\AK980Studio\AK980Studio.exe` (needs the .NET 8 Desktop Runtime, already installed here)
- CLI: `publish\cli\ak980.exe` (`list`, `lcd`, `preview`, `time`, `light`, `custom`, `options`, `side`, `battery`, `keys`, `remap`, `macros`, `raw`; `remap` and `macros` take `--dry` to print the bytes without sending)
- Build: `dotnet build` / `dotnet publish src/AK980Studio -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/AK980Studio`

Don't open the stock AJAZZ app afterwards: it will reset the screen again.

## Features

| Page | What it does |
|---|---|
| Screen | Library of animations (first run imports the stock app's saved ones, with their timings). Fit/Fill/Stretch, zoom + pan, mirror/flip, brightness/contrast/saturation, speed, fixed delay, trim, frame step, text overlay, dithering. The preview shows the exact 16-bit colors the panel will display. Clock sync is opt-in. |
| Lighting | All 19 effects plus off. Each effect only shows the controls it supports (brightness, speed, direction, color, rainbow). Live apply, presets, optional re-apply on connect. |
| Per-key colors | Paint the real key layout, eyedropper, rainbow/gradient fills, WASD preset. Labels shrink to fit narrow keys. |
| Keys | Remap any key, on its own or while Fn is held: another key or shortcut (with Ctrl/Shift/Alt/Win), a mouse button or wheel, a media or launcher key, a macro (once, N times, toggle, while held, click), up to three keys at once, or nothing. Capture a key by pressing it, Windows-shortcut presets, a list of everything remapped. First run imports the stock app's remaps. |
| Macros | Up to 100 macros in the keyboard's macro memory, with a live usage meter. Record by typing (with or without your timing), type text, add keys and mouse clicks, edit each pause, reorder; keys follow their macro when the list changes. |
| Keyboard | Win / Alt+Tab / Alt+F4 locks, Fn swap, sleep timer, debounce level (with the stock latency figures). |
| Battery | Sidebar charge level and tray tooltip, read through the 2.4G dongle every 15 s (configurable). Low-battery notification with an adjustable threshold. The keyboard only reports its charge in 2.4G mode; in wired mode the last reading and its age are shown. |
| App settings | Dark/light theme, accent color, UI size, start with Windows (hidden), close to tray, battery alerts, command gap. |

Firmware-only lights (no software command exists, checked against every opcode in the stock driver):

- `Fn+Z`: logo LED next to the arrow keys
- `Fn+Del`: LCD on/off
- `Fn+X`: all lights
- The red Fn key while charging and the Num Lock highlight are drawn by the firmware and can't be switched off.

Data lives in `%LOCALAPPDATA%\AK980Studio` (`settings.json`, `library\`).

## Protocol (wired, VID 0C45 PID 8009)

Sources: `DeviceDriver.exe` and `mui.dll` from the stock install, analysed with [rea](https://github.com/morluto/rea) + Ghidra 12.1.4 and capstone. The verified items were tested on the keyboard.

| Interface | Usage page | Reports | Role |
|---|---|---|---|
| MI_03 | FF13 | 65-byte feature (ID 0 + 64) | commands |
| MI_02 | FF68 | 4097-byte output (ID 0 + 4096) | LCD pages |

Each command is a 64-byte packet sent with `HidD_SetFeature` (report ID 0 prepended), followed by a `HidD_GetFeature` read-back. The reply echoes the packet, and byte 4 of the reply is a status. The stock app waits 35 ms before each packet.

A transaction looks like this: `04 18` (begin), then `04 OP ARG 00 00 00 00 00 COUNT_LO COUNT_HI`, then COUNT data packets, then `04 02` (commit), and for lighting `04 F0` (apply).

| Op | Data | Meaning | Verified |
|---|---|---|---|
| `72` | ARG = slot, COUNT = 4 KiB pages, sent on MI_02, one ack per page | LCD upload | yes |
| `28` | COUNT = 1: `00 SLOT 5A YY MM DD hh mm ss 00 DOW … [62]=AA [63]=55` | LCD clock and selected slot | yes |
| `13` | `MODE R G B 0 0 0 0 RAINBOW BRIGHT SPEED DIR 0 0 AA 55` | backlight effect, modes 1–19, 0 = off | yes |
| `13` | `80 … [9]=BRIGHT … AA 55` | switch to per-key mode | yes |
| `23` | COUNT = 9: 576 bytes of `[idx R G B]` × 144, last two bytes `AA 55` | per-key colors | yes |
| `13` | `20..29` | "side light" (hidden in the stock UI, no visible effect on this board) | sent, no effect |
| `17` | ARG = 1, COUNT = 1: `00 01 ALTTAB ALTF4 WIN FNSWAP SLEEP 00 RESPONSE … [62]=AA [63]=55` | firmware options | sent unchanged values only |
| `F5` | read back the per-key table | read | not used |
| `11` / `27` | COUNT = 9: 576 bytes, one 4-byte entry per key index, `AA 55` at 574; then `04 02` and `04 F0`, both acknowledged | key remap, normal layer / Fn layer (see below) | yes: Home → `!`, Fn+H → `z`, the stock Delete ↔ PgDn swap re-sent and kept |
| `19`, `15` | `04 19` (no begin), then `04 15 … [8]=PAGES`, 30 ms, PAGES × 64 bytes, 30 ms, `04 02` | macros (see below) | yes: PgUp played a stored "hi" macro |
| `20` | | real-time lighting stream (music/ambilight) | not implemented |

Key entries (stock `DeviceDriver.exe` 0x418030 / 0x4183e0; all zero = firmware default):

| Entry | Meaning |
|---|---|
| `00 00 00 00` | default |
| `05 03 00 00` | disabled |
| `02 MODS CODE 00` | HID key with modifiers (MODS: 1 Ctrl, 2 Shift, 4 Alt, 8 Win); a bare modifier key is sent as `02 BIT 00` |
| `01 01 BTN 00` / `01 03 DIR 00` | mouse: BTN 1 left, 2 right, 4 middle, 3 double click, 10 button 4, 08 button 5; DIR 01 wheel up, FF wheel down |
| `03 LO HI 00` | consumer usage: CD play/pause, B7 stop, B6/B5 previous/next, E9/EA volume up/down, E2 mute, 0192 calculator, 018A mail, 0183 media player, 0194 My Computer, 0221–022A browser keys |
| `06 SLOT MODE COUNT` | macro: MODE 0 once, 1 COUNT times (1–255), 2 until pressed again, 3 while held, 4 click to play |
| `07 K1 K2 K3` | up to three keys pressed together |

The stock app's "open program", "open website" and "send text" functions only work while it runs, so they are not offered. The stock database on this PC holds a Delete ↔ PgDn swap and an Fn-layer entry on the Fn key (`02 00 AF`); AK980 Studio imports those on first run, so applying never drops them. The Fn-key entry does not block the Fn layer (Fn+H remaps worked with it present). Keys that have their own firmware Fn function may ignore a Fn-layer remap: Fn+Home did nothing visible either way.

Macro image (stock 0x42d820): a 100 × 4-byte directory (macro offset LE16 + `00 00`; `FF FF FF FF` for an empty macro), then from offset 400 one record per macro: event count LE16, 6 × `00`, events. Each recorded step becomes two 4-byte events: `00 00 CODE TYPE` (TYPE `B0` key down, `30` key up, `90` mouse down, `10` mouse up; CODE is the HID usage or the mouse button mask) and `MS_LO MS_HI 00 50`, the pause after it (at least 10 ms). PAGES = size / 64 + 1, or + 2 when the size isn't a multiple of 64; the last two bytes are `AA 55`. The stock app refuses more than 0xA8C bytes of (steps + macros) × 8.

LCD upload blob: `[0]` = frame count, `[1..n]` = per-frame delay in 2 ms units, then from offset 256 the frames as RGB565 little-endian, 240×135, top-down rows. The blob is padded with `FF` to a whole number of 4 KiB pages; the keyboard holds up to 140 frames. Each page takes about 145 ms, because the keyboard acks every flash write.

The 2.4 GHz dongle (PID FEFE) uses a different framing on MI_03 (usage page FF60, 33-byte output reports): report ID 0, the command bytes, and a byte-sum checksum in the last byte. The reply echoes the first three command bytes.

- Battery (verified): request `20 01`, reply `20 01 00 LEVEL …` with LEVEL in percent. The keyboard only answers while it is switched to 2.4G mode, and in that mode the USB command interface disappears (the cable only charges).
- Clock: `0C 10 00 00 SLOT 5A …`
- LCD data: 28-byte chunks after a `7F 03 00 SLOT COUNT24` header

Configuration commands (lighting, LCD, options) are only implemented over USB.

## Repo layout

- `src/Ajazz.Keyboard`: HID transport (P/Invoke, no dependencies), protocol, key remap and macro encoders, the key layout (`Layout/ak980-layout.json`, generated from the stock XML by `re/layout2json.py`), LCD rendering and encoding
- `src/AK980Studio`: WPF app
- `src/ak980`: CLI
- `re/`: reverse-engineering helpers (rea wrapper, Ghidra dump script, capstone scripts, layout converter). `re/bin` and `re/out` are git-ignored because they hold vendor binaries and decompiler output.
- `scripts/`: dev helpers that screenshot the running app (`shot.ps1`), switch pages (`nav.ps1`) and press buttons or keys on the key picture (`invoke.ps1 key-119`) through UI Automation

The AULA F108 Pro runs an older build of the same driver; its port, [F108 Studio](../AULA), shares this protocol and the remap and macro code.
