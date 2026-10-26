using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Sonix.Keyboard;
using KeyboardStudio.Controls;
using KeyboardStudio.Services;

namespace KeyboardStudio.Pages;

public partial class LightingPage : UserControl
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private LightSettings _light;
    private bool _loading;

    public LightingPage()
    {
        InitializeComponent();
        _light = App.Device.Light;

        foreach (var mode in _modes)
        {
            var rb = new RadioButton { Content = mode.Name, Tag = mode, GroupName = "mode", Style = (Style)FindResource("Segment") };
            rb.Checked += Mode_Checked;
            ModePanel.Children.Add(rb);
        }
        BuildFirmwareCombos();
        Picker.ColorChanged += _ => Setting_Changed(this, new RoutedEventArgs());
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await ApplyAsync(); };

        LiveBox.IsChecked = App.Device.LiveApply;
        RestoreBox.IsChecked = App.Device.RestoreLightingOnConnect;
        LoadUi(_light);
        RefreshPresets();
    }

    /// <summary>Effects with this keyboard's names (pages are rebuilt when another keyboard is picked).</summary>
    private readonly IReadOnlyList<LightMode> _modes = LightMode.For(App.Model);

    private LightMode CurrentMode => _modes.FirstOrDefault(m => m.Id == _light.Mode) ?? _modes[0];

    /// <summary>The keyboard's own Fn combos for lights that have no software command (from its manual / the stock driver).</summary>
    private void BuildFirmwareCombos()
    {
        FirmwareIntro.Text = App.Model.FirmwareLightsIntro;
        for (int i = 0; i < App.Model.FirmwareCombos.Count; i++)
        {
            var (keys, action) = App.Model.FirmwareCombos[i];
            FirmwareGrid.RowDefinitions.Add(new RowDefinition());
            var kbd = new Border { Style = (Style)FindResource("Kbd"), Child = new TextBlock { Text = keys, Style = (Style)FindResource("KbdText") } };
            Grid.SetRow(kbd, i);
            FirmwareGrid.Children.Add(kbd);
            var text = new TextBlock { Text = action, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            FirmwareGrid.Children.Add(text);
        }
    }

    private void LoadUi(LightSettings s)
    {
        _loading = true;
        _light = s;
        foreach (RadioButton rb in ModePanel.Children)
            rb.IsChecked = ((LightMode)rb.Tag).Id == s.Mode;
        BrightnessSlider.Value = s.Brightness;
        SpeedSlider.Value = s.Speed;
        RainbowBox.IsChecked = s.Rainbow;
        Picker.Color = Color.FromRgb(s.R, s.G, s.B);
        BuildDirections();
        UpdateEnabled();
        _loading = false;
    }

    private void BuildDirections()
    {
        DirectionPanel.Children.Clear();
        var caps = CurrentMode.Caps;
        int count = caps.HasFlag(LightCaps.Direction4) ? 4 : caps.HasFlag(LightCaps.Direction2) ? 2 : 0;
        for (int i = 0; i < count; i++)
        {
            var rb = new RadioButton
            {
                Content = $"Way {i + 1}", Tag = i, GroupName = "dir", Style = (Style)FindResource("Segment"),
                IsChecked = _light.Direction == i, ToolTip = $"Direction value {i}; the exact sweep depends on the effect",
            };
            rb.Checked += (_, e) => Setting_Changed(rb, e);
            DirectionPanel.Children.Add(rb);
        }
        DirectionRow.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEnabled()
    {
        var caps = CurrentMode.Caps;
        BrightnessSlider.IsEnabled = caps.HasFlag(LightCaps.Brightness);
        SpeedSlider.IsEnabled = caps.HasFlag(LightCaps.Speed);
        RainbowBox.Visibility = caps.HasFlag(LightCaps.Rainbow) ? Visibility.Visible : Visibility.Collapsed;
        bool colorUsed = caps.HasFlag(LightCaps.Color) && RainbowBox.IsChecked != true;
        Picker.IsEnabled = colorUsed;
        Picker.Opacity = colorUsed ? 1 : 0.4;
        ColorNote.Text = _light.Mode == 0 ? "Backlight is off."
            : !caps.HasFlag(LightCaps.Color) ? $"{CurrentMode.Name} picks its own colors."
            : RainbowBox.IsChecked == true ? "Rainbow mode ignores the color." : "";
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: LightMode mode }) return;
        _light = _light with { Mode = mode.Id, Direction = 0 };
        _loading = true;
        BuildDirections();
        _loading = false;
        UpdateEnabled();
        Changed();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var c = Picker.Color;
        int dir = DirectionPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as int? ?? 0;
        _light = _light with
        {
            Brightness = (int)BrightnessSlider.Value,
            Speed = (int)SpeedSlider.Value,
            Rainbow = RainbowBox.IsChecked == true,
            R = c.R, G = c.G, B = c.B,
            Direction = dir,
        };
        UpdateEnabled();
        Changed();
    }

    private void Changed()
    {
        App.Device.Light = _light;
        if (LiveBox.IsChecked == true)
        {
            _debounce.Stop();
            _debounce.Start();
        }
    }

    private async Task ApplyAsync()
    {
        ApplyStatus.Text = "Applying...";
        bool ok = await App.Keyboard.SetLightingAsync(_light);
        ApplyStatus.Text = ok ? $"{CurrentMode.Name} applied." : App.Keyboard.Status;
        App.Device.Save();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyAsync();

    private void Live_Click(object sender, RoutedEventArgs e)
    {
        App.Device.LiveApply = LiveBox.IsChecked == true;
        App.Device.Save();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        App.Device.RestoreLightingOnConnect = RestoreBox.IsChecked == true;
        App.Device.Save();
    }

    // ------------------------------------------------------------------ presets

    private void RefreshPresets()
    {
        PresetPanel.Children.Clear();
        foreach (var p in App.Device.LightPresets)
        {
            var dot = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(p.Light.R, p.Light.G, p.Light.B)),
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(dot);
            content.Children.Add(new TextBlock { Text = p.Name });
            var btn = new Button { Content = content, Margin = new Thickness(0, 0, 8, 8), ToolTip = "Click to apply, right-click to delete" };
            var preset = p;
            btn.Click += async (_, _) => { LoadUi(preset.Light); App.Device.Light = preset.Light; await ApplyAsync(); };
            var menu = new ContextMenu();
            var del = new MenuItem { Header = "Delete preset" };
            del.Click += (_, _) => { App.Device.LightPresets.Remove(preset); App.Device.Save(); RefreshPresets(); };
            menu.Items.Add(del);
            btn.ContextMenu = menu;
            PresetPanel.Children.Add(btn);
        }
        NoPresets.Visibility = App.Device.LightPresets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt.Ask(Window.GetWindow(this), "Preset name", $"{CurrentMode.Name} #{App.Device.LightPresets.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        App.Device.LightPresets.Add(new LightPreset(name.Trim(), _light));
        App.Device.Save();
        RefreshPresets();
    }
}
