using System.Drawing.Imaging;
using Ajazz.Keyboard;
using Ajazz.Keyboard.Lcd;

static int Usage()
{
    Console.WriteLine("""
        ak980 - AJAZZ AK980 command line tool

          ak980 list                                   list the keyboard's HID interfaces
          ak980 time [--slot N]                        set the LCD clock to the PC's local time
          ak980 lcd <image|gif|frame-dir> [--slot N] [--fit fit|fill|stretch] [--delay MS]
                                                       upload a picture/animation to the LCD
          ak980 preview <image|gif> <out-dir> [--fit ...]
                                                       render the LCD frames to PNGs without touching the keyboard
          ak980 light <mode 0-19> [--color RRGGBB] [--bright 0-5] [--speed 0-5] [--dir N] [--rainbow]
          ak980 side <off|flowing|red|yellow|green|iceblue|blue|pink|white|neon>
          ak980 battery                                read the battery level through the 2.4G dongle
          ak980 keys                                   list key indices and names used by remap
          ak980 remap [--fn] [--dry] <Key>=<action> ... write a whole layer: listed keys get the action, all others their default
          ak980 remap [--fn] [--dry] --reset           write a layer with every key at its default
                actions: default | off | key:[ctrl+][shift+][alt+][win+]Name | mouse:left|right|middle|double|btn4|btn5|wheelup|wheeldown
                         media:play|stop|prev|next|volup|voldown|mute|calc|mail|player|computer|home|refresh|back|forward|favorites|search
                         macro:N[:once|repeat:COUNT|toggle|hold|click] | multi:A,B,C
          ak980 macros [--dry] <macro> ...             store macros in slots 0, 1, ... (replaces all macros on the keyboard)
          ak980 macros [--dry] --clear                 remove every macro
                macros:  text:hello | combo:ctrl+shift+esc | steps:down:A,wait:50,up:A,tap:B,click:left
          ak980 raw <hex bytes>                        send one 64-byte command packet, print the reply
        """);
    return 1;
}

static void Dump(string title, byte[] data)
{
    Console.WriteLine(title);
    for (int i = 0; i < data.Length; i += 32)
        Console.WriteLine($"  {i,4}: {Convert.ToHexString(data.AsSpan(i, Math.Min(32, data.Length - i)))}");
}

Dictionary<int, KeyAction> ParseRemaps(IEnumerable<string> specs)
{
    var layout = Ajazz.Keyboard.Layout.KeyboardLayout.Load();
    var map = new Dictionary<int, KeyAction>();
    foreach (var spec in specs)
    {
        int eq = spec.IndexOf('=');
        if (eq <= 0) throw new FormatException($"Expected Key=action, got '{spec}'");
        string keyName = spec[..eq];
        var key = layout.ByName(keyName)
                  ?? (int.TryParse(keyName, out var idx) ? layout.ByIndex(idx) : null)
                  ?? throw new FormatException($"Unknown key '{keyName}' (see 'ak980 keys')");
        map[key.Index] = KeyAction.Parse(spec[(eq + 1)..]);
        Console.WriteLine($"  {key.Name,-10} (index {key.Index,3}) -> {map[key.Index].Describe()}  [{Convert.ToHexString(map[key.Index].ToEntry())}]");
    }
    return map;
}

string? Opt(string name) =>
    Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

LcdFitMode Fit() => Enum.TryParse<LcdFitMode>(Opt("--fit"), true, out var f) ? f : LcdFitMode.Fit;

byte Slot() => byte.TryParse(Opt("--slot"), out var s) ? s : (byte)1;

Ak980Keyboard OpenKeyboard() =>
    Ak980Keyboard.Open() ?? throw new InvalidOperationException("AK980 not found on USB (VID 0C45 / PID 8009)");

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length == 0) return Usage();

