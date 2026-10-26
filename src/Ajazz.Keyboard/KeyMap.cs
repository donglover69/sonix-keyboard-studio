namespace Ajazz.Keyboard;

/// <summary>Modifier bits as the firmware expects them (the HID boot-report modifier byte).</summary>
[Flags]
public enum KeyModifiers : byte
{
    None = 0,
    Ctrl = 0x01,
    Shift = 0x02,
    Alt = 0x04,
    Win = 0x08,
    RightCtrl = 0x10,
    RightShift = 0x20,
    RightAlt = 0x40,
    RightWin = 0x80,
}

public enum KeyActionKind
{
    /// <summary>Firmware default for the key (entry 00 00 00 00).</summary>
    Default,
    /// <summary>Key does nothing (stock "Disable Key", entry 05 03).</summary>
    Disabled,
    /// <summary>A keyboard key with optional modifiers (entry 02 MODS CODE).</summary>
    Key,
    /// <summary>Mouse button or wheel (entry 01 01 BTN / 01 03 DIR; stock combo values 1..8 at 0x411260).</summary>
    Mouse,
    /// <summary>Consumer-control usage: media and launcher keys (entry 03 LO HI; stock tables at 0x4183e0 and 0x4192d0).</summary>
    Media,
    /// <summary>Plays a stored macro (entry 06 SLOT MODE COUNT).</summary>
    Macro,
    /// <summary>Up to three keys pressed together (stock "Multi-Key", entry 07 K1 K2 K3).</summary>
    MultiKey,
}

public enum MouseFunction
{
    LeftClick,
    RightClick,
    MiddleClick,
    DoubleClick,
    Button4,
    Button5,
    WheelUp,
    WheelDown,
}

/// <summary>Stock key dialog play modes (combo item data at DeviceDriver.exe 0x4101c9..0x41032e, strings 242..247).</summary>
public enum MacroPlayMode
{
    /// <summary>"Play Once"</summary>
    Once = 0,
    /// <summary>"Play Multiple Times" with a loop count 1..255</summary>
    Repeat = 1,
    /// <summary>"Stop when key is pressed again"</summary>
    Toggle = 2,
    /// <summary>"Release to stop"</summary>
    Hold = 3,
    /// <summary>"Click to play"</summary>
    Click = 4,
}

public enum KeyMapLayer
{
    /// <summary>What the key does normally (op 0x11).</summary>
    Top = 0,
    /// <summary>What the key does while Fn is held (op 0x27).</summary>
    Fn = 1,
}

/// <summary>
/// One key's function. Encoded exactly like the stock driver's key records (DeviceDriver.exe 0x418030 / 0x4183e0; AULA F108 build 0x418230 /
/// 0x4185e0): 4 bytes per key, indexed by the layout's key_index.
/// </summary>
public sealed record KeyAction
{
    public static readonly KeyAction Default = new();

