using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AK980Studio.Pages;
using AK980Studio.Services;

namespace AK980Studio;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        Logo.Source = AppIcon.CreateImageSource(64);
        Icon = AppIcon.CreateImageSource(32);
        ApplyScale();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this, App.Settings.Theme != "Light");
        App.Keyboard.StatusChanged += () => Dispatcher.Invoke(UpdateStatus);
        App.Keyboard.BatteryChanged += () => Dispatcher.Invoke(UpdateBattery);
        // Keeps "5 min ago" fresh while the keyboard is wired and can't report its charge.
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        tick.Tick += (_, _) => UpdateBattery();
        tick.Start();
        UpdateBattery();
        Show("screen");
    }

    private void UpdateBattery()
    {
        var kb = App.Keyboard;
        if (kb.BatteryPercent is not int pct)
        {
            BatteryCard.Visibility = Visibility.Collapsed;
            return;
        }
        BatteryCard.Visibility = Visibility.Visible;
        BatteryText.Text = $"{pct}%";
        BatteryBar.Value = pct;
        // Segoe Fluent/MDL2 battery glyphs: Battery0..9 = E850..E859, Battery10 = E83F.
        BatteryIcon.Text = pct >= 95 ? "" : ((char)(0xE850 + pct / 10)).ToString();
        string level = pct > 50 ? "Good" : pct >= App.Settings.BatteryWarnPercent ? "Warn" : "Bad";
        BatteryBar.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, level);
        BatteryIcon.SetResourceReference(TextBlock.ForegroundProperty, level);
        BatteryAge.Text = BatteryFormat.Age(kb);
        BatteryCard.ToolTip = kb.IsWireless
            ? "Read from the keyboard over the 2.4G dongle"
            : "The keyboard only reports its battery in 2.4G mode; this is the last reading.";
    }

    public void ApplyScale()
    {
        UiScale.ScaleX = UiScale.ScaleY = Math.Clamp(App.Settings.UiScale, 0.8, 1.6);
    }

    private void UpdateStatus()
    {
        var kb = App.Keyboard;
        StatusText.Text = kb.IsBusy ? "Working..." : kb.Status;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            kb.IsBusy ? "Accent" : kb.IsConnected || kb.IsWireless ? "Good" : "Warn");
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && IsInitialized)
            Show(tag);
    }

    private void Show(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "lighting" => new LightingPage(),
                "perkey" => new PerKeyPage(),
                "keys" => new KeysPage(),
                "macros" => new MacrosPage(),
                "keyboard" => new KeyboardPage(),
                "settings" => new SettingsPage(),
                _ => new ScreenPage(),
            };
            _pages[tag] = page;
        }
        if (PageHost is not null) PageHost.Content = page;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        App.Settings.Save();
        if (App.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        ((App)Application.Current).ExitApp();
    }
}
