# Keyboard Studio

One open replacement app for the stock driver software of SONiX-based AJAZZ and AULA keyboards, built from their reverse-engineered USB protocol. Dark mode by default, nothing is sent to a keyboard when the app starts unless you turn that on, and each keyboard keeps its own settings.

## Supported keyboards

| Keyboard | USB (wired) | 2.4G dongle | Screen | Keys | Status |
|---|---|---|---|---|---|
| AJAZZ AK980 V2 | `0C45:8009` | `0C45:FEFE` | 240×135, 140 frames | 97 | everything below verified on the keyboard |
| AULA F108 Pro | `0C45:800A` | `05AC:024F` | 240×135, 141 frames | 104 | everything below verified on the keyboard |

Both stock apps are builds of the same SONiX `DeviceDriver.exe`. Their command code is byte-identical, so one protocol implementation and one app serve both; the differences are data (IDs, key layout, LCD frame limit, a few names), kept in `src/Sonix.Keyboard/KeyboardModel.cs`. Adding another keyboard from the same driver family is mostly a new entry there plus its layout.

The stock apps re-send the LCD clock **and a screen page number** every time they start (and at every boot with autostart), so the screen jumps to another page on every launch. Keyboard Studio only does that when you ask.

## Run it

- App: `publish\KeyboardStudio\KeyboardStudio.exe` (needs the .NET 8 Desktop Runtime)
- CLI: `publish\cli\kbstudio.exe` (`models`, `list`, `read`, `lcd`, `preview`, `time`, `light`, `custom`, `options`, `side`, `battery`, `keys`, `remap`, `macros`, `raw`; `--model ak980|f108pro` when both keyboards are plugged in; `remap` and `macros` take `--dry` to print the bytes without sending)
- Build: `dotnet build`, then
  - `dotnet publish src/KeyboardStudio -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/KeyboardStudio`
  - `dotnet publish src/kbstudio -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/cli`

Don't run a stock app afterwards: it will reset the screen again.

Coming from AK980 Studio or F108 Studio: the first start takes over their settings, remaps, macros and animation library, and if you had "start with Windows" on, it replaces their autostart entry.

## Features

Pick the keyboard in the sidebar (both can be plugged in at once); every page works on that keyboard.

