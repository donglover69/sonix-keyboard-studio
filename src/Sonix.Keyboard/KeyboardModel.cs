using Sonix.Keyboard.Lcd;

namespace Sonix.Keyboard;

/// <summary>A key combination the firmware handles on its own (no software command exists for it).</summary>
public sealed record FirmwareCombo(string Keys, string Action);

/// <summary>
/// Everything that differs between the supported keyboards. They all run SONiX firmware driven by builds of the same
/// stock driver (DeviceDriver.exe), so the protocol is shared; only IDs, the key layout, LCD limits and names differ.
/// </summary>
public sealed record KeyboardModel
{
    /// <summary>Stable id used for settings files and the CLI's --model option.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }

    public ushort VendorId { get; init; } = 0x0C45;
    public required ushort ProductId { get; init; }
    public required ushort DongleVendorId { get; init; }
    public required ushort DongleProductId { get; init; }

    /// <summary>Embedded layout file (generated from the stock layouts/rgb-keyboard.xml by re/layout2json.py).</summary>
    public required string LayoutResource { get; init; }
    public required LcdGeometry Lcd { get; init; }

    /// <summary>%LOCALAPPDATA% folder pattern of the stock driver's data (database, saved animations).</summary>
    public required string StockFolderPattern { get; init; }
    /// <summary>Folder of the earlier single-keyboard app (AK980 Studio / F108 Studio) whose settings are taken over.</summary>
    public required string LegacyAppFolder { get; init; }

    /// <summary>Effect names that differ from <see cref="LightMode.All"/> (from the model's language/1033.lan).</summary>
    public IReadOnlyDictionary<byte, string> EffectNames { get; init; } = new Dictionary<byte, string>();

    public string FirmwareLightsIntro { get; init; } = "";
    public IReadOnlyList<FirmwareCombo> FirmwareCombos { get; init; } = [];
    /// <summary>How to reset the keyboard to factory settings, if the manual documents it.</summary>
    public string? FactoryReset { get; init; }

    public override string ToString() => Name;

    public static readonly KeyboardModel Ak980 = new()
    {
        Id = "ak980",
        Name = "AJAZZ AK980 V2",
        ShortName = "AK980",
        ProductId = 0x8009,
        DongleVendorId = 0x0C45,
        DongleProductId = 0xFEFE,
        LayoutResource = "ak980-layout.json",
        Lcd = new LcdGeometry(MaxFrames: 140),
        StockFolderPattern = "AJAZZ*Files",
        LegacyAppFolder = "AK980Studio",
        FirmwareLightsIntro = "These have no software command (checked against every command in the stock driver), only key combos on the keyboard:",
        FirmwareCombos =
        [
            new("Fn + Z", "Logo light (next to the arrow keys) on / off"),
            new("Fn + Del", "Screen on / off"),
            new("Fn + X", "All lights on / off (per the AK980 V2 PRO manual)"),
            new("Fn + knob press", "Knob: volume ↔ screen menu"),
            new("—", "Red Fn key while charging and the Num Lock key highlight are drawn by the firmware on top of every effect; there's no switch for them."),
        ],
    };

    public static readonly KeyboardModel F108Pro = new()
    {
        Id = "f108pro",
        Name = "AULA F108 Pro",
        ShortName = "F108 Pro",
        ProductId = 0x800A,
        // The dongle enumerates with an Apple vendor ID (stock config.xml: VID_05AC&PID_024F&MI_03).
        DongleVendorId = 0x05AC,
        DongleProductId = 0x024F,
        LayoutResource = "f108pro-layout.json",
        Lcd = new LcdGeometry(MaxFrames: 141),
        StockFolderPattern = "AULA*Files",
        LegacyAppFolder = "F108Studio",
        EffectNames = new Dictionary<byte, string> { [5] = "Falling" },
        FirmwareLightsIntro = "The stock driver has no command for the light bar or the side lights on this keyboard (its side-light menu is disabled), so they stay on the firmware's key combos. From the F108 Pro manual:",
        FirmwareCombos =
        [
            new("Fn + Tab", "Backlight: next color"),
            new("Fn + \\", "Backlight: next effect"),
            new("Fn + ↑ / ↓", "Backlight brightness + / −"),
            new("Fn + ← / →", "Backlight speed − / +"),
            new("Fn + L Shift", "Light bar: next effect"),
            new("Fn + L Ctrl", "Light bar: speed"),
            new("Fn + L Alt", "Light bar: brightness"),
            new("Fn + Z", "Light bar: next color"),
            new("Fn + R Shift", "Side light: next effect"),
            new("Fn + R Ctrl", "Side light: speed"),
            new("Fn + R Alt", "Side light: brightness"),
            new("Fn + ?", "Side light: next color"),
            new("Fn + knob", "Knob: screen menu ↔ volume (knob light on = volume)"),
            new("Fn + Esc 3 s", "Factory reset"),
        ],
        FactoryReset = "Holding Fn + Esc for 3 s resets the keyboard to factory settings.",
    };

    public static readonly IReadOnlyList<KeyboardModel> All = [Ak980, F108Pro];

    public static KeyboardModel? ById(string? id) =>
        All.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>CLI-friendly lookup: id, short name or anything containing the product number ("ak980", "f108").</summary>
    public static KeyboardModel? Find(string name) =>
        ById(name) ?? All.FirstOrDefault(m =>
            m.ShortName.Replace(" ", "").Contains(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
}
