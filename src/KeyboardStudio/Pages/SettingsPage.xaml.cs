using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyboardStudio.Services;

namespace KeyboardStudio.Pages;

public partial class SettingsPage : UserControl
{
    private readonly bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        _loading = true;
        var s = App.Settings;
        ThemeDark.IsChecked = s.Theme != "Light";
        ThemeLight.IsChecked = s.Theme == "Light";
        foreach (var hex in ThemeManager.AccentPresets)
        {
            var c = ThemeManager.ParseColor(hex, Colors.White);
            var dot = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 8, 8),
                Background = new SolidColorBrush(c), Cursor = Cursors.Hand, ToolTip = hex,
            };
            dot.MouseLeftButtonUp += (_, _) => SetAccent(hex);
            AccentPanel.Children.Insert(AccentPanel.Children.Count - 1, dot);
        }
        AccentCustom.Color = ThemeManager.ParseColor(s.Accent, Colors.MediumPurple);
        ScaleSlider.Value = s.UiScale;
        Autostart.IsChecked = s.StartWithWindows;
        StartMin.IsChecked = s.StartMinimized;
        CloseTray.IsChecked = s.CloseToTray;
        DelaySlider.Value = s.CommandDelayMs;
        BatteryWarnBox.IsChecked = s.BatteryWarn;
        BatteryWarnSlider.Value = s.BatteryWarnPercent;
        BatteryPollSlider.Value = s.BatteryPollSeconds;
        AboutText.Text = $"Keyboard Studio {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)} · open replacement for the {string.Join(" and ", Sonix.Keyboard.KeyboardModel.All.Select(m => m.Name))} drivers, " +
                         "built from a reverse-engineered protocol. Settings live in " + AppSettings.Folder;
        _loading = false;
    }

    private void SetAccent(string hex)
    {
        App.Settings.Accent = hex;
        AccentCustom.Color = ThemeManager.ParseColor(hex, Colors.MediumPurple);
        ThemeManager.Apply(App.Settings);
        App.Settings.Save();
    }

    private void AccentCustom_Changed(object sender, RoutedEventArgs e)
    {
        var c = AccentCustom.Color;
        SetAccent($"#{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.Theme = ThemeLight.IsChecked == true ? "Light" : "Dark";
        ThemeManager.Apply(App.Settings);
        App.Settings.Save();
    }

    private void Scale_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.UiScale = Math.Round(ScaleSlider.Value, 2);
        (Window.GetWindow(this) as MainWindow)?.ApplyScale();
        App.Settings.Save();
    }

    private void Behaviour_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.StartWithWindows = Autostart.IsChecked == true;
        s.StartMinimized = StartMin.IsChecked == true;
        s.CloseToTray = CloseTray.IsChecked == true;
        Services.Autostart.Set(s.StartWithWindows);
        s.Save();
    }

    private void Battery_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.BatteryWarn = BatteryWarnBox.IsChecked == true;
        App.Settings.BatteryWarnPercent = (int)BatteryWarnSlider.Value;
        App.Settings.BatteryPollSeconds = (int)BatteryPollSlider.Value;
        App.Settings.Save();
    }

    private void Delay_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.CommandDelayMs = (int)DelaySlider.Value;
        App.Settings.Save();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppSettings.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", AppSettings.Folder) { UseShellExecute = true });
    }
}
