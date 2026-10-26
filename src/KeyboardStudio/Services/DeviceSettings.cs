using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sonix.Keyboard;
using Sonix.Keyboard.Lcd;

namespace KeyboardStudio.Services;

public sealed record LightPreset(string Name, LightSettings Light);

/// <summary>
/// Everything stored for one keyboard, persisted to %LOCALAPPDATA%\KeyboardStudio\devices\&lt;model id&gt;.json.
/// Property names match the old AK980 Studio / F108 Studio settings.json, so those files load as-is.
/// </summary>
public sealed class DeviceSettings
{
    [JsonIgnore]
    public KeyboardModel Model { get; private set; } = KeyboardModel.Ak980;

    // Sent on connect only when the user turned it on
    public bool RestoreLightingOnConnect { get; set; }
    /// <summary>The stock app always did this on launch, which is what reset the screen. Off by default.</summary>
    public bool SyncClockOnConnect { get; set; }
    public bool SyncClockHourly { get; set; }

    /// <summary>LCD page the keyboard should show. Uploads and clock syncs both use it, so they never disagree.</summary>
    public byte LcdSlot { get; set; } = 1;

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
    /// <summary>Remaps and macros were taken over from the stock app's database (done once, also for older settings files).</summary>
    public bool StockKeysImported { get; set; }
    /// <summary>What the last successful Apply sent, so the pages can show unsent changes.</summary>
    public string? KeysAppliedHash { get; set; }
    public string? MacrosAppliedHash { get; set; }

    // Last battery reading through the dongle
    public int? LastBatteryPercent { get; set; }
    public DateTime? LastBatteryTime { get; set; }

    public static string Folder { get; } = Path.Combine(AppSettings.Folder, "devices");

    private string FilePath => Path.Combine(Folder, $"{Model.Id}.json");

    public static DeviceSettings Load(KeyboardModel model)
    {
        DeviceSettings? loaded = null;
        bool save = false;
        foreach (var file in new[] { Path.Combine(Folder, $"{model.Id}.json"), AppSettings.LegacyFile(model) })
        {
            try
            {
                if (!File.Exists(file)) continue;
                loaded = JsonSerializer.Deserialize<DeviceSettings>(File.ReadAllText(file), AppSettings.Json);
                save = file != Path.Combine(Folder, $"{model.Id}.json");
                if (loaded is not null) break;
            }
            catch (Exception)
            {
                // A corrupt settings file should never stop the app from opening.
            }
        }

        var s = loaded ?? new DeviceSettings();
        s.Model = model;
        // First run for this keyboard: start from whatever the stock app last put on it.
        if (loaded is null && StockImporter.ReadActiveLighting(model) is { } light)
        {
            s.Light = light;
            save = true;
        }
        if (!s.StockKeysImported)
        {
            s.StockKeysImported = true;
            s.ImportStockKeys(alreadyOnKeyboard: true);
            save = true;
        }
        // Nothing configured and nothing ever sent: the keyboard is at its defaults, so there is nothing to apply.
        if (s.KeysAppliedHash is null && s.KeyMapTop.Count == 0 && s.KeyMapFn.Count == 0) s.KeysAppliedHash = s.KeysHash();
        if (s.MacrosAppliedHash is null && s.Macros.Count == 0) s.MacrosAppliedHash = s.MacrosHash();
        if (save) s.Save();
        return s;
    }

    /// <summary>Replaces remaps and macros with the stock app's active profile. Returns a summary, or null if there is no stock data.</summary>
    public string? ImportStockKeys(bool alreadyOnKeyboard = false)
    {
        if (StockImporter.ReadKeys(Model) is not { } stock) return null;
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
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, AppSettings.Json));
    }
}
