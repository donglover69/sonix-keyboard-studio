using System.IO;
using System.Text.Json;
using Ajazz.Keyboard;
using Ajazz.Keyboard.Lcd;

namespace AK980Studio.Services;

public sealed record LightPreset(string Name, LightSettings Light);

/// <summary>Everything the user can customize, persisted to %LOCALAPPDATA%\AK980Studio\settings.json.</summary>
public sealed class AppSettings
{
    // Look & feel
    public string Theme { get; set; } = "Dark";
    public string Accent { get; set; } = "#B48CFF";
    public double UiScale { get; set; } = 1.0;

    // Behaviour
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool RestoreLightingOnConnect { get; set; }

    /// <summary>The stock app always did this on launch, which is what reset the screen. Off by default.</summary>
    public bool SyncClockOnConnect { get; set; }
    public bool SyncClockHourly { get; set; }

    /// <summary>LCD slot the keyboard should show. Uploads and clock syncs both use it, so they never disagree.</summary>
    public byte LcdSlot { get; set; } = 1;
    public int CommandDelayMs { get; set; } = 35;

    // LCD editor defaults
    public LcdFitMode LcdFit { get; set; } = LcdFitMode.Fit;
    public string LcdBackground { get; set; } = "#000000";
    public bool LcdDither { get; set; } = true;
    public double LcdSpeed { get; set; } = 1.0;
    public string? LastUploadedId { get; set; }

    // Lighting
    public LightSettings Light { get; set; } = new();
    public bool LiveApply { get; set; } = true;
    public List<LightPreset> LightPresets { get; set; } = new();

    // Per-key custom colors: LED index -> "#RRGGBB"
    public Dictionary<int, string> PerKeyColors { get; set; } = new();
    public int PerKeyBrightness { get; set; } = LightSettings.MaxLevel;

    // Firmware options (Keyboard page)
    public KeyboardOptions KeyboardOptions { get; set; } = new();

    // Key remaps (key index -> action; missing keys are default) and macros (list position = slot on the keyboard)
    public Dictionary<int, KeyAction> KeyMapTop { get; set; } = new();
    public Dictionary<int, KeyAction> KeyMapFn { get; set; } = new();
    public List<Macro> Macros { get; set; } = new();
    /// <summary>Remaps and macros were taken over from the stock app's database (done once, also for existing settings files).</summary>
    public bool StockKeysImported { get; set; }
    /// <summary>What the last successful Apply sent, so the pages can show unsent changes.</summary>
    public string? KeysAppliedHash { get; set; }
    public string? MacrosAppliedHash { get; set; }

    // Battery (read through the 2.4G dongle; the keyboard only answers in 2.4G mode)
    public int BatteryPollSeconds { get; set; } = 15;
    public bool BatteryWarn { get; set; } = true;
    public int BatteryWarnPercent { get; set; } = 20;
    public int? LastBatteryPercent { get; set; }
    public DateTime? LastBatteryTime { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AK980Studio");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        AppSettings? loaded = null;
        try
        {
            if (File.Exists(FilePath))
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
        }
        catch (Exception)
        {
            // A corrupt settings file should never stop the app from opening.
        }
        var s = loaded ?? new AppSettings();
        // First run: start from whatever the stock app last put on the keyboard.
        if (loaded is null && StockImporter.ReadActiveLighting() is { } light) s.Light = light;
        if (!s.StockKeysImported)
        {
            s.StockKeysImported = true;
            s.ImportStockKeys(alreadyOnKeyboard: true);
            s.Save();
        }
        // Nothing configured and nothing ever sent: the keyboard is at its defaults, so there is nothing to apply.
        if (s.KeysAppliedHash is null && s.KeyMapTop.Count == 0 && s.KeyMapFn.Count == 0) s.KeysAppliedHash = s.KeysHash();
        if (s.MacrosAppliedHash is null && s.Macros.Count == 0) s.MacrosAppliedHash = s.MacrosHash();
        return s;
    }

    /// <summary>Replaces remaps and macros with the stock app's active profile. Returns a summary, or null if there is no stock data.</summary>
    public string? ImportStockKeys(bool alreadyOnKeyboard = false)
    {
        if (StockImporter.ReadKeys() is not { } stock) return null;
        KeyMapTop = stock.Top;
        KeyMapFn = stock.Fn;
        Macros = stock.Macros;
        // On first run the keyboard already holds exactly this (the stock app sends it on every change).
        if (alreadyOnKeyboard)
        {
            KeysAppliedHash = KeysHash();
            MacrosAppliedHash = MacrosHash();
        }
        string summary = $"{stock.Top.Count} remap(s), {stock.Fn.Count} Fn-layer remap(s), {stock.Macros.Count} macro(s)";
        return stock.Skipped.Count == 0 ? summary : $"{summary}; skipped (needs the stock app running): {string.Join(", ", stock.Skipped)}";
    }

    public string KeysHash() =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            KeyMap.EncodeLayer(KeyMapTop).Concat(KeyMap.EncodeLayer(KeyMapFn)).ToArray()));

    public string MacrosHash()
    {
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(MacroCodec.Encode(Macros, out _)));
        }
        catch (InvalidOperationException)
        {
            return "too-big";
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
