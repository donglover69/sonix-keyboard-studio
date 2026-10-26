namespace Sonix.Keyboard;

public enum MacroAction
{
    KeyDown,
    KeyUp,
    MouseDown,
    MouseUp,
}

/// <summary>
/// One recorded input plus the pause after it, exactly like a stock t_macrorecord row.
/// <see cref="Code"/> is a HID keyboard usage for key steps and a button mask (1 left, 2 right, 4 middle) for mouse steps.
/// </summary>
public sealed record MacroStep(MacroAction Action, byte Code, int DelayMs = MacroCodec.MinDelayMs)
{
    public string Describe() => Action switch
    {
        MacroAction.KeyDown => $"↓ {HidKeys.NameOf(Code)}",
        MacroAction.KeyUp => $"↑ {HidKeys.NameOf(Code)}",
        MacroAction.MouseDown => $"↓ {MouseName(Code)}",
        MacroAction.MouseUp => $"↑ {MouseName(Code)}",
        _ => "?",
    };

    public static string MouseName(byte mask) => mask switch { 1 => "Left button", 2 => "Right button", 4 => "Middle button", _ => $"Button {mask}" };
}

public sealed class Macro
{
    public string Name { get; set; } = "Macro";
    public List<MacroStep> Steps { get; set; } = new();

    /// <summary>Types <paramref name="text"/> (US layout): each character is down, up; Shift is wrapped around shifted characters.</summary>
    public static List<MacroStep> TextSteps(string text, int delayMs = MacroCodec.MinDelayMs)
    {
        var steps = new List<MacroStep>();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (!HidKeys.TryFromChar(ch, out bool shift, out byte code)) continue;
            if (shift) steps.Add(new MacroStep(MacroAction.KeyDown, 0xE1, delayMs));
            steps.Add(new MacroStep(MacroAction.KeyDown, code, delayMs));
            steps.Add(new MacroStep(MacroAction.KeyUp, code, delayMs));
            if (shift) steps.Add(new MacroStep(MacroAction.KeyUp, 0xE1, delayMs));
        }
        return steps;
    }

    /// <summary>Presses a key combination: modifiers down, key down, key up, modifiers up (reverse order).</summary>
    public static List<MacroStep> ComboSteps(KeyModifiers mods, byte code, int delayMs = MacroCodec.MinDelayMs)
    {
        var modCodes = new List<byte>();
        if (mods.HasFlag(KeyModifiers.Ctrl)) modCodes.Add(0xE0);
        if (mods.HasFlag(KeyModifiers.Shift)) modCodes.Add(0xE1);
        if (mods.HasFlag(KeyModifiers.Alt)) modCodes.Add(0xE2);
        if (mods.HasFlag(KeyModifiers.Win)) modCodes.Add(0xE3);
        var steps = modCodes.Select(c => new MacroStep(MacroAction.KeyDown, c, delayMs)).ToList();
        if (code != 0)
        {
            steps.Add(new MacroStep(MacroAction.KeyDown, code, delayMs));
            steps.Add(new MacroStep(MacroAction.KeyUp, code, delayMs));
        }
        steps.AddRange(Enumerable.Reverse(modCodes).Select(c => new MacroStep(MacroAction.KeyUp, c, delayMs)));
        return steps;
    }

    /// <summary>CLI syntax: text:hello | combo:ctrl+shift+esc | steps:down:A,wait:50,up:A,click:left (wait sets the previous step's delay).</summary>
    public static Macro Parse(string spec, string? name = null)
    {
        int colon = spec.IndexOf(':');
        if (colon < 0) throw new FormatException($"Unknown macro '{spec}' (use text:, combo: or steps:)");
        string kind = spec[..colon].ToLowerInvariant(), rest = spec[(colon + 1)..];
        var m = new Macro { Name = name ?? spec };
        switch (kind)
        {
            case "text":
                m.Steps = TextSteps(rest);
                break;
            case "combo":
            {
                var a = KeyAction.Parse("key:" + rest);
                m.Steps = ComboSteps(a.Modifiers, a.Code);
                break;
            }
            case "steps":
                foreach (var part in rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var p = part.Split(':');
                    switch (p[0].ToLowerInvariant())
                    {
                        case "down": m.Steps.Add(new MacroStep(MacroAction.KeyDown, HidKeys.Code(p[1]))); break;
                        case "up": m.Steps.Add(new MacroStep(MacroAction.KeyUp, HidKeys.Code(p[1]))); break;
                        case "tap":
                            m.Steps.Add(new MacroStep(MacroAction.KeyDown, HidKeys.Code(p[1])));
                            m.Steps.Add(new MacroStep(MacroAction.KeyUp, HidKeys.Code(p[1])));
                            break;
                        case "click":
                        {
                            byte mask = p[1].ToLowerInvariant() switch { "left" => 1, "right" => 2, "middle" => 4, _ => throw new FormatException($"Unknown button '{p[1]}'") };
                            m.Steps.Add(new MacroStep(MacroAction.MouseDown, mask));
                            m.Steps.Add(new MacroStep(MacroAction.MouseUp, mask));
                            break;
                        }
                        case "wait":
                            if (m.Steps.Count == 0) throw new FormatException("wait needs a step before it");
                            m.Steps[^1] = m.Steps[^1] with { DelayMs = int.Parse(p[1]) };
                            break;
                        default: throw new FormatException($"Unknown macro step '{part}'");
                    }
                }
                break;
            default:
                throw new FormatException($"Unknown macro '{spec}' (use text:, combo: or steps:)");
        }
        return m;
    }
}

