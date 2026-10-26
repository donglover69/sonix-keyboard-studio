# Agent notes

AK980 Studio is a replacement for the AJAZZ AK980 V2 driver, built from its reverse-engineered USB protocol. README.md has the features and the full protocol table. The AULA F108 Pro uses an older build of the same driver; F108 Studio (`../AULA`) shares the protocol and the key remap and macro code, so fix bugs in both.

## Build

- `dotnet build` (.NET 8 SDK, Windows only: WPF + WinForms tray + GDI+)
- Publish: `dotnet publish src/AK980Studio -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/AK980Studio` (close the running app first; it autostarts from that folder and locks the exe)
- CLI for hardware testing: `src/ak980` (`ak980 list`, `lcd`, `light`, `custom`, `options`, `keys`, `remap`, `macros`, `raw`). `remap --dry` and `macros --dry` print the exact bytes without touching the keyboard.

## Reverse engineering

- Source binaries come from the stock installer (`AJAZZ AK980 V2.exe`, installs to `C:\Program Files (x86)\AJAZZ AK980\`). Copy `DeviceDriver.exe`, `mui.dll` and `layouts\rgb-keyboard.xml` into `re/bin/`. That folder and `re/out/` are git-ignored: never commit vendor binaries or decompiler output.
- [rea](https://github.com/morluto/rea) with Ghidra 12.1.x + JDK 21: `re/rea.ps1` (set `REA_TOOLS`, default `%USERPROFILE%\tools`). Each rea CLI query re-imports the binary (~3 min). For bulk work, run `re/ghidra/DumpAll.java` through `analyzeHeadless`.
- Quick helpers (pefile + capstone): `re/opscan.py` (every `04 XX` opcode store grouped by function), `re/fdiff.py` (normalized disassembly diff between two builds), `re/sigfind.py` (find a function of one build in the other by masked byte signature), `re/disasm.py`, `re/xref.py`, `re/callers.py`, `re/opcodes.py`, `re/fieldwrites.py`, `re/strings.py`, `re/layout2json.py` (vendor XML → `src/Ajazz.Keyboard/Layout/ak980-layout.json`, with HID codes).
- Remaps and macros: key remap sender 0x4183e0 (04 11 / 04 27), key entry encoder 0x418030, launcher-key table 0x4192d0, macro sender 0x42d820 (04 19 / 04 15), VK→HID table 0x451a20, key dialog combos 0x4101c9 (play modes) and 0x411260 (mouse). The stock database keeps remaps in `t_key_macro_data` and macros in `t_macro_data` + `t_macrorecord`; `Services/StockImporter.Keys.cs` converts them exactly like the stock encoder.

## Hardware testing rules

- Never send unknown opcodes to the keyboard. Some may trigger firmware reset or bootloader mode.
- Prefer no-op tests (re-send the current state) before visible changes, and restore the user's lighting, remaps and macros afterwards. The user's AK980 has a Delete ↔ PgDn swap and an Fn-layer entry on Fn (`02 00 AF`) stored by the stock app; a remap test must send those back.
- The logo LED (`Fn+Z`) and the charging indicator are firmware-only; no software command exists.

## UI checks

`scripts/shot.ps1 <out.png>` screenshots the running app; `scripts/nav.ps1 "<page name>"` switches pages via UI Automation (Screen, Lighting, Per-key colors, Keys, Macros, Keyboard, App settings); `scripts/invoke.ps1 <AutomationId or name>` presses a button, for example `key-119` selects Delete on the Keys page. Don't synthesize mouse clicks: the window may be behind other windows.
