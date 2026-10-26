# Agent notes

Keyboard Studio replaces the stock driver software of SONiX-based keyboards (AJAZZ AK980 V2, AULA F108 Pro), built from the reverse-engineered USB protocol. README.md has the features, the supported keyboards and the full protocol. Per-keyboard differences live in `src/Sonix.Keyboard/KeyboardModel.cs`; everything else is shared.

## Build

- `dotnet build` (.NET 8 SDK, Windows only: WPF + WinForms tray + GDI+)
- Publish: `dotnet publish src/KeyboardStudio -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/KeyboardStudio` and the same for `src/kbstudio` into `publish/cli`. Close a running copy first; it locks its exe.
- CLI for hardware testing: `src/kbstudio` (`models`, `list`, `read`, `lcd`, `light`, `custom`, `options`, `keys`, `remap`, `macros`, `raw`). Pass `--model ak980|f108pro` when both keyboards are plugged in. `remap --dry` and `macros --dry` print the exact bytes without touching a keyboard.

## Adding a keyboard

1. Get its stock installer; unpack it without installing (innoextract for Inno Setup) into `re/bin/<model>/`.
2. Compare its `DeviceDriver.exe` with a known build: `re/opscan.py` (opcode stores per function), `re/fdiff.py` (normalized diff; it can run past a function's end), `re/sigfind.py` (find functions across builds). Only proceed if the command functions match.
3. `re/layout2json.py <layouts/rgb-keyboard.xml> src/Sonix.Keyboard/Layout/<model>-layout.json`, then add a `KeyboardModel` (IDs from `config.xml`, `gif_maxframes` from the XML, effect names from `language/1033.lan` 521..540, firmware combos from the manual).

## Reverse engineering

- `re/bin/` and `re/out/` are git-ignored: never commit installers, vendor binaries or decompiler output.
- Full decompile: `pwsh -File re/ghidra/dump.ps1 -Binary re\bin\<model>\DeviceDriver.exe -Out re\out\<model>.c` (Ghidra headless + `re/ghidra/DumpAll.java`, a few minutes), then grep `// ==== 00xxxxxx` headers. rea (`re/rea.ps1`) works too but re-imports the binary on every query (~3 min).
- Function map (AK980 build → F108 Pro build): options 0x414060→0x414230, LCD upload 0x422960→0x422b50, clock 0x423920→0x423b10, F5 read 0x42af70→0x42ae20, lighting 0x42b190→0x42b040, side light 0x434300→0x433fd0, per-key mode 0x44bda0→0x44b800, per-key table 0x44beb0→0x44b910, send transport 0x44f370→0x44edc0, read transport 0x44f840→0x44f290, key remap sender 0x4183e0→0x4185e0, key entry encoder 0x418030→0x418230, launcher-key table 0x4192d0→0x4194e0, macro sender 0x42d820→0x42d630, VK→HID table 0x451a20→0x451460, key dialog combos 0x4101c9→0x410369 (play modes) and 0x411260→0x411400 (mouse).
- The stock databases (`%LOCALAPPDATA%\<driver> Files\db\*_datav1.db`) keep remaps in `t_key_macro_data`, macros in `t_macro_data` + `t_macrorecord`, lighting in `t_light_data`; `Services/StockImporter*.cs` reads them read-only from a temp copy.

## Hardware testing rules

- Never send unknown or guessed commands. Only packets that come from the vendor code; some opcodes may trigger a firmware reset or bootloader mode.
- Writes that replace stored configuration (key layers, macros) need the user's explicit go-ahead with a test and restore plan.
- Start with no-op tests (`kbstudio read`, or re-sending the current state), ask the user what they see, and restore their lighting, remaps and macros afterwards. A layer write replaces the whole layer, so include existing remaps. The user's AK980 has a Delete ↔ PgDn swap and an Fn-layer entry on Fn (`02 00 AF`) from the stock app.
- The app never sends anything on startup unless the user enabled it.
- F108 Pro factory reset: `Fn+Esc` held 3 s.

## UI checks

- Try the app on a copy of the data: set `KEYBOARDSTUDIO_HOME` to a folder that holds copies of `KeyboardStudio`, `AK980Studio` or `F108Studio` (from `%LOCALAPPDATA%`). In that mode the app never touches Windows autostart. Turn off "re-apply on connect" and clock sync in the copies so nothing is sent.
- `scripts/shot.ps1 <out.png>` screenshots the running app; `scripts/nav.ps1 "<name>"` selects a page (Screen, Lighting, Per-key colors, Keys, Macros, Keyboard, App settings) or a keyboard ("AJAZZ AK980 V2", "AULA F108 Pro") via UI Automation; `scripts/invoke.ps1 <AutomationId or name>` presses a button, for example `key-115` selects a key on the Keys page. Don't synthesize mouse clicks: the window may be behind other windows.