/// <summary>
/// Macro storage format, from the stock sender (DeviceDriver.exe 0x42d820; AULA F108 build 0x42d630). The keyboard gets one image:
/// a 100 × 4-byte directory (offset LE16 + 00 00 per macro slot, FF FF FF FF for a macro without steps, 0 after the last macro)
/// starting at 0, then from offset 400 one record per macro: [event count LE16][6 × 00][events]. Every stock record step becomes
/// two 4-byte events: the action [00 00 CODE TYPE] (TYPE B0 key down, 30 key up, 90 mouse down, 10 mouse up) and its delay
/// [ms LE16 00 50] with ms ≥ 10. The image is sent after 04 19 as PAGES × 64 bytes (PAGES = size/64 + 1, or + 2 when the size
/// is not a multiple of 64), announced by 04 15 … [8] = PAGES, with AA 55 in the last two bytes, then 04 02.
/// The stock app refuses to send when (steps + macros) × 8 &gt; 0xA8C.
/// </summary>
public static class MacroCodec
{
    public const int MaxMacros = 100;
    public const int DirectoryBytes = 400;
    public const int MinDelayMs = 10;
    public const int MaxDelayMs = 0xFFFF;
    /// <summary>Stock limit: (total steps + macro count) × 8 must not exceed this.</summary>
    public const int BudgetBytes = 0xA8C;

    public const byte TypeKeyDown = 0xB0;
    public const byte TypeKeyUp = 0x30;
    public const byte TypeMouseDown = 0x90;
    public const byte TypeMouseUp = 0x10;
    public const byte TypeDelay = 0x50;

    /// <summary>Bytes used against <see cref="BudgetBytes"/>.</summary>
    public static int Used(IReadOnlyList<Macro> macros) => (macros.Sum(m => m.Steps.Count) + macros.Count) * 8;

    /// <summary>Encodes all macros; returns PAGES × 64 bytes ready to send.</summary>
    public static byte[] Encode(IReadOnlyList<Macro> macros, out int pages)
    {
        if (macros.Count > MaxMacros) throw new InvalidOperationException($"At most {MaxMacros} macros fit in the keyboard");
        if (Used(macros) > BudgetBytes)
            throw new InvalidOperationException($"Macros use {Used(macros)} of {BudgetBytes} bytes of keyboard memory; remove some steps");

        var img = new byte[DirectoryBytes + BudgetBytes + 128];
        int offset = DirectoryBytes;
        for (int i = 0; i < macros.Count; i++)
        {
            var steps = macros[i].Steps;
            if (steps.Count == 0)
            {
                img[i * 4] = img[i * 4 + 1] = img[i * 4 + 2] = img[i * 4 + 3] = 0xFF;
                continue;
            }
            int events = steps.Count * 2;
            img[i * 4] = (byte)offset;
            img[i * 4 + 1] = (byte)(offset >> 8);
            img[offset] = (byte)events;
            img[offset + 1] = (byte)(events >> 8);
            int p = offset + 8;
            foreach (var s in steps)
            {
                img[p + 2] = s.Code;
                img[p + 3] = s.Action switch
                {
                    MacroAction.KeyDown => TypeKeyDown,
                    MacroAction.KeyUp => TypeKeyUp,
                    MacroAction.MouseDown => TypeMouseDown,
                    _ => TypeMouseUp,
                };
                int ms = Math.Clamp(s.DelayMs, MinDelayMs, MaxDelayMs);
                img[p + 4] = (byte)ms;
                img[p + 5] = (byte)(ms >> 8);
                img[p + 7] = TypeDelay;
                p += 8;
            }
            offset = p;
        }
        pages = offset / 64 + (offset % 64 == 0 ? 1 : 2);
        var data = new byte[pages * 64];
        Array.Copy(img, data, offset);
        data[^2] = 0xAA;
        data[^1] = 0x55;
        return data;
    }
}
