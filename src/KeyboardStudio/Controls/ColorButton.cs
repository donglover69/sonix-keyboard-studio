using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KeyboardStudio.Controls;

/// <summary>A swatch that opens a <see cref="ColorPicker"/> popup.</summary>
public sealed class ColorButton : UserControl
{
    private readonly Border _swatch;
    private readonly TextBlock _hex = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly ColorPicker _picker = new() { Width = 220 };
    private readonly Popup _popup;
    private Color _color = Colors.Black;

    public event RoutedEventHandler? ColorChanged;

    public ColorButton()
    {
        _swatch = new Border { Width = 30, Height = 22, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "Line");
        _hex.SetResourceReference(TextBlock.ForegroundProperty, "Fg1");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        row.Children.Add(_swatch);
        row.Children.Add(_hex);

        var frame = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = _picker };
        frame.SetResourceReference(Border.BackgroundProperty, "Bg2");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        _popup = new Popup { Child = frame, PlacementTarget = row, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };

        row.MouseLeftButtonUp += (_, _) => { _picker.Color = _color; _popup.IsOpen = true; };
        _picker.ColorChanged += c =>
        {
            SetColor(c);
            ColorChanged?.Invoke(this, new RoutedEventArgs());
        };

        var host = new Grid();
        host.Children.Add(row);
        host.Children.Add(_popup);
        Content = host;
        SetColor(_color);
    }

    public Color Color
    {
        get => _color;
        set => SetColor(value);
    }

    private void SetColor(Color c)
    {
        _color = c;
        _swatch.Background = new SolidColorBrush(c);
        _hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
