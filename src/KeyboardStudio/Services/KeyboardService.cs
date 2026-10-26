using System.Windows.Threading;
using Sonix.Keyboard;
using Sonix.Keyboard.Lcd;

namespace KeyboardStudio.Services;

/// <summary>
/// Owns the keyboard connection: polls for plug/unplug, and runs every device operation on one
/// background worker so the UI never blocks and commands never interleave.
/// Nothing is sent on connect unless the user enabled it in settings.
/// </summary>
public sealed class KeyboardService : IDisposable
{
    private readonly AppSettings _app;
    private readonly DeviceSettings _settings;
    private readonly DispatcherTimer _poll;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SonixKeyboard? _kb;
    private SonixDongle? _dongle;
    private DateTime _lastClockSync = DateTime.MinValue;
    private DateTime _lastBatteryPoll = DateTime.MinValue;
    private bool _batteryPolling;

    public event Action? StatusChanged;
    public event Action? BatteryChanged;

    public bool IsConnected => _kb is not null;
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "Looking for keyboard...";

    /// <summary>True while the keyboard answers over the dongle (2.4G mode).</summary>
    public bool IsWireless { get; private set; }
    public int? BatteryPercent => _settings.LastBatteryPercent;
    public DateTime? BatteryTime => _settings.LastBatteryTime;

    public KeyboardModel Model { get; }

    public KeyboardService(KeyboardModel model, AppSettings app, DeviceSettings settings)
    {
        Model = model;
        _app = app;
        _settings = settings;
        Status = $"Looking for the {model.ShortName}...";
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += async (_, _) => await PollAsync();
    }

    public void Start()
    {
        _poll.Start();
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        if (IsBusy) return;
        var (usb, dongle) = await Task.Run(() => SonixKeyboard.Detect(Model));

        if (usb && _kb is null)
        {
            try
            {
                _kb = await Task.Run(() => SonixKeyboard.Open(Model));
            }
            catch (Exception ex)
            {
                SetStatus($"Keyboard found but could not be opened: {ex.Message}");
                return;
            }
            if (_kb is not null)
            {
                _kb.CommandDelayMs = _app.CommandDelayMs;
                SetWireless(false);
                SetStatus($"Connected: {_kb.Product} (USB)");
                await OnConnectedAsync();
            }
        }
        else if (!usb && _kb is not null)
        {
            _kb.Dispose();
            _kb = null;
            // Switching the keyboard to 2.4G drops the USB device; check the battery right away.
            _lastBatteryPoll = DateTime.MinValue;
        }
        else if (_kb is not null && _settings.SyncClockHourly && DateTime.Now - _lastClockSync > TimeSpan.FromHours(1))
        {
            await RunAsync(kb => SyncClock(kb), "Clock synced");
        }

        if (usb) return;
        if (!dongle)
        {
            _dongle?.Dispose();
            _dongle = null;
            SetWireless(false);
            SetStatus($"Looking for the {Model.ShortName}...");
            return;
        }
        if (DateTime.Now - _lastBatteryPoll >= TimeSpan.FromSeconds(Math.Max(5, _app.BatteryPollSeconds)))
            await PollBatteryAsync();
        SetStatus(IsWireless
            ? "Connected over 2.4G. Switch the keyboard to wired (USB) mode to change settings."
            : "2.4G dongle found, but the keyboard isn't answering (asleep, off, or in Bluetooth mode).");
    }

    /// <summary>Asks the keyboard for its charge over the dongle, like the stock app does every 15 s in 2.4G mode.</summary>
    private async Task PollBatteryAsync()
    {
        if (_batteryPolling) return;
        _batteryPolling = true;
        _lastBatteryPoll = DateTime.Now;
        try
        {
            int? level = await Task.Run(() =>
            {
                _dongle ??= SonixDongle.Open(Model);
                return _dongle?.ReadBattery();
            });
            SetWireless(level is not null);
            if (level is int pct)
            {
                bool changed = _settings.LastBatteryPercent != pct;
                _settings.LastBatteryPercent = pct;
                _settings.LastBatteryTime = DateTime.Now;
                if (changed) _settings.Save(); // keep the last reading even if the app is killed
                BatteryChanged?.Invoke();
            }
        }
        catch (Exception)
        {
            // The dongle was unplugged mid-request; reopen on the next poll.
            _dongle?.Dispose();
            _dongle = null;
            SetWireless(false);
        }
        finally
        {
            _batteryPolling = false;
        }
    }

    private void SetWireless(bool wireless)
    {
        if (IsWireless == wireless) return;
        IsWireless = wireless;
        BatteryChanged?.Invoke();
    }

    private async Task OnConnectedAsync()
    {
        if (_settings.RestoreLightingOnConnect)
            await RunAsync(kb => kb.SetLighting(_settings.Light), null);
        if (_settings.SyncClockOnConnect)
            await RunAsync(kb => SyncClock(kb), null);
    }

    private void SyncClock(SonixKeyboard kb)
    {
        kb.SyncClock(DateTime.Now, _settings.LcdSlot);
        _lastClockSync = DateTime.Now;
    }

    /// <summary>Runs <paramref name="op"/> on the worker. Returns false (and reports why) if it failed.</summary>
    public async Task<bool> RunAsync(Action<SonixKeyboard> op, string? doneMessage)
    {
        var kb = _kb;
        if (kb is null)
        {
            SetStatus(IsWireless
                ? "The keyboard is in 2.4G mode. Switch it to wired (USB) mode to change settings."
                : "Keyboard not connected (USB cable required)");
            return false;
        }
        await _gate.WaitAsync();
        IsBusy = true;
        StatusChanged?.Invoke();
        try
        {
            kb.CommandDelayMs = _app.CommandDelayMs;
            await Task.Run(() => op(kb));
            if (doneMessage is not null) SetStatus(doneMessage);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
            // A failed transfer usually means the device went away; force a reconnect on the next poll.
            _kb?.Dispose();
            _kb = null;
            return false;
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
            StatusChanged?.Invoke();
        }
    }

    public Task<bool> SyncClockAsync() => RunAsync(SyncClock, $"Clock synced ({DateTime.Now:HH:mm})");

    public Task<bool> SetLightingAsync(LightSettings s) => RunAsync(kb => kb.SetLighting(s), null);

    public Task<bool> SetCustomColorsAsync(IReadOnlyDictionary<int, (byte, byte, byte)> colors, int brightness) =>
        RunAsync(kb => kb.SetCustomColors(colors, brightness), "Per-key colors applied");

    public Task<bool> SetOptionsAsync(KeyboardOptions o) => RunAsync(kb => kb.SetOptions(o), "Keyboard settings saved");

    /// <summary>Writes both key layers, like the stock app's full sync.</summary>
    public Task<bool> SetKeyMapsAsync(IReadOnlyDictionary<int, KeyAction> top, IReadOnlyDictionary<int, KeyAction> fn) =>
        RunAsync(kb =>
        {
            kb.SetKeyMap(KeyMapLayer.Top, top);
            kb.SetKeyMap(KeyMapLayer.Fn, fn);
        }, "Key remaps saved");

    public Task<bool> SetMacrosAsync(IReadOnlyList<Macro> macros) => RunAsync(kb => kb.SetMacros(macros), "Macros saved");

    public Task<bool> UploadAsync(LcdAnimation anim, IProgress<double> progress) =>
        RunAsync(kb => kb.UploadLcd(anim, _settings.LcdSlot, progress), "Upload complete");

    private void SetStatus(string s)
    {
        if (Status == s) return;
        Status = s;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _poll.Stop();
        _kb?.Dispose();
        _dongle?.Dispose();
    }
}
