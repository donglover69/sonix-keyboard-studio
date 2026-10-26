namespace Sonix.Keyboard;

/// <summary>Which controls a lighting mode honours (the stock DB's t_light_data.config_func column).</summary>
[Flags]
public enum LightCaps
{
    None = 0,
    Brightness = 0x01,
    Speed = 0x02,
    Direction2 = 0x04,
    Direction4 = 0x08,
    Color = 0x10,
    Rainbow = 0x20,
}

public sealed record LightMode(byte Id, string Name, LightCaps Caps)
{
    /// <summary>The effects with the names a model's stock app uses (they only differ in a few names).</summary>
    public static IReadOnlyList<LightMode> For(KeyboardModel model) =>
        All.Select(m => model.EffectNames.TryGetValue(m.Id, out var name) ? m with { Name = name } : m).ToList();

    /// <summary>Modes in firmware order (names from the AK980's language/1033.lan 521..540, caps from the stock profile DB).</summary>
    public static readonly IReadOnlyList<LightMode> All =
    [
        new(1, "Static", (LightCaps)0x31),
        new(2, "Single On", (LightCaps)0x33),
        new(3, "Single Off", (LightCaps)0x33),
        new(4, "Glittering", (LightCaps)0x33),
        new(5, "Fluttering", (LightCaps)0x33),
        new(6, "Colourful", (LightCaps)0x03),
        new(7, "Breath", (LightCaps)0x33),
        new(8, "Spectrum", (LightCaps)0x03),
        new(9, "Outward", (LightCaps)0x33),
        new(10, "Scrolling", (LightCaps)0x3B),
        new(11, "Rolling", (LightCaps)0x37),
        new(12, "Rotating", (LightCaps)0x37),
        new(13, "Explode", (LightCaps)0x33),
        new(14, "Launch", (LightCaps)0x33),
        new(15, "Ripples", (LightCaps)0x33),
        new(16, "Flowing", (LightCaps)0x37),
        new(17, "Pulsating", (LightCaps)0x33),
        new(18, "Tilt", (LightCaps)0x37),
        new(19, "Shuttle", (LightCaps)0x33),
        new(0, "LED Off", LightCaps.None),
    ];
}

/// <summary>Firmware options sent with opcode 0x17 (stock DeviceDriver.exe 0x414060).</summary>
public sealed record KeyboardOptions
{
    public bool DisableAltTab { get; init; }
    public bool DisableAltF4 { get; init; }
    public bool DisableWinKey { get; init; }
    public bool FnSwitch { get; init; }
    /// <summary>Stock UI order: 0 = no sleep, 1 = 1 min, 2 = 5 min, 3 = 30 min.</summary>
    public int SleepTime { get; init; } = 2;
    /// <summary>Key response (debounce) level as stored by the stock app.</summary>
    public int KeyResponse { get; init; } = 2;

    public byte[] ToPacket()
    {
        var p = new byte[64];
        p[1] = 0x01;
        p[2] = (byte)(DisableAltTab ? 1 : 0);
        p[3] = (byte)(DisableAltF4 ? 1 : 0);
        p[4] = (byte)(DisableWinKey ? 1 : 0);
        p[5] = (byte)(FnSwitch ? 1 : 0);
        p[6] = (byte)SleepTime;
        p[8] = (byte)KeyResponse;
        p[62] = 0xAA;
        p[63] = 0x55;
        return p;
    }
}

/// <summary>Secondary light zone modes (combo item data 0x20.. in the stock app, names from 1033.lan 661..670).</summary>
public enum SideLightMode : byte
{
    Flowing = 0x20,
    Red = 0x21,
    Yellow = 0x22,
    Green = 0x23,
    IceBlue = 0x24,
    Blue = 0x25,
    Pink = 0x26,
    White = 0x27,
    Neon = 0x28,
    Off = 0x29,
}

/// <summary>Settings for the main backlight effect. Brightness and speed are 0..5 like the stock app.</summary>
public sealed record LightSettings
{
    public const int MaxLevel = 5;

    public byte Mode { get; init; } = 1;
    public byte R { get; init; } = 0xB4;
    public byte G { get; init; } = 0x8C;
    public byte B { get; init; } = 0xFF;
    public bool Rainbow { get; init; }
    public int Brightness { get; init; } = MaxLevel;
    public int Speed { get; init; } = 3;
    public int Direction { get; init; }

    /// <summary>Data packet for opcode 0x13: MODE R G B .. .. .. .. RAINBOW BRIGHT SPEED DIR .. .. AA 55.</summary>
    public byte[] ToPacket()
    {
        var p = new byte[64];
        p[0] = Mode;
        if (Mode != 0)
        {
            p[1] = R;
            p[2] = G;
            p[3] = B;
            p[8] = (byte)(Rainbow ? 1 : 0);
            p[9] = (byte)Math.Clamp(Brightness, 0, MaxLevel);
            p[10] = (byte)Math.Clamp(Speed, 0, MaxLevel);
            p[11] = (byte)Direction;
        }
        p[14] = 0xAA;
        p[15] = 0x55;
        return p;
    }
}
