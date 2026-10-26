using System.Windows;
using System.Windows.Controls;
using Sonix.Keyboard;

namespace KeyboardStudio.Pages;

public partial class KeyboardPage : UserControl
{
    private static readonly string[] SleepNames = ["No sleep", "1 min", "5 min", "30 min"];

    // From the stock language file (strings 600..624): wired / 2.4 GHz / Bluetooth latency per level.
    private static readonly string[] ResponseInfoText =
    [
        "Level 1: wired ~2-3 ms, 2.4 GHz ~5-6 ms, Bluetooth ~12-13 ms. Fastest; may chatter on some switches.",
        "Level 2: wired ~5-6 ms, 2.4 GHz ~7-9 ms, Bluetooth ~15-16 ms.",
        "Level 3: wired ~8-9 ms, 2.4 GHz ~10-12 ms, Bluetooth ~18-19 ms.",
        "Level 4: wired ~13-14 ms, 2.4 GHz ~15-17 ms, Bluetooth ~23-24 ms.",
        "Level 5: wired ~17-18 ms, 2.4 GHz ~19-21 ms, Bluetooth ~27-28 ms. Use this if keys double-type.",
    ];

    public KeyboardPage()
    {
        InitializeComponent();
        var o = App.Device.KeyboardOptions;
        NoWin.IsChecked = o.DisableWinKey;
        NoAltTab.IsChecked = o.DisableAltTab;
        NoAltF4.IsChecked = o.DisableAltF4;
        FnSwitch.IsChecked = o.FnSwitch;

        for (int i = 0; i < SleepNames.Length; i++)
            SleepPanel.Children.Add(new RadioButton { Content = SleepNames[i], Tag = i, GroupName = "sleep", Style = (Style)FindResource("Segment"), IsChecked = o.SleepTime == i });

        for (int level = 1; level <= 5; level++)
        {
            var rb = new RadioButton { Content = $"Level {level}", Tag = level, GroupName = "resp", Style = (Style)FindResource("Segment"), IsChecked = o.KeyResponse == level };
            rb.Checked += (_, _) => ResponseInfo.Text = ResponseInfoText[(int)rb.Tag - 1];
            ResponsePanel.Children.Add(rb);
        }
        ResponseInfo.Text = ResponseInfoText[Math.Clamp(o.KeyResponse, 1, 5) - 1];
    }

    private static int Selected(Panel p, int fallback) =>
        p.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as int? ?? fallback;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var o = new KeyboardOptions
        {
            DisableWinKey = NoWin.IsChecked == true,
            DisableAltTab = NoAltTab.IsChecked == true,
            DisableAltF4 = NoAltF4.IsChecked == true,
            FnSwitch = FnSwitch.IsChecked == true,
            SleepTime = Selected(SleepPanel, 2),
            KeyResponse = Selected(ResponsePanel, 2),
        };
        App.Device.KeyboardOptions = o;
        App.Device.Save();
        Status.Text = "Saving...";
        Status.Text = await App.Keyboard.SetOptionsAsync(o) ? "Saved to the keyboard." : App.Keyboard.Status;
    }
}
