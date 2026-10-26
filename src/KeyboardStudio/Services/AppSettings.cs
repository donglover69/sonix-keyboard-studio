using System.IO;
using System.Text.Json;
using Sonix.Keyboard;

namespace KeyboardStudio.Services;

/// <summary>
/// App-wide preferences (look, startup, battery alerts), persisted to %LOCALAPPDATA%\KeyboardStudio\settings.json.
/// Everything tied to one keyboard lives in <see cref="DeviceSettings"/>.
/// </summary>
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
    public int CommandDelayMs { get; set; } = 35;

    // Battery (read through the 2.4G dongle; the keyboards only answer in 2.4G mode)
    public int BatteryPollSeconds { get; set; } = 15;
    public bool BatteryWarn { get; set; } = true;
    public int BatteryWarnPercent { get; set; } = 20;

    /// <summary><see cref="KeyboardModel.Id"/> of the keyboard shown last.</summary>
    public string? LastDevice { get; set; }

    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// Where this app's data and the older apps' settings live: %LOCALAPPDATA%, or KEYBOARDSTUDIO_HOME for a test copy.
    /// The stock drivers' data is always read from the real %LOCALAPPDATA% (read-only).
    /// </summary>
    internal static string DataRoot { get; } =
        Environment.GetEnvironmentVariable("KEYBOARDSTUDIO_HOME") is { Length: > 0 } home
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Running against a test copy of the data (KEYBOARDSTUDIO_HOME): never touches Windows autostart on its own.</summary>
    public static bool IsSandboxed => Environment.GetEnvironmentVariable("KEYBOARDSTUDIO_HOME") is { Length: > 0 };

    public static string Folder { get; } = Path.Combine(DataRoot, "KeyboardStudio");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>settings.json of the single-keyboard app a model had before (AK980 Studio, F108 Studio).</summary>
    internal static string LegacyFile(KeyboardModel model) => Path.Combine(DataRoot, model.LegacyAppFolder, "settings.json");

    /// <summary>True on the first start, when settings were taken over from AK980 Studio or F108 Studio.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool MigratedFromLegacy { get; private set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception)
        {
            // A corrupt settings file should never stop the app from opening.
        }

        // First start: take over the look and behaviour of an earlier single-keyboard app (same property names).
        var s = new AppSettings();
        foreach (var model in KeyboardModel.All)
        {
            try
            {
                var file = LegacyFile(model);
                if (!File.Exists(file)) continue;
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json) ?? new();
                s.LastDevice = model.Id;
                s.MigratedFromLegacy = true;
                break;
            }
            catch (Exception)
            {
                // Unreadable legacy settings: start fresh.
            }
        }
        Library.MigrateLegacy();
        s.Save();
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
