using System.Windows;
using System.Windows.Controls;

namespace KeyboardStudio.Controls;

/// <summary>"Label ——o—— value" row used throughout the editor.</summary>
public sealed class LabeledSlider : UserControl
{
    private readonly TextBlock _label = new() { Width = 92, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _value = new() { Width = 62, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _slider = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
    private bool _silent;

    /// <summary>Raised when the user moves the slider (not when the value is set from code).</summary>
    public event RoutedEventHandler? ValueChanged;

    public LabeledSlider()
    {
        _label.SetResourceReference(TextBlock.ForegroundProperty, "Fg1");
        _value.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");
        _value.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _value.FontSize = 12;
        var dock = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(_label, Dock.Left);
        DockPanel.SetDock(_value, Dock.Right);
        dock.Children.Add(_label);
        dock.Children.Add(_value);
        dock.Children.Add(_slider);
        Content = dock;
        _slider.ValueChanged += (_, e) =>
        {
            if (Integer && Math.Abs(e.NewValue - Math.Round(e.NewValue)) > 1e-9)
            {
                _slider.Value = Math.Round(e.NewValue);
                return;
            }
            UpdateText();
            // Before Loaded, value changes come from XAML/coercion (e.g. Minimum clamping), never from the user.
            if (!_silent && IsLoaded) ValueChanged?.Invoke(this, new RoutedEventArgs());
        };
        UpdateText();
    }

    public string Label { get => (string)_label.Text; set => _label.Text = value; }
    private string _format = "{0:0.##}";

    public string Format
    {
        get => _format;
        set { _format = value; UpdateText(); }
    }

    public bool Integer
    {
        get => _slider.IsSnapToTickEnabled;
        set { _slider.IsSnapToTickEnabled = value; _slider.TickFrequency = 1; }
    }

    public double Minimum { get => _slider.Minimum; set => _slider.Minimum = value; }
    public double Maximum { get => _slider.Maximum; set => _slider.Maximum = value; }

    public double Value
    {
        get => _slider.Value;
        set { _silent = true; _slider.Value = value; _silent = false; UpdateText(); }
    }

    private void UpdateText() => _value.Text = string.Format(_format, _slider.Value);
}