    public KeyActionKind Kind { get; init; }
    public KeyModifiers Modifiers { get; init; }
    /// <summary>HID keyboard usage for <see cref="KeyActionKind.Key"/>.</summary>
    public byte Code { get; init; }
    public MouseFunction Mouse { get; init; }
    /// <summary>16-bit consumer usage for <see cref="KeyActionKind.Media"/>.</summary>
    public ushort Usage { get; init; }
    /// <summary>Macro slot: position in the macro list sent to the keyboard, 0-based.</summary>
    public int MacroIndex { get; init; }
    public MacroPlayMode MacroMode { get; init; }
    /// <summary>Loop count 1..255 for <see cref="MacroPlayMode.Repeat"/>.</summary>
    public int MacroCount { get; init; } = 1;
    /// <summary>Up to three HID usages for <see cref="KeyActionKind.MultiKey"/>.</summary>
    public byte[] Keys { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDefault => Kind == KeyActionKind.Default;

    public byte[] ToEntry()
    {
        var e = new byte[4];
        switch (Kind)
        {
            case KeyActionKind.Default:
                break;
            case KeyActionKind.Disabled:
                e[0] = 0x05;
                e[1] = 0x03;
                break;
            case KeyActionKind.Key:
                e[0] = 0x02;
                if (Modifiers == KeyModifiers.None && Code is >= 0xE0 and <= 0xE7)
                {
                    // A bare modifier key is sent as its bit in the modifier byte, like the stock app does.
                    e[1] = (byte)(1 << (Code - 0xE0));
                }
                else
                {
                    e[1] = (byte)Modifiers;
                    e[2] = Code;
                }
                break;
            case KeyActionKind.Mouse:
                e[0] = 0x01;
                (e[1], e[2]) = Mouse switch
                {
                    MouseFunction.LeftClick => ((byte)0x01, (byte)0x01),
                    MouseFunction.RightClick => ((byte)0x01, (byte)0x02),
                    MouseFunction.MiddleClick => ((byte)0x01, (byte)0x04),
                    MouseFunction.DoubleClick => ((byte)0x01, (byte)0x03),
                    MouseFunction.Button5 => ((byte)0x01, (byte)0x08),
                    MouseFunction.Button4 => ((byte)0x01, (byte)0x10),
                    MouseFunction.WheelUp => ((byte)0x03, (byte)0x01),
                    MouseFunction.WheelDown => ((byte)0x03, (byte)0xFF),
                    _ => ((byte)0x01, (byte)0x01),
                };
                break;
            case KeyActionKind.Media:
                e[0] = 0x03;
                e[1] = (byte)Usage;
                e[2] = (byte)(Usage >> 8);
                break;
            case KeyActionKind.Macro:
                e[0] = 0x06;
                e[1] = (byte)Math.Clamp(MacroIndex, 0, MacroCodec.MaxMacros - 1);
                e[2] = (byte)MacroMode;
                e[3] = (byte)(MacroMode == MacroPlayMode.Repeat ? Math.Clamp(MacroCount, 1, 255) : 0);
                break;
            case KeyActionKind.MultiKey:
                e[0] = 0x07;
                for (int i = 0; i < Math.Min(3, Keys.Length); i++) e[1 + i] = Keys[i];
                break;
        }
        return e;
    }

    /// <summary>Short human-readable label, e.g. "Ctrl+Shift+A", "Left click", "Macro 2 ×3".</summary>
    public string Describe(IReadOnlyList<string>? macroNames = null) => Kind switch
    {
        KeyActionKind.Default => "Default",
        KeyActionKind.Disabled => "Disabled",
        KeyActionKind.Key => HidKeys.Describe(Modifiers, Code),
        KeyActionKind.Mouse => MouseNames[(int)Mouse],
        KeyActionKind.Media => MediaKeys.NameOf(Usage),
        KeyActionKind.Macro => (macroNames is not null && MacroIndex < macroNames.Count ? macroNames[MacroIndex] : $"Macro {MacroIndex}")
                               + MacroMode switch { MacroPlayMode.Repeat => $" ×{MacroCount}", MacroPlayMode.Toggle => " (toggle)", MacroPlayMode.Hold => " (hold)", MacroPlayMode.Click => " (click)", _ => "" },
        KeyActionKind.MultiKey => string.Join("+", Keys.Select(k => HidKeys.NameOf(k))),
        _ => "?",
    };

    public static readonly string[] MouseNames =
        ["Left click", "Right click", "Middle click", "Double click", "Mouse button 4", "Mouse button 5", "Wheel up", "Wheel down"];

    /// <summary>
    /// Parses the CLI/preset syntax: default | off | key:[ctrl+][shift+][alt+][win+]NAME | mouse:left|right|middle|double|btn4|btn5|wheelup|wheeldown |
    /// media:NAME | macro:N[:once|repeat:COUNT|toggle|hold] | multi:A,B,C
    /// </summary>
    public static KeyAction Parse(string spec)
    {
        var s = spec.Trim();
        if (s.Equals("default", StringComparison.OrdinalIgnoreCase)) return Default;
        if (s.Equals("off", StringComparison.OrdinalIgnoreCase) || s.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            return new KeyAction { Kind = KeyActionKind.Disabled };
        int colon = s.IndexOf(':');
        if (colon < 0) throw new FormatException($"Unknown key action '{spec}'");
        string kind = s[..colon].ToLowerInvariant(), rest = s[(colon + 1)..];
        switch (kind)
        {
            case "key":
            {
                var parts = rest.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                KeyModifiers mods = KeyModifiers.None;
                byte code = 0;
                foreach (var p in parts)
                {
                    switch (p.ToLowerInvariant())
                    {
                        case "ctrl": mods |= KeyModifiers.Ctrl; break;
                        case "shift": mods |= KeyModifiers.Shift; break;
                        case "alt": mods |= KeyModifiers.Alt; break;
                        case "win": case "gui": mods |= KeyModifiers.Win; break;
                        default: code = HidKeys.Code(p); break;
                    }
                }
                return new KeyAction { Kind = KeyActionKind.Key, Modifiers = mods, Code = code };
            }
            case "mouse":
                return new KeyAction
                {
                    Kind = KeyActionKind.Mouse,
                    Mouse = rest.ToLowerInvariant() switch
                    {
                        "left" => MouseFunction.LeftClick, "right" => MouseFunction.RightClick, "middle" => MouseFunction.MiddleClick,
                        "double" => MouseFunction.DoubleClick, "btn4" => MouseFunction.Button4, "btn5" => MouseFunction.Button5,
                        "wheelup" => MouseFunction.WheelUp, "wheeldown" => MouseFunction.WheelDown,
                        _ => throw new FormatException($"Unknown mouse action '{rest}'"),
                    },
                };
            case "media":
                return new KeyAction { Kind = KeyActionKind.Media, Usage = MediaKeys.Usage(rest) };
            case "macro":
            {
                var parts = rest.Split(':');
                var a = new KeyAction { Kind = KeyActionKind.Macro, MacroIndex = int.Parse(parts[0]) };
                if (parts.Length > 1)
                {
                    a = parts[1].ToLowerInvariant() switch
                    {
                        "once" => a with { MacroMode = MacroPlayMode.Once },
                        "repeat" => a with { MacroMode = MacroPlayMode.Repeat, MacroCount = parts.Length > 2 ? int.Parse(parts[2]) : 2 },
                        "toggle" => a with { MacroMode = MacroPlayMode.Toggle },
                        "hold" => a with { MacroMode = MacroPlayMode.Hold },
                        "click" => a with { MacroMode = MacroPlayMode.Click },
                        _ => throw new FormatException($"Unknown macro mode '{parts[1]}'"),
                    };
                }
                return a;
            }
            case "multi":
                return new KeyAction
                {
                    Kind = KeyActionKind.MultiKey,
                    Keys = rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3).Select(HidKeys.Code).ToArray(),
                };
            default:
                throw new FormatException($"Unknown key action '{spec}'");
        }
    }
}

/// <summary>Builds the 576-byte key table the keyboard stores per layer (9 × 64-byte packets, last two bytes AA 55).</summary>
public static class KeyMap
{
    public const int TableBytes = 576;
    public const int Packets = 9;
    /// <summary>Highest key index that fits before the AA 55 terminator.</summary>
    public const int MaxIndex = 142;

