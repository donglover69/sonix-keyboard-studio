using System.Threading;
using System.Windows;
using KeyboardStudio.Services;
using Sonix.Keyboard;

namespace KeyboardStudio;

/// <summary>One supported keyboard: its profile, its saved settings and the service that talks to it.</summary>
public sealed record DeviceContext(KeyboardModel Model, DeviceSettings Settings, KeyboardService Service);

public partial class App : Application
{
    private const string InstanceName = "KeyboardStudio.SingleInstance";
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    private readonly HashSet<string> _lowBatteryWarned = new();

    /// <summary>App-wide preferences.</summary>
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>Every supported keyboard, connected or not; each is watched by its own service.</summary>
    public static IReadOnlyList<DeviceContext> Devices { get; private set; } = [];

    /// <summary>The keyboard the window is showing.</summary>
    public static DeviceContext ActiveDevice { get; private set; } = null!;

    public static KeyboardModel Model => ActiveDevice.Model;
    public static DeviceSettings Device => ActiveDevice.Settings;
    public static KeyboardService Keyboard => ActiveDevice.Service;

    /// <summary>Raised after <see cref="Select"/> switched to another keyboard.</summary>
    public static event Action? CurrentChanged;

    public static void Select(KeyboardModel model)
    {
        var next = Devices.First(d => d.Model == model);
        if (next == ActiveDevice) return;
        ActiveDevice = next;
        Settings.LastDevice = model.Id;
        Settings.Save();
        CurrentChanged?.Invoke();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, InstanceName, out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!first)
        {
            // Already running: ask that instance to show its window, then quit.
            _showSignal.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Settings = AppSettings.Load();
        ThemeManager.Apply(Settings);
        if (Settings.MigratedFromLegacy && Settings.StartWithWindows && !AppSettings.IsSandboxed) Autostart.ReplaceLegacy();

        Devices = KeyboardModel.All.Select(m =>
        {
            var s = DeviceSettings.Load(m);
            return new DeviceContext(m, s, new KeyboardService(m, Settings, s));
        }).ToList();

        // Show the keyboard used last, unless only another one is plugged in.
        var connected = SonixKeyboard.DetectAll();
        var last = KeyboardModel.ById(Settings.LastDevice);
        var start = last is not null && (connected.Count == 0 || connected.Contains(last)) ? last : connected.FirstOrDefault() ?? KeyboardModel.All[0];
        ActiveDevice = Devices.First(d => d.Model == start);

        var window = new MainWindow();
        MainWindow = window;
        _tray = new TrayIcon(ShowMain, ExitApp);

        bool minimized = e.Args.Contains("--minimized") && Settings.StartMinimized;
        if (!minimized) window.Show();

        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.Invoke(ShowMain);
        }) { IsBackground = true }.Start();

        foreach (var d in Devices)
        {
            var device = d;
            device.Service.BatteryChanged += () => Dispatcher.Invoke(() => OnBatteryChanged(device));
            device.Service.Start();
        }
        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        var parts = Devices.Where(d => d.Service.BatteryPercent is not null)
            .Select(d => $"{d.Model.ShortName} {d.Service.BatteryPercent}%{(d.Service.IsWireless ? "" : $" ({BatteryFormat.Age(d.Service)})")}");
        _tray?.SetText(string.Join(" · ", new[] { "Keyboard Studio" }.Concat(parts)));
    }

    private void OnBatteryChanged(DeviceContext d)
    {
        UpdateTrayText();
        var kb = d.Service;
        if (!kb.IsWireless || kb.BatteryPercent is not int pct) return;
        int threshold = Settings.BatteryWarnPercent;
        if (Settings.BatteryWarn && pct < threshold && _lowBatteryWarned.Add(d.Model.Id))
        {
            _tray?.Notify($"{d.Model.ShortName} battery low", $"{pct}% left. Plug in the USB cable to charge.", warning: true);
        }
        else if (pct >= threshold + 5)
        {
            _lowBatteryWarned.Remove(d.Model.Id); // re-arm after charging
        }
    }

    public void ShowMain()
    {
        var w = MainWindow;
        if (w is null) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    public void ExitApp()
    {
        Settings.Save();
        foreach (var d in Devices)
        {
            d.Settings.Save();
            d.Service.Dispose();
        }
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
