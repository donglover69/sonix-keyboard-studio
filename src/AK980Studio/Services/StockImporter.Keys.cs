using System.IO;
using Ajazz.Keyboard;
using Ajazz.Keyboard.Layout;

namespace AK980Studio.Services;

public static partial class StockImporter
{
    public sealed record StockKeys(
        Dictionary<int, KeyAction> Top,
        Dictionary<int, KeyAction> Fn,
        List<Macro> Macros,
        List<string> Skipped);

    /// <summary>
    /// Key remaps and macros of the stock app's active profile (t_key_macro_data, t_macro_data, t_macrorecord), converted the
    /// way the stock sender encodes them (DeviceDriver.exe 0x418030 and 0x42d820). Host-side functions (open program, open
    /// website, send text) only work while the stock app runs, so they are skipped and listed in <see cref="StockKeys.Skipped"/>.
    /// Returns null when no stock database exists.
    /// </summary>
    public static StockKeys? ReadKeys()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in Directory.EnumerateDirectories(local, StockFolderPattern))
        {
            var dbDir = Path.Combine(root, "db");
            if (!Directory.Exists(dbDir)) continue;
            foreach (var dbPath in Directory.EnumerateFiles(dbDir, "*_datav1.db"))
            {
                var copy = Path.Combine(Path.GetTempPath(), $"{TempPrefix}{Guid.NewGuid():N}.db");
                try
                {
                    File.Copy(dbPath, copy);
                    using var db = new Sqlite(copy);
                    var result = ReadKeys(db);
                    if (result is not null) return result;
                }
                catch (Exception)
                {
                    // Unreadable database: try the next one.
                }
                finally
                {
                    try { File.Delete(copy); } catch (IOException) { }
                }
            }
        }
        return null;
    }

    private static StockKeys? ReadKeys(Sqlite db)
    {
        var profile = db.Query("SELECT profile FROM t_profile_data WHERE status = 1 LIMIT 1").FirstOrDefault()?[0] ?? "1";
        var layout = KeyboardLayout.Load();
        var result = new StockKeys(new(), new(), new(), new());

        // Macros in the order the stock sender numbers them (its SELECT * FROM t_macro_data).
        var macroIds = new List<string>();
        foreach (var m in db.Query("SELECT macro_id, name FROM t_macro_data"))
        {
            macroIds.Add(m[0]);
            var macro = new Macro { Name = m[1] };
            foreach (var r in db.Query($"SELECT type, value, delay_time FROM t_macrorecord WHERE macro_id = {int.Parse(m[0])} ORDER BY record_index ASC"))
            {
                int type = int.Parse(r[0]), value = int.Parse(r[1]), delay = Math.Max(MacroCodec.MinDelayMs, int.Parse(r[2]));
                MacroStep? step = type switch
                {
                    2 => new(MacroAction.KeyDown, HidKeys.FromVirtualKey(value), delay),
                    3 => new(MacroAction.KeyUp, HidKeys.FromVirtualKey(value), delay),
                    4 when value is >= 1 and <= 3 => new(MacroAction.MouseDown, StockMouseMask(value), delay),
                    5 when value is >= 1 and <= 3 => new(MacroAction.MouseUp, StockMouseMask(value), delay),
                    _ => null,
                };
                if (step is not null && step.Code != 0) macro.Steps.Add(step);
                else if (macro.Steps.Count > 0) macro.Steps[^1] = macro.Steps[^1] with { DelayMs = macro.Steps[^1].DelayMs + delay };
            }
            result.Macros.Add(macro);
        }

        foreach (var r in db.Query($"SELECT fn_layer, key_code, macro_type, macro_value, macro_value2, macro_value3, macro_desc FROM t_key_macro_data WHERE profile = {int.Parse(profile)}"))
        {
            int fn = int.Parse(r[0]), code = int.Parse(r[1]), type = int.Parse(r[2]);
            int v1 = int.Parse(r[3]), v2 = int.Parse(r[4]), v3 = int.Parse(r[5]);
            var key = layout.Keys.FirstOrDefault(k => k.Code == code);
            var action = ConvertStock(type, v1, v2, v3, macroIds);
            if (key is null || action is null)
            {
                result.Skipped.Add($"{(fn == 1 ? "Fn+" : "")}{key?.Name ?? $"key 0x{code:X2}"}: {r[6]}");
                continue;
            }
            (fn == 1 ? result.Fn : result.Top)[key.Index] = action;
        }
        return result;
    }

    /// <summary>Stock mouse values 1 left, 2 middle, 3 right → button mask (0x42d820).</summary>
    private static byte StockMouseMask(int value) => value switch { 1 => 1, 2 => 4, _ => 2 };

    /// <summary>Key type 6 ("Multimedia"), values 1..7 (0x418030).</summary>
    private static readonly ushort[] StockMedia = [0xCD, 0xB7, 0xB6, 0xB5, 0xE9, 0xEA, 0xE2];

    /// <summary>Key type 12, values 500..517 (0x4192d0).</summary>
    private static readonly ushort[] StockLaunch =
        [0x194, 0x192, 0x18A, 0x183, 0xCD, 0xB7, 0xB6, 0xB5, 0xE9, 0xEA, 0xE2, 0x223, 0x227, 0x226, 0x224, 0x225, 0x22A, 0x221];

    /// <summary>Key type 7 ("Windows Shortcut"), values 1..8 (0x418030).</summary>
    private static readonly (byte Mods, byte Code)[] StockShortcuts =
        [(0x08, 0x07), (0x08, 0x08), (0x08, 0x0F), (0x01, 0x1A), (0x04, 0x2B), (0x01, 0x06), (0x01, 0x19), (0x01, 0x1B)];

    private static KeyAction? ConvertStock(int type, int v1, int v2, int v3, List<string> macroIds)
    {
        static KeyAction Of(KeyActionKind k) => new() { Kind = k };
        switch (type)
        {
            case 1:
                return Of(KeyActionKind.Disabled);
            case 2:
                return Of(KeyActionKind.Key) with { Modifiers = (KeyModifiers)v2, Code = (byte)v1 };
            case 13:
                return Of(KeyActionKind.MultiKey) with { Keys = new[] { v1, v2, v3 }.Where(c => c != 0).Select(c => (byte)c).ToArray() };
            case 3:
            {
                int index = macroIds.IndexOf(v1.ToString());
                if (index < 0) return null;
                return Of(KeyActionKind.Macro) with { MacroIndex = index, MacroMode = (MacroPlayMode)Math.Clamp(v2, 0, 4), MacroCount = Math.Max(1, v3) };
            }
            case 5 when v1 is >= 1 and <= 8:
                return Of(KeyActionKind.Mouse) with
                {
                    Mouse = v1 switch
                    {
                        1 => MouseFunction.LeftClick, 2 => MouseFunction.MiddleClick, 3 => MouseFunction.RightClick, 4 => MouseFunction.DoubleClick,
                        5 => MouseFunction.WheelUp, 6 => MouseFunction.WheelDown, 7 => MouseFunction.Button5, _ => MouseFunction.Button4,
                    },
                };
            case 6 when v1 is >= 1 and <= 7:
                return Of(KeyActionKind.Media) with { Usage = StockMedia[v1 - 1] };
            case 12 when v1 is >= 500 and <= 517:
                return Of(KeyActionKind.Media) with { Usage = StockLaunch[v1 - 500] };
            case 7 when v1 is >= 1 and <= 8:
                return Of(KeyActionKind.Key) with { Modifiers = (KeyModifiers)StockShortcuts[v1 - 1].Mods, Code = StockShortcuts[v1 - 1].Code };
            case 7 when v1 is 10 or 11:
                return Of(KeyActionKind.Key) with { Modifiers = v1 == 11 ? KeyModifiers.Ctrl : KeyModifiers.Shift };
            default:
                return null;
        }
    }
}