    public static byte[] EncodeLayer(IReadOnlyDictionary<int, KeyAction> actions)
    {
        var t = new byte[TableBytes];
        foreach (var (index, action) in actions)
        {
            if (index < 1 || index > MaxIndex || action is null) continue;
            action.ToEntry().CopyTo(t, index * 4);
        }
        t[574] = 0xAA;
        t[575] = 0x55;
        return t;
    }
}

/// <summary>HID keyboard usages (page 7) with the names used by the CLI and the app.</summary>
public static class HidKeys
{
    public static readonly IReadOnlyList<(string Name, byte Code)> All = BuildAll();

    private static readonly Dictionary<string, byte> ByName = BuildIndex();

    private static List<(string, byte)> BuildAll()
    {
        var l = new List<(string, byte)>();
        for (int i = 0; i < 26; i++) l.Add((((char)('A' + i)).ToString(), (byte)(0x04 + i)));
        for (int i = 1; i <= 9; i++) l.Add((i.ToString(), (byte)(0x1D + i)));
        l.Add(("0", 0x27));
        l.AddRange(new (string, byte)[]
        {
            ("Enter", 0x28), ("Esc", 0x29), ("Backspace", 0x2A), ("Tab", 0x2B), ("Space", 0x2C), ("-", 0x2D), ("=", 0x2E),
            ("[", 0x2F), ("]", 0x30), ("\\", 0x31), (";", 0x33), ("'", 0x34), ("`", 0x35), (",", 0x36), (".", 0x37), ("/", 0x38),
            ("CapsLock", 0x39),
        });
        for (int i = 1; i <= 12; i++) l.Add(($"F{i}", (byte)(0x39 + i)));
        l.AddRange(new (string, byte)[]
        {
            ("PrintScreen", 0x46), ("ScrollLock", 0x47), ("Pause", 0x48), ("Insert", 0x49), ("Home", 0x4A), ("PageUp", 0x4B),
            ("Delete", 0x4C), ("End", 0x4D), ("PageDown", 0x4E), ("Right", 0x4F), ("Left", 0x50), ("Down", 0x51), ("Up", 0x52),
            ("NumLock", 0x53), ("Num/", 0x54), ("Num*", 0x55), ("Num-", 0x56), ("Num+", 0x57), ("NumEnter", 0x58),
            ("Num1", 0x59), ("Num2", 0x5A), ("Num3", 0x5B), ("Num4", 0x5C), ("Num5", 0x5D), ("Num6", 0x5E), ("Num7", 0x5F),
            ("Num8", 0x60), ("Num9", 0x61), ("Num0", 0x62), ("Num.", 0x63), ("Menu", 0x65),
        });
        for (int i = 13; i <= 24; i++) l.Add(($"F{i}", (byte)(0x68 + i - 13)));
        l.AddRange(new (string, byte)[]
        {
            ("LeftCtrl", 0xE0), ("LeftShift", 0xE1), ("LeftAlt", 0xE2), ("LeftWin", 0xE3),
            ("RightCtrl", 0xE4), ("RightShift", 0xE5), ("RightAlt", 0xE6), ("RightWin", 0xE7),
            // The stock driver's code for the Fn key (VK 0xFE -> 0xAF in DeviceDriver.exe 0x451a20, code="0xAF" in the layout).
            ("Fn", 0xAF),
        });
        return l;
    }

