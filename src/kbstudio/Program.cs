using System.Drawing.Imaging;
using Sonix.Keyboard;
using Sonix.Keyboard.Layout;
using Sonix.Keyboard.Lcd;

static int Usage()
{
    var models = string.Join(" | ", KeyboardModel.All.Select(m => $"{m.Id} ({m.Name})"));
    Console.WriteLine($"""
        kbstudio - Keyboard Studio command line tool for SONiX-based AJAZZ and AULA keyboards

          kbstudio [--model ID] <command> ...
                --model picks the keyboard: {models}.
                Without it, the one that's plugged in is used (required when several are).

          kbstudio models                                 list supported keyboards and what's plugged in
          kbstudio list                                   list the keyboard's and dongle's HID interfaces
          kbstudio read                                   read the per-key table back (changes nothing on the keyboard)
          kbstudio time [--slot N]                        set the LCD clock (and shown page) to the PC's local time
          kbstudio lcd <image|gif|frame-dir> [--slot N] [--fit fit|fill|stretch] [--delay MS]
                                                          upload a picture/animation to the LCD
          kbstudio preview <image|gif> <out-dir> [--fit ...]
                                                          render the LCD frames to PNGs without touching the keyboard
          kbstudio light <mode 0-19> [--color RRGGBB] [--bright 0-5] [--speed 0-5] [--dir N] [--rainbow]
          kbstudio custom <RRGGBB> [led=RRGGBB ...] [--bright 0-5]
                                                          per-key colors (every LED, then overrides by LED index)
          kbstudio options [--sleep 0-3] [--response N] [--nowin] [--noalttab] [--noaltf4] [--fnswitch]
          kbstudio side <off|flowing|red|yellow|green|iceblue|blue|pink|white|neon>
          kbstudio battery                                read the battery level through the 2.4G dongle
          kbstudio keys                                   list key indices and names used by remap
          kbstudio remap [--fn] [--dry] <Key>=<action> ... write a whole layer: listed keys get the action, all others their default
          kbstudio remap [--fn] [--dry] --reset           write a layer with every key at its default
                actions: default | off | key:[ctrl+][shift+][alt+][win+]Name | mouse:left|right|middle|double|btn4|btn5|wheelup|wheeldown
                         media:play|stop|prev|next|volup|voldown|mute|calc|mail|player|computer|home|refresh|back|forward|favorites|search
                         macro:N[:once|repeat:COUNT|toggle|hold|click] | multi:A,B,C
          kbstudio macros [--dry] <macro> ...             store macros in slots 0, 1, ... (replaces all macros on the keyboard)
          kbstudio macros [--dry] --clear                 remove every macro
                macros:  text:hello | combo:ctrl+shift+esc | steps:down:A,wait:50,up:A,tap:B,click:left
          kbstudio raw <hex bytes>                        send one 64-byte command packet, print the reply
        """);
    return 1;
}

static void Dump(string title, byte[] data)
{
    Console.WriteLine(title);
    for (int i = 0; i < data.Length; i += 32)
        Console.WriteLine($"  {i,4}: {Convert.ToHexString(data.AsSpan(i, Math.Min(32, data.Length - i)))}");
}

string? Opt(string name) =>
    Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

LcdFitMode Fit() => Enum.TryParse<LcdFitMode>(Opt("--fit"), true, out var f) ? f : LcdFitMode.Fit;

byte Slot() => byte.TryParse(Opt("--slot"), out var s) ? s : (byte)1;

// --model ID, or the single keyboard that's plugged in.
KeyboardModel ResolveModel()
{
    if (Opt("--model") is { } name)
        return KeyboardModel.Find(name) ?? throw new InvalidOperationException($"Unknown model '{name}' (see 'kbstudio models')");
    var connected = SonixKeyboard.DetectAll();
    return connected.Count switch
    {
        1 => connected[0],
        0 => throw new InvalidOperationException("No supported keyboard found; plug one in or pass --model"),
        _ => throw new InvalidOperationException($"Several keyboards are plugged in ({string.Join(", ", connected.Select(m => m.Id))}); pick one with --model"),
    };
}

SonixKeyboard OpenKeyboard(KeyboardModel model) =>
    SonixKeyboard.Open(model) ?? throw new InvalidOperationException($"{model.Name} not found on USB (VID {model.VendorId:X4} / PID {model.ProductId:X4})");