| Page | What it does |
|---|---|
| Screen | Library of animations, shared by all keyboards (first run imports the stock apps' saved ones, with their timings). Fit/Fill/Stretch, zoom + pan, mirror/flip, brightness/contrast/saturation, speed, fixed delay, trim, frame step, text overlay, dithering. The preview shows the exact 16-bit colors the panel will display. Clock sync is opt-in. |
| Lighting | All 19 effects plus off, with the keyboard's own effect names. Each effect only shows the controls it supports (brightness, speed, direction, color, rainbow). Live apply, presets, optional re-apply on connect. Lists the keyboard's firmware-only light combos. |
| Per-key colors | Paint the real key layout, eyedropper, rainbow/gradient fills, WASD preset. Labels shrink to fit narrow keys. |
| Keys | Remap any key, on its own or while Fn is held: another key or shortcut (with Ctrl/Shift/Alt/Win), a mouse button or wheel, a media or launcher key, a macro (once, N times, toggle, while held, click), up to three keys at once, or nothing. Capture a key by pressing it, Windows-shortcut presets, a list of everything remapped. First run imports the stock app's remaps. |
| Macros | Up to 100 macros in the keyboard's macro memory, with a live usage meter. Record by typing (with or without your timing), type text, add keys and mouse clicks, edit each pause, reorder; keys follow their macro when the list changes. |
| Keyboard | Win / Alt+Tab / Alt+F4 locks, Fn swap, sleep timer, debounce level (with the stock latency figures). |
| Battery | Sidebar charge level and tray tooltip per keyboard, read through its 2.4G dongle every 15 s (configurable), low-battery notifications. The keyboards only report their charge in 2.4G mode; in wired mode the last reading and its age are shown. |
| App settings | Dark/light theme, accent color, UI size, start with Windows (hidden), close to tray, battery alerts, command gap. |

Data lives in `%LOCALAPPDATA%\KeyboardStudio`: `settings.json` (app), `devices\<model>.json` (per keyboard), `library\`.

### Firmware-only controls

- AK980 V2: `Fn+Z` logo light, `Fn+Del` screen on/off, `Fn+X` all lights, `Fn`+knob press toggles the knob between volume and screen menu. The red Fn key while charging and the Num Lock highlight are drawn by the firmware.
- F108 Pro (manual): backlight `Fn+Tab` colors, `Fn+\` effects, `Fn+↑/↓` brightness, `Fn+←/→` speed; light bar `Fn+Left Shift/Ctrl/Alt/Z`; side light `Fn+Right Shift/Ctrl/Alt/?`; `Fn+knob` toggles the knob between screen menu and volume; `Fn+Esc` held 3 s resets to factory settings. The connection mode is picked in the screen menu with the knob.

## Protocol (wired)

Sources: the stock `DeviceDriver.exe` and `mui.dll` of both keyboards (the AULA installer is Inno Setup and unpacks with innoextract, nothing has to be installed), analysed with [rea](https://github.com/morluto/rea) + Ghidra 12.1.4 (`re/ghidra/dump.ps1`) and capstone (`re/*.py`). `re/fdiff.py` shows every command function is byte-identical between the two builds, and `re/sigfind.py` finds the key dialog and encoders at shifted addresses with the same values. Addresses below are the AK980 build; `agents.md` maps them to the F108 build.

| Interface | Usage page | Reports | Role |
|---|---|---|---|
| MI_03 | FF13 | 65-byte feature (ID 0 + 64) | commands |
| MI_02 | FF68 | 4097-byte output (ID 0 + 4096), 65-byte input | LCD pages |

Each command is a 64-byte packet sent with `HidD_SetFeature` (report ID 0 prepended), followed by a `HidD_GetFeature` read-back. The reply echoes the packet, and report byte 4 is a status. The stock apps wait 35 ms before each packet.

A transaction looks like this: `04 18` (begin), then `04 OP ARG 00 00 00 00 00 COUNT_LO COUNT_HI`, then COUNT data packets, then `04 02` (commit), and for lighting `04 F0` (apply).

| Op | Data | Meaning | Verified |
|---|---|---|---|
| `F5` | no begin; after the echo, up to 9 × 64 bytes come through successive `HidD_GetFeature` calls (the first read repeats the echo), then `04 02` | read back the per-key table: `[idx R G B]` × 144 with the LEDs' *live* colors | F108 Pro |
| `72` | ARG = slot, COUNT = 4 KiB pages, sent on MI_02, one ack per page | LCD upload | both |
| `28` | COUNT = 1: `00 PAGE 5A YY MM DD hh mm ss 00 DOW … [62]=AA [63]=55` | LCD clock; the page byte (stock: selected list item + 1) also switches which screen page is shown | both |
| `13` | `MODE R G B 0 0 0 0 RAINBOW BRIGHT SPEED DIR 0 0 AA 55` | backlight effect, modes 1–19, 0 = off | both |
| `13` | `80 … [9]=BRIGHT … AA 55` | switch to per-key mode | both |
| `23` | COUNT = 9: 576 bytes of `[idx R G B]` × 144, last two bytes `AA 55` | per-key colors | both |
| `13` | `20..29` | "side light" (hidden in both stock UIs) | sent, no visible effect on either |
| `17` | ARG = 1, COUNT = 1: `00 01 ALTTAB ALTF4 WIN FNSWAP SLEEP 00 RESPONSE … [62]=AA [63]=55` | firmware options | sent with unchanged values |
| `11` / `27` | COUNT = 9: 576 bytes, one 4-byte entry per key index, `AA 55` at 574; then `04 02` and `04 F0`, both acknowledged | key remap, normal layer / Fn layer | both |
| `19`, `15` | `04 19` (no begin), then `04 15 … [8]=PAGES`, 30 ms, PAGES × 64 bytes, 30 ms, `04 02` | macros | both |
| `20` | COUNT = 8 | real-time lighting stream (music/ambilight) | not implemented |

Key entries (stock 0x418030 / 0x4183e0; all zero = firmware default). A layer write replaces the whole layer.

| Entry | Meaning |
|---|---|
| `00 00 00 00` | default |
| `05 03 00 00` | disabled |
| `02 MODS CODE 00` | HID key with modifiers (MODS: 1 Ctrl, 2 Shift, 4 Alt, 8 Win); a bare modifier key is sent as `02 BIT 00` |
| `01 01 BTN 00` / `01 03 DIR 00` | mouse: BTN 1 left, 2 right, 4 middle, 3 double click, 10 button 4, 08 button 5; DIR 01 wheel up, FF wheel down |
| `03 LO HI 00` | consumer usage: CD play/pause, B7 stop, B6/B5 previous/next, E9/EA volume up/down, E2 mute, 0192 calculator, 018A mail, 0183 media player, 0194 My Computer, 0221–022A browser keys |
| `06 SLOT MODE COUNT` | macro: MODE 0 once, 1 COUNT times (1–255), 2 until pressed again, 3 while held, 4 click to play |
| `07 K1 K2 K3` | up to three keys pressed together |

The stock apps' "open program", "open website" and "send text" functions only work while they run, so they are not offered. Keys that have their own firmware Fn function may ignore a Fn-layer remap (seen with Fn+Home on the AK980).

Macro image (stock 0x42d820): a 100 × 4-byte directory (macro offset LE16 + `00 00`; `FF FF FF FF` for an empty macro), then from offset 400 one record per macro: event count LE16, 6 × `00`, events. Each recorded step becomes two 4-byte events: `00 00 CODE TYPE` (TYPE `B0` key down, `30` key up, `90` mouse down, `10` mouse up; CODE is the HID usage or the mouse button mask) and `MS_LO MS_HI 00 50`, the pause after it (at least 10 ms). PAGES = size / 64 + 1, or + 2 when the size isn't a multiple of 64; the last two bytes are `AA 55`. The stock apps refuse more than 0xA8C bytes of (steps + macros) × 8.

LCD upload blob: `[0]` = frame count, `[1..n]` = per-frame delay in 2 ms units, then from offset 256 the frames as RGB565 little-endian, 240×135, top-down rows. The blob is padded with `FF` to a whole number of 4 KiB pages. Each page takes about 145 ms, because the keyboard acks every flash write.

### 2.4G dongles

MI_03, usage page FF60, 33-byte output reports: report ID 0, the command bytes, and a byte-sum checksum in the last byte. The reply echoes the first three command bytes.

- Battery (verified on both): request `20 01`, reply `20 01 00 LEVEL …` with LEVEL in percent. A keyboard only answers while it is switched to 2.4G mode, and in that mode its USB command interface disappears (the cable only charges).
- AK980 clock: `0C 10 00 00 SLOT 5A …`; LCD data: 28-byte chunks after a `7F 03 00 SLOT COUNT24` header.

Configuration commands (lighting, LCD, options, keys, macros) are only implemented over USB.

## What already existed

No VIA/QMK or upstream OpenRGB support for either keyboard. [hcode10/OpenRGB-With-AulaF108Pro-support](https://github.com/hcode10/OpenRGB-With-AulaF108Pro-support) (from [OpenRGB issue 5253](https://gitlab.com/CalcProgrammer1/OpenRGB/-/issues/5253)) drives the F108 Pro's LEDs through the `04 20` real-time stream only.

## Repo layout

- `src/Sonix.Keyboard`: HID transport (P/Invoke, no dependencies), the keyboard models, protocol, key remap and macro encoders, the key layouts (`Layout/*-layout.json`, generated from the stock XML by `re/layout2json.py`), LCD rendering and encoding
- `src/KeyboardStudio`: WPF app
- `src/kbstudio`: CLI
- `re/`: reverse-engineering helpers (rea wrapper, Ghidra dump script, capstone scripts, layout converter). `re/bin` and `re/out` are git-ignored because they hold vendor binaries, installers and decompiler output.
- `scripts/`: dev helpers that screenshot the running app (`shot.ps1`), switch pages or keyboards (`nav.ps1`) and press buttons or keys on the key picture (`invoke.ps1 key-115`) through UI Automation

This repo started as AK980 Studio; its history is kept.