    private static Dictionary<string, byte> BuildIndex()
    {
        var d = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, c) in All) d[n] = c;
        // Aliases from the layout file and common spellings
        d["Return"] = 0x28; d["Escape"] = 0x29; d["Bksp"] = 0x2A; d["Del"] = 0x4C; d["Ins"] = 0x49; d["PgUp"] = 0x4B; d["PgDn"] = 0x4E;
        d["PrtSc"] = 0x46; d["ScrLk"] = 0x47; d["Caps"] = 0x39; d["Num"] = 0x53; d["App"] = 0x65; d["Application"] = 0x65;
        d["Ctrl"] = 0xE0; d["Shift"] = 0xE1; d["Alt"] = 0xE2; d["Win"] = 0xE3; d["Gui"] = 0xE3;
        d["↑"] = 0x52; d["↓"] = 0x51; d["←"] = 0x50; d["→"] = 0x4F;
        return d;
    }

    public static byte Code(string name) =>
        ByName.TryGetValue(name, out var c) ? c
        : name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToByte(name[2..], 16)
        : throw new FormatException($"Unknown key '{name}'");

    public static bool TryCode(string name, out byte code) => ByName.TryGetValue(name, out code);

    public static string NameOf(byte code)
    {
        foreach (var (n, c) in All) if (c == code) return n;
        return $"0x{code:X2}";
    }

    public static string Describe(KeyModifiers mods, byte code)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Ctrl) || mods.HasFlag(KeyModifiers.RightCtrl)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Shift) || mods.HasFlag(KeyModifiers.RightShift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.RightAlt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Win) || mods.HasFlag(KeyModifiers.RightWin)) parts.Add("Win");
        if (code != 0) parts.Add(NameOf(code));
        return parts.Count == 0 ? "(nothing)" : string.Join("+", parts);
    }

    /// <summary>
    /// Windows virtual-key code → HID usage, as the stock recorder converts it (DeviceDriver.exe 0x451a20; the media-key VKs
    /// are left out because the firmware gets those as consumer usages instead). Returns 0 for keys it has no code for.
    /// </summary>
    public static byte FromVirtualKey(int vk)
    {
        if (vk is >= 0x41 and <= 0x5A) return (byte)(vk - 0x3D);   // A..Z
        if (vk is >= 0x31 and <= 0x39) return (byte)(vk - 0x13);   // 1..9
        if (vk is >= 0x70 and <= 0x7B) return (byte)(vk - 0x36);   // F1..F12
        if (vk is >= 0x7C and <= 0x87) return (byte)(vk - 0x14);   // F13..F24
        if (vk is >= 0x61 and <= 0x69) return (byte)(vk - 0x08);   // Num1..Num9
        return vk switch
        {
            0x30 => 0x27, 0x0D => 0x28, 0x1B => 0x29, 0x08 => 0x2A, 0x09 => 0x2B, 0x20 => 0x2C, 0xBD => 0x2D, 0xBB => 0x2E,
            0xDB => 0x2F, 0xDD => 0x30, 0xDC => 0x31, 0xBA => 0x33, 0xDE => 0x34, 0xC0 => 0x35, 0xBC => 0x36, 0xBE => 0x37,
            0xBF => 0x38, 0x14 => 0x39, 0x2C => 0x46, 0x91 => 0x47, 0x13 => 0x48, 0x2D => 0x49, 0x24 => 0x4A, 0x21 => 0x4B,
            0x2E => 0x4C, 0x23 => 0x4D, 0x22 => 0x4E, 0x27 => 0x4F, 0x25 => 0x50, 0x28 => 0x51, 0x26 => 0x52, 0x90 => 0x53,
            0x6F => 0x54, 0x6A => 0x55, 0x6D => 0x56, 0x6B => 0x57, 0x6C => 0x58, 0x60 => 0x62, 0x6E => 0x63,
            0xA2 => 0xE0, 0xA0 => 0xE1, 0xA4 => 0xE2, 0x5B => 0xE3, 0xA3 => 0xE4, 0xA1 => 0xE5, 0xA5 => 0xE6, 0x5C => 0xE7,
            0x5D => 0x65, 0xE2 => 0x64, 0xFE => 0xAF,
            _ => 0,
        };
    }

    /// <summary>US layout: character → (needs Shift, HID usage). Used to turn text into a macro.</summary>
    public static bool TryFromChar(char ch, out bool shift, out byte code)
    {
        shift = false;
        code = 0;
        if (ch >= 'a' && ch <= 'z') { code = (byte)(0x04 + ch - 'a'); return true; }
        if (ch >= 'A' && ch <= 'Z') { shift = true; code = (byte)(0x04 + ch - 'A'); return true; }
        if (ch >= '1' && ch <= '9') { code = (byte)(0x1E + ch - '1'); return true; }
        const string plain = "0\n\t -=[]\\;'`,./";
        const string shifted = ")!@#$%^&*(_+{}|:\"~<>?";
        const string shiftedBase = "0123456789-=[]\\;'`,./";
        int i = plain.IndexOf(ch);
        if (i >= 0)
        {
            code = ch switch { '0' => 0x27, '\n' => 0x28, '\t' => 0x2B, ' ' => 0x2C, '-' => 0x2D, '=' => 0x2E, '[' => 0x2F, ']' => 0x30,
                '\\' => 0x31, ';' => 0x33, '\'' => 0x34, '`' => 0x35, ',' => 0x36, '.' => 0x37, '/' => 0x38, _ => 0 };
            return code != 0;
        }
        i = shifted.IndexOf(ch);
        if (i >= 0)
        {
            shift = true;
            return TryFromChar(shiftedBase[i], out _, out code);
        }
        return false;
    }
}

