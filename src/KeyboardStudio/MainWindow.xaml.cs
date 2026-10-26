using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shapes;
using KeyboardStudio.Pages;
using KeyboardStudio.Services;

namespace KeyboardStudio;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly Dictionary<DeviceContext, (RadioButton Button, Ellipse Dot, TextBlock State)> _deviceRows = new();
    private string _page = "screen";

    public MainWindow()
    {
        InitializeComponent();
        Logo.Source = AppIcon.CreateImageSource(64);
        Icon = AppIcon.CreateImageSource(32);
        ApplyScale();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this, App.Settings.Theme != "Light");
        BuildDeviceList();
        foreach (var d in App.Devices)
        {
            var device = d;
            device.Service.StatusChanged += () => Dispatcher.Invoke(() => OnDeviceChanged(device));
            device.Service.BatteryChanged += () => Dispatcher.Invoke(() => OnDeviceChanged(device));
        }
        App.CurrentChanged += OnCurrentChanged;
        // Keeps "5 min ago" fresh while the keyboard is wired and can't report its charge.
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        tick.Tick += (_, _) => UpdateBattery();
        tick.Start();
        UpdateStatus();
        UpdateBattery();
        Show(_page);
    }

    private void BuildDeviceList()
    {
        foreach (var d in App.Devices)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = d.Model.Name };
            name.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) });
            var state = new TextBlock { FontSize = 11 };
            state.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");
            var text = new StackPanel();
            text.Children.Add(name);
            text.Children.Add(state);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(text);
            var button = new RadioButton
            {
                Style = (Style)FindResource("NavItem"), GroupName = "device", Content = row, IsChecked = d == App.ActiveDevice, Tag = d,
            };
            AutomationProperties.SetName(button, d.Model.Name);
            button.Checked += (_, _) => App.Select(d.Model);
            DevicePanel.Children.Add(button);
            _deviceRows[d] = (button, dot, state);
            UpdateDeviceRow(d);
        }
    }

    private void UpdateDeviceRow(DeviceContext d)
    {
        var (_, dot, state) = _deviceRows[d];
        var kb = d.Service;
        state.Text = kb.IsConnected ? "USB" : kb.IsWireless ? "2.4G" : "not connected";
        dot.SetResourceReference(Shape.FillProperty, kb.IsConnected || kb.IsWireless ? "Good" : "Fg2");
    }

    private void OnDeviceChanged(DeviceContext d)
    {
        UpdateDeviceRow(d);
        if (d != App.ActiveDevice) return;
        UpdateStatus();
        UpdateBattery();
    }

    /// <summary>Another keyboard was picked: rebuild the pages for it, staying on the same page.</summary>
    private void OnCurrentChanged()
    {
        _pages.Clear();
        PageHost.Content = null;
        Show(_page);
        UpdateStatus();
        UpdateBattery();
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
        BatteryIcon.Text = pct >= 95 ? "" : ((char)(0xE850 + pct / 10)).ToString();
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
        StatusDot.SetResourceReference(Shape.FillProperty,
            kb.IsBusy ? "Accent" : kb.IsConnected || kb.IsWireless ? "Good" : "Warn");
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && IsInitialized)
            Show(tag);
    }

    private void Show(string tag)
    {
        _page = tag;
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
        App.Device.Save();
        if (App.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        ((App)Application.Current).ExitApp();
    }
}