try
{
    switch (args[0])
    {
        case "list":
            foreach (var d in Ak980Keyboard.ListInterfaces())
                Console.WriteLine($"{d.ProductId:X4} MI_{d.Interface:X2} page {d.UsagePage:X4}:{d.Usage:X2} in {d.InputLength,4} out {d.OutputLength,4} feat {d.FeatureLength,3}  {d.Product}");
            return 0;

        case "time":
        {
            using var kb = OpenKeyboard();
            kb.SyncClock(DateTime.Now, Slot());
            Console.WriteLine($"Clock set to {DateTime.Now:yyyy-MM-dd HH:mm:ss} (slot {Slot()})");
            return 0;
        }

        case "lcd" when args.Length >= 2:
        {
            using var anim = LcdAnimation.FromPath(args[1], LcdGeometry.AK980, Fit());
            if (int.TryParse(Opt("--delay"), out var delay))
                anim.Frames.ForEach(f => f.DelayMs = delay);
            using var kb = OpenKeyboard();
            Console.WriteLine($"Uploading {anim.Frames.Count} frame(s) to slot {Slot()}...");
            var progress = new Progress<double>(p => Console.Write($"\r  {p:P0}   "));
            kb.UploadLcd(anim, Slot(), progress);
            Console.WriteLine("\nDone.");
            return 0;
        }

        case "preview" when args.Length >= 3:
        {
            using var anim = LcdAnimation.FromPath(args[1], LcdGeometry.AK980, Fit());
            Directory.CreateDirectory(args[2]);
            for (int i = 0; i < anim.Frames.Count; i++)
                anim.Frames[i].Image.Save(Path.Combine(args[2], $"{i:D3}.png"), ImageFormat.Png);
            var blob = anim.Encode();
            Console.WriteLine($"{anim.Frames.Count} frame(s), delays {string.Join(",", anim.Frames.Select(f => f.DelayMs).Distinct())} ms, blob {blob.Length} bytes ({blob.Length / LcdGeometry.PageSize} pages)");
            return 0;
        }

        case "light" when args.Length >= 2:
        {
            var color = Convert.FromHexString(Opt("--color") ?? "B48CFF");
            var s = new LightSettings
            {
                Mode = byte.Parse(args[1]),
                R = color[0], G = color[1], B = color[2],
                Rainbow = args.Contains("--rainbow"),
                Brightness = int.TryParse(Opt("--bright"), out var br) ? br : LightSettings.MaxLevel,
                Speed = int.TryParse(Opt("--speed"), out var sp) ? sp : 3,
                Direction = int.TryParse(Opt("--dir"), out var dir) ? dir : 0,
            };
            using var kb = OpenKeyboard();
            kb.SetLighting(s);
            Console.WriteLine($"Lighting: {LightMode.All.FirstOrDefault(m => m.Id == s.Mode)?.Name ?? "?"} {s}");
            return 0;
        }

        case "custom" when args.Length >= 2:
        {
            // custom <RRGGBB for every LED> [index=RRGGBB ...] [--bright N]
            static (byte, byte, byte) Rgb(string hex) { var c = Convert.FromHexString(hex); return (c[0], c[1], c[2]); }
            var colors = Enumerable.Range(1, 127).ToDictionary(i => i, _ => Rgb(args[1]));
            foreach (var a in args.Skip(2).Where(a => a.Contains('=')))
                colors[int.Parse(a.Split('=')[0])] = Rgb(a.Split('=')[1]);
            using var kb = OpenKeyboard();
            kb.SetCustomColors(colors, int.TryParse(Opt("--bright"), out var b) ? b : LightSettings.MaxLevel);
            Console.WriteLine($"Custom colors set ({colors.Count} LEDs)");
            return 0;
        }

        case "options":
        {
            // options [--sleep 0-3] [--response N] [--nowin] [--noalttab] [--noaltf4] [--fnswitch]
            var o = new KeyboardOptions
            {
                DisableWinKey = args.Contains("--nowin"),
                DisableAltTab = args.Contains("--noalttab"),
                DisableAltF4 = args.Contains("--noaltf4"),
                FnSwitch = args.Contains("--fnswitch"),
                SleepTime = int.TryParse(Opt("--sleep"), out var sl) ? sl : 2,
                KeyResponse = int.TryParse(Opt("--response"), out var rs) ? rs : 2,
            };
            using var kb = OpenKeyboard();
            kb.SetOptions(o);
            Console.WriteLine($"Options: {o}");
            return 0;
        }

        case "side" when args.Length >= 2:
        {
            var mode = Enum.Parse<SideLightMode>(args[1], ignoreCase: true);
            using var kb = OpenKeyboard();
            kb.SetSideLight(mode);
            Console.WriteLine($"Side/logo light: {mode}");
            return 0;
        }

        case "battery":
        {
            using var dongle = Ak980Dongle.Open() ?? throw new InvalidOperationException("2.4G dongle not found");
            var level = dongle.ReadBattery();
            Console.WriteLine(level is int pct ? $"Battery: {pct}%" : "No answer (the keyboard must be in 2.4G mode)");
            return level is null ? 3 : 0;
        }

        case "keys":
            foreach (var k in Ajazz.Keyboard.Layout.KeyboardLayout.Load().Keys.OrderBy(k => k.Index))
                Console.WriteLine($"{k.Index,4}  {k.Name,-10} code 0x{k.Code:X2}");
            return 0;

        case "remap":
        {
            var layer = args.Contains("--fn") ? KeyMapLayer.Fn : KeyMapLayer.Top;
            var map = args.Contains("--reset")
                ? new Dictionary<int, KeyAction>()
                : ParseRemaps(args.Skip(1).Where(a => !a.StartsWith("--")));
            if (args.Contains("--dry"))
            {
                Dump($"{layer} layer table (04 {(layer == KeyMapLayer.Fn ? "27" : "11")}, 9 packets):", KeyMap.EncodeLayer(map));
                return 0;
            }
            using var kb = OpenKeyboard();
            kb.SetKeyMap(layer, map);
            Console.WriteLine($"{layer} layer written ({map.Count} remapped key(s), all others default).");
            return 0;
        }

        case "macros":
        {
            var macros = args.Contains("--clear")
                ? new List<Macro>()
                : args.Skip(1).Where(a => !a.StartsWith("--")).Select((m, i) => Macro.Parse(m, $"Macro {i}")).ToList();
            for (int i = 0; i < macros.Count; i++)
                Console.WriteLine($"  slot {i}: {macros[i].Steps.Count} step(s): {string.Join(" ", macros[i].Steps.Select(s => s.Describe()))}");
            Console.WriteLine($"  memory: {MacroCodec.Used(macros)} / {MacroCodec.BudgetBytes} bytes");
            if (args.Contains("--dry"))
            {
                var data = MacroCodec.Encode(macros, out int pages);
                Dump($"Macro image (04 19, 04 15 [8]={pages}, {pages} packets):", data);
                return 0;
            }
            using var kb = OpenKeyboard();
            kb.SetMacros(macros);
            Console.WriteLine($"{macros.Count} macro(s) stored.");
            return 0;
        }

        case "raw" when args.Length >= 2:
        {
            var bytes = Convert.FromHexString(string.Concat(args.Skip(1)).Replace(" ", ""));
            using var kb = OpenKeyboard();
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