Dictionary<int, KeyAction> ParseRemaps(KeyboardModel model, IEnumerable<string> specs)
{
    var layout = KeyboardLayout.Load(model);
    var map = new Dictionary<int, KeyAction>();
    foreach (var spec in specs)
    {
        int eq = spec.IndexOf('=');
        if (eq <= 0) throw new FormatException($"Expected Key=action, got '{spec}'");
        string keyName = spec[..eq];
        var key = layout.ByName(keyName)
                  ?? (int.TryParse(keyName, out var idx) ? layout.ByIndex(idx) : null)
                  ?? throw new FormatException($"Unknown key '{keyName}' (see 'kbstudio keys')");
        map[key.Index] = KeyAction.Parse(spec[(eq + 1)..]);
        Console.WriteLine($"  {key.Name,-10} (index {key.Index,3}) -> {map[key.Index].Describe()}  [{Convert.ToHexString(map[key.Index].ToEntry())}]");
    }
    return map;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Positional arguments without the --model option and its value.
var rest = new List<string>(args);
if (rest.IndexOf("--model") is var mi and >= 0) rest.RemoveRange(mi, Math.Min(2, rest.Count - mi));
if (rest.Count == 0) return Usage();

try
{
    switch (rest[0])
    {
        case "models":
        {
            var connected = SonixKeyboard.DetectAll();
            foreach (var m in KeyboardModel.All)
            {
                var (usb, dongle) = SonixKeyboard.Detect(m);
                Console.WriteLine($"{m.Id,-8} {m.Name,-16} USB {m.VendorId:X4}:{m.ProductId:X4}  dongle {m.DongleVendorId:X4}:{m.DongleProductId:X4}  "
                                  + (usb ? "connected (USB)" : dongle ? "dongle plugged in" : "not connected"));
            }
            return 0;
        }

        case "list":
            foreach (var m in Opt("--model") is null ? KeyboardModel.All : [ResolveModel()])
                foreach (var d in SonixKeyboard.ListInterfaces(m))
                    Console.WriteLine($"{d.VendorId:X4}:{d.ProductId:X4} MI_{d.Interface:X2} page {d.UsagePage:X4}:{d.Usage:X2} in {d.InputLength,4} out {d.OutputLength,4} feat {d.FeatureLength,3}  {d.Product}");
            return 0;

        case "read":
        {
            using var kb = OpenKeyboard(ResolveModel());
            var table = kb.ReadCustomTable();
            Console.WriteLine($"echo: {(kb.LastEcho is null ? "none" : Convert.ToHexString(kb.LastEcho.AsSpan(0, 16)))}");
            Console.WriteLine($"{table.Length / 64} packet(s), {table.Length} bytes");
            for (int i = 0; i < table.Length; i += 32)
                Console.WriteLine($"{i,4}: {Convert.ToHexString(table.AsSpan(i, Math.Min(32, table.Length - i)))}");
            return 0;
        }

        case "time":
        {
            using var kb = OpenKeyboard(ResolveModel());
            kb.SyncClock(DateTime.Now, Slot());
            Console.WriteLine($"Clock set to {DateTime.Now:yyyy-MM-dd HH:mm:ss} (slot {Slot()})");
            return 0;
        }

        case "lcd" when rest.Count >= 2:
        {
            var model = ResolveModel();
            using var anim = LcdAnimation.FromPath(rest[1], model.Lcd, Fit());
            if (int.TryParse(Opt("--delay"), out var delay))
                anim.Frames.ForEach(f => f.DelayMs = delay);
            using var kb = OpenKeyboard(model);
            Console.WriteLine($"Uploading {anim.Frames.Count} frame(s) to slot {Slot()}...");
            var progress = new Progress<double>(p => Console.Write($"\r  {p:P0}   "));
            kb.UploadLcd(anim, Slot(), progress);
            Console.WriteLine("\nDone.");
            return 0;
        }

        case "preview" when rest.Count >= 3:
        {
            var geometry = Opt("--model") is null ? new LcdGeometry() : ResolveModel().Lcd;
            using var anim = LcdAnimation.FromPath(rest[1], geometry, Fit());
            Directory.CreateDirectory(rest[2]);
            for (int i = 0; i < anim.Frames.Count; i++)
                anim.Frames[i].Image.Save(Path.Combine(rest[2], $"{i:D3}.png"), ImageFormat.Png);
            var blob = anim.Encode();
            Console.WriteLine($"{anim.Frames.Count} frame(s), delays {string.Join(",", anim.Frames.Select(f => f.DelayMs).Distinct())} ms, blob {blob.Length} bytes ({blob.Length / LcdGeometry.PageSize} pages)");
            return 0;
        }

        case "light" when rest.Count >= 2:
        {
            var model = ResolveModel();
            var color = Convert.FromHexString(Opt("--color") ?? "B48CFF");
            var s = new LightSettings
            {
                Mode = byte.Parse(rest[1]),
                R = color[0], G = color[1], B = color[2],
                Rainbow = args.Contains("--rainbow"),
                Brightness = int.TryParse(Opt("--bright"), out var br) ? br : LightSettings.MaxLevel,
                Speed = int.TryParse(Opt("--speed"), out var sp) ? sp : 3,
                Direction = int.TryParse(Opt("--dir"), out var dir) ? dir : 0,
            };
            using var kb = OpenKeyboard(model);
            kb.SetLighting(s);
            Console.WriteLine($"Lighting: {LightMode.For(model).FirstOrDefault(m => m.Id == s.Mode)?.Name ?? "?"} {s}");
            return 0;
        }

        case "custom" when rest.Count >= 2:
        {
            static (byte, byte, byte) Rgb(string hex) { var c = Convert.FromHexString(hex); return (c[0], c[1], c[2]); }
            var colors = Enumerable.Range(1, 127).ToDictionary(i => i, _ => Rgb(rest[1]));
            foreach (var a in rest.Skip(2).Where(a => a.Contains('=')))
                colors[int.Parse(a.Split('=')[0])] = Rgb(a.Split('=')[1]);
            using var kb = OpenKeyboard(ResolveModel());
            kb.SetCustomColors(colors, int.TryParse(Opt("--bright"), out var b) ? b : LightSettings.MaxLevel);
            Console.WriteLine($"Custom colors set ({colors.Count} LEDs)");
            return 0;
        }

        case "options":
        {
            var o = new KeyboardOptions
            {
                DisableWinKey = args.Contains("--nowin"),
                DisableAltTab = args.Contains("--noalttab"),
                DisableAltF4 = args.Contains("--noaltf4"),
                FnSwitch = args.Contains("--fnswitch"),
                SleepTime = int.TryParse(Opt("--sleep"), out var sl) ? sl : 2,
                KeyResponse = int.TryParse(Opt("--response"), out var rs) ? rs : 2,
            };
            using var kb = OpenKeyboard(ResolveModel());
            kb.SetOptions(o);
            Console.WriteLine($"Options: {o}");
            return 0;
        }

        case "side" when rest.Count >= 2:
        {
            var mode = Enum.Parse<SideLightMode>(rest[1], ignoreCase: true);
            using var kb = OpenKeyboard(ResolveModel());
            kb.SetSideLight(mode);
            Console.WriteLine($"Side light: {mode}");
            return 0;
        }

        case "battery":
        {
            var model = ResolveModel();
            using var dongle = SonixDongle.Open(model) ?? throw new InvalidOperationException($"{model.Name} dongle not found (VID {model.DongleVendorId:X4} / PID {model.DongleProductId:X4})");
            var level = dongle.ReadBattery();
            Console.WriteLine(level is int pct ? $"{model.Name} battery: {pct}%" : "No answer (the keyboard must be in 2.4G mode)");
            return level is null ? 3 : 0;
        }

        case "keys":
            foreach (var k in KeyboardLayout.Load(ResolveModel()).Keys.OrderBy(k => k.Index))
                Console.WriteLine($"{k.Index,4}  {k.Name,-10} code 0x{k.Code:X2}");
            return 0;

        case "remap":
        {
            var model = ResolveModel();
            var layer = args.Contains("--fn") ? KeyMapLayer.Fn : KeyMapLayer.Top;
            var map = args.Contains("--reset")
                ? new Dictionary<int, KeyAction>()
                : ParseRemaps(model, rest.Skip(1).Where(a => !a.StartsWith("--")));
            if (args.Contains("--dry"))
            {
                Dump($"{model.Name} {layer} layer table (04 {(layer == KeyMapLayer.Fn ? "27" : "11")}, 9 packets):", KeyMap.EncodeLayer(map));
                return 0;
            }
            using var kb = OpenKeyboard(model);
            kb.SetKeyMap(layer, map);
            Console.WriteLine($"{model.Name}: {layer} layer written ({map.Count} remapped key(s), all others default).");
            return 0;
        }

        case "macros":
        {
            var macros = args.Contains("--clear")
                ? new List<Macro>()
                : rest.Skip(1).Where(a => !a.StartsWith("--")).Select((m, i) => Macro.Parse(m, $"Macro {i}")).ToList();
            for (int i = 0; i < macros.Count; i++)
                Console.WriteLine($"  slot {i}: {macros[i].Steps.Count} step(s): {string.Join(" ", macros[i].Steps.Select(s => s.Describe()))}");
            Console.WriteLine($"  memory: {MacroCodec.Used(macros)} / {MacroCodec.BudgetBytes} bytes");
            if (args.Contains("--dry"))
            {
                var data = MacroCodec.Encode(macros, out int pages);
                Dump($"Macro image (04 19, 04 15 [8]={pages}, {pages} packets):", data);
                return 0;
            }
            var model = ResolveModel();
            using var kb = OpenKeyboard(model);
            kb.SetMacros(macros);
            Console.WriteLine($"{model.Name}: {macros.Count} macro(s) stored.");
            return 0;
        }

        case "raw" when rest.Count >= 2:
        {
            var bytes = Convert.FromHexString(string.Concat(rest.Skip(1)).Replace(" ", ""));
            using var kb = OpenKeyboard(ResolveModel());
            var reply = kb.SendPacket(bytes);
            Console.WriteLine(Convert.ToHexString(reply));
            return 0;
        }

        default:
            return Usage();
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
