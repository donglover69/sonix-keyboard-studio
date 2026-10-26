using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KeyboardStudio.Controls;

public partial class ColorPicker : UserControl
{
    private static readonly string[] SwatchColors =
        ["#FFFFFF", "#FF3B3B", "#FF8A00", "#FFD400", "#3BFF6B", "#00E5FF", "#2F6BFF", "#A625F3", "#FF4FD8", "#000000"];

    private double _h, _s = 1, _v = 1;
    private bool _dragging, _updating;

    /// <summary>Raised whenever the user changes the color (dragging raises it continuously).</summary>
    public event Action<Color>? ColorChanged;

    public ColorPicker()
    {
        InitializeComponent();
        foreach (var hex in SwatchColors)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var sw = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(c), Cursor = Cursors.Hand, ToolTip = hex,
                BorderThickness = new Thickness(1),
            };
            sw.SetResourceReference(Border.BorderBrushProperty, "Line");
            sw.MouseLeftButtonUp += (_, _) => { Color = c; ColorChanged?.Invoke(c); };
            Swatches.Children.Add(sw);
        }
        SvArea.SizeChanged += (_, _) => UpdateVisuals();
        Color = Colors.White;
    }

    public Color Color
    {
        get => FromHsv(_h, _s, _v);
        set
        {
            ToHsv(value, out var h, out _s, out _v);
            // Keep the hue for greys so the square doesn't jump to red.
            if (_s > 0.001 && _v > 0.001) _h = h;
            UpdateVisuals();
        }
    }

    private void UpdateVisuals()
    {
        _updating = true;
        HueSlider.Value = _h;
        HueFill.Background = new SolidColorBrush(FromHsv(_h, 1, 1));
        var c = Color;
        Preview.Background = new SolidColorBrush(c);
        HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        Canvas.SetLeft(SvThumb, _s * SvArea.ActualWidth - 7);
        Canvas.SetTop(SvThumb, (1 - _v) * SvArea.ActualHeight - 7);
        _updating = false;
    }

    private void Hue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _h = e.NewValue;
        UpdateVisuals();
        ColorChanged?.Invoke(Color);
    }

    private void Sv_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        SvArea.CaptureMouse();
        PickSv(e.GetPosition(SvArea));
    }

    private void Sv_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging) PickSv(e.GetPosition(SvArea));
    }

    private void Sv_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        SvArea.ReleaseMouseCapture();
    }

    private void PickSv(Point p)
    {
        _s = Math.Clamp(p.X / SvArea.ActualWidth, 0, 1);
        _v = Math.Clamp(1 - p.Y / SvArea.ActualHeight, 0, 1);
        UpdateVisuals();
        ColorChanged?.Invoke(Color);
    }

    private void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Hex_Commit(sender, e);
    }

    private void Hex_Commit(object sender, RoutedEventArgs e)
    {
        var text = HexBox.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(text);
            Color = Color.FromRgb(c.R, c.G, c.B);
            ColorChanged?.Invoke(Color);
        }
        catch (FormatException)
        {
            UpdateVisuals();
        }
    }

    public static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static void ToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        s = max == 0 ? 0 : d / max;
        v = max;
    }
}