/// <summary>Consumer-control usages (page 0x0C): exactly the ones the stock driver can send.</summary>
public static class MediaKeys
{
    public static readonly IReadOnlyList<(string Name, ushort Usage)> All =
    [
        ("Play/Pause", 0xCD), ("Stop", 0xB7), ("Previous track", 0xB6), ("Next track", 0xB5),
        ("Volume up", 0xE9), ("Volume down", 0xEA), ("Mute", 0xE2),
        ("Calculator", 0x192), ("Mail", 0x18A), ("Media player", 0x183), ("My Computer", 0x194),
        ("Browser home", 0x223), ("Browser refresh", 0x227), ("Browser stop", 0x226), ("Browser back", 0x224),
        ("Browser forward", 0x225), ("Browser favorites", 0x22A), ("Browser search", 0x221),
    ];

    private static readonly Dictionary<string, ushort> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["play"] = 0xCD, ["playpause"] = 0xCD, ["stop"] = 0xB7, ["prev"] = 0xB6, ["previous"] = 0xB6, ["next"] = 0xB5,
        ["volup"] = 0xE9, ["voldown"] = 0xEA, ["mute"] = 0xE2, ["calc"] = 0x192, ["calculator"] = 0x192, ["mail"] = 0x18A,
        ["player"] = 0x183, ["computer"] = 0x194, ["mycomputer"] = 0x194, ["home"] = 0x223, ["refresh"] = 0x227,
        ["back"] = 0x224, ["forward"] = 0x225, ["favorites"] = 0x22A, ["search"] = 0x221,
    };

    public static ushort Usage(string name)
    {
        if (Aliases.TryGetValue(name, out var u)) return u;
        foreach (var (n, usage) in All) if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return usage;
        // Only usages the stock driver sends are accepted (see the list above).
        throw new FormatException($"Unknown media key '{name}'");
    }

    public static string NameOf(ushort usage)
    {
        foreach (var (n, u) in All) if (u == usage) return n;
        return $"Consumer 0x{usage:X3}";
    }
}
