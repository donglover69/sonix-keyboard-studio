using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyboardStudio.Controls;
using KeyboardStudio.Services;
using Sonix.Keyboard.Layout;

namespace KeyboardStudio.Pages;

public partial class PerKeyPage : UserControl
{
    private static readonly Dictionary<string, string> ShortNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = "Bksp", ["page up"] = "PgUp", ["page down"] = "PgDn", ["home"] = "Home", ["del"] = "Del",
        ["Ctrl_R"] = "Ctrl", ["Ctrl_L"] = "Ctrl", ["Shift_L"] = "Shift", ["Shift_R"] = "Shift", ["Alt_L"] = "Alt",
        ["Alt_R"] = "Alt", ["Win_L"] = "Win", ["Caps Lock"] = "Caps", ["Num Lock"] = "Num", ["Enter_R"] = "Ent",
    };

    private readonly List<(LayoutKey Def, Border View, TextBlock Label)> _keys = new();
    private readonly Dictionary<int, Color> _colors = new();
    private bool _painting;

    public PerKeyPage()
    {
        InitializeComponent();
        Brush.Color = Color.FromRgb(0xA6, 0x25, 0xF3);
        SecondColor.Color = Color.FromRgb(0x00, 0xE5, 0xFF);
        BrightnessSlider.Value = App.Device.PerKeyBrightness;
        foreach (var (led, hex) in App.Device.PerKeyColors)
            _colors[led] = ThemeManager.ParseColor(hex, Colors.Black);
        BuildKeys();
    }

    private void BuildKeys()
    {
        var layout = KeyboardLayout.Load(App.Model);
        foreach (var k in layout.Keys)
        {
            var label = new TextBlock
            {
                Text = ShortNames.TryGetValue(k.Name, out var s) ? s : k.Name,
                FontSize = 9.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var view = new Border
            {
                Width = k.W - 2, Height = k.H - 2, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                // Shrinks long labels (Pause, Home, PgUp...) to fit narrow keys instead of cutting them off.
                Child = new Viewbox { Child = label, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(2, 1, 2, 1) },
                ToolTip = $"{k.Name}  (LED {k.Led})", Cursor = Cursors.Pen,
            };
            view.SetResourceReference(Border.BorderBrushProperty, "Line");
            Canvas.SetLeft(view, k.X + 1);
            Canvas.SetTop(view, k.Y + 1);
            KeyCanvas.Children.Add(view);
            _keys.Add((k, view, label));
            if (!_colors.ContainsKey(k.Led)) _colors[k.Led] = Color.FromRgb(0xA6, 0x25, 0xF3);
        }
        Redraw();
    }

    private void Redraw()
    {
        foreach (var (def, view, label) in _keys)
        {
            var c = _colors[def.Led];
            view.Background = new SolidColorBrush(c);
            double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
            label.Foreground = lum > 0.5 ? Brushes.Black : Brushes.White;
        }
    }

    private LayoutKey? HitTest(Point p) =>
        _keys.Select(k => k.Def).FirstOrDefault(k => p.X >= k.X && p.X < k.X + k.W && p.Y >= k.Y && p.Y < k.Y + k.H);

    private void PaintAt(Point p)
    {
        var key = HitTest(p);
        if (key is null) return;
        if (Eyedropper.IsChecked == true)
        {
            Brush.Color = _colors[key.Led];
            Eyedropper.IsChecked = false;
            _painting = false;
            return;
        }
        if (_colors[key.Led] == Brush.Color) return;
        _colors[key.Led] = Brush.Color;
        Redraw();
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _painting = true;
        KeyCanvas.CaptureMouse();
        PaintAt(e.GetPosition(KeyCanvas));
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_painting) PaintAt(e.GetPosition(KeyCanvas));
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _painting = false;
        KeyCanvas.ReleaseMouseCapture();
        Persist();
    }

    private void Fill(Func<LayoutKey, Color> f)
    {
        foreach (var (def, _, _) in _keys) _colors[def.Led] = f(def);
        Redraw();
        Persist();
    }

    private void FillAll_Click(object sender, RoutedEventArgs e) => Fill(_ => Brush.Color);

    private void Clear_Click(object sender, RoutedEventArgs e) => Fill(_ => Colors.Black);

    private void RainbowX_Click(object sender, RoutedEventArgs e) =>
        Fill(k => ColorPicker.FromHsv((k.X + k.W / 2.0) / 800.0 * 330, 1, 1));

    private void RainbowY_Click(object sender, RoutedEventArgs e) =>
        Fill(k => ColorPicker.FromHsv((k.Y - 40) / 240.0 * 300, 1, 1));

    private void Gradient_Click(object sender, RoutedEventArgs e)
    {
        Color a = Brush.Color, b = SecondColor.Color;
        Fill(k =>
        {
            double t = Math.Clamp((k.X + k.W / 2.0 - 24) / 752.0, 0, 1);
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        });
    }

    private void Gaming_Click(object sender, RoutedEventArgs e)
    {
        // By HID code (layout names differ between keyboards): W A S D, arrows, Space, left Shift, left Ctrl, Esc.
        var hot = new HashSet<int> { 0x1A, 0x04, 0x16, 0x07, 0x52, 0x51, 0x50, 0x4F, 0x2C, 0xE1, 0xE0, 0x29 };
        Fill(k => hot.Contains(k.Code) ? Brush.Color : SecondColor.Color);
    }

    private void Persist()
    {
        App.Device.PerKeyColors = _colors.ToDictionary(kv => kv.Key, kv => $"#{kv.Value.R:X2}{kv.Value.G:X2}{kv.Value.B:X2}");
        App.Device.PerKeyBrightness = (int)BrightnessSlider.Value;
        App.Device.Save();
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        Persist();
        Status.Text = "Applying...";
        var map = _colors.ToDictionary(kv => kv.Key, kv => (kv.Value.R, kv.Value.G, kv.Value.B));
        bool ok = await App.Keyboard.SetCustomColorsAsync(map, (int)BrightnessSlider.Value);
        Status.Text = ok ? "Per-key colors are on the keyboard." : App.Keyboard.Status;
    }
}
