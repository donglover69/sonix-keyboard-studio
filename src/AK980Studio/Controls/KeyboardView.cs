using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ajazz.Keyboard.Layout;

namespace AK980Studio.Controls;

/// <summary>
/// The physical key layout drawn on a canvas (scaled with a Viewbox). Labels shrink to fit narrow keys instead of being cut off.
/// Used by the Keys page; each key can show a second line (its remapped function).
/// </summary>
public sealed class KeyboardView : UserControl
{
    private sealed class KeyVisual
    {
        public required LayoutKey Key { get; init; }
        public required Border Box { get; init; }
        public required TextBlock Name { get; init; }
        public required TextBlock Detail { get; init; }
    }

    /// <summary>Plain template so each key is a real Button (keyboard focus, screen readers, UI Automation invoke).</summary>
    private static readonly ControlTemplate KeyTemplate = BuildKeyTemplate();

    private static ControlTemplate BuildKeyTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return t;
    }

    private readonly Canvas _canvas;
    private readonly Dictionary<int, KeyVisual> _keys = new();
    private int? _selected;

    public event Action<LayoutKey>? KeyClicked;

    public KeyboardLayout Layout { get; } = KeyboardLayout.Load();

    public KeyboardView()
    {
        // Crop to the keys so the board renders as large as possible.
        const int pad = 6;
        int minX = Layout.Keys.Min(k => k.X) - pad, minY = Layout.Keys.Min(k => k.Y) - pad;
        int maxX = Layout.Keys.Max(k => k.X + k.W) + pad, maxY = Layout.Keys.Max(k => k.Y + k.H) + pad;
        _canvas = new Canvas { Width = maxX - minX, Height = maxY - minY, Background = Brushes.Transparent };
        foreach (var k in Layout.Keys)
        {
            var name = new TextBlock { Text = k.Name, FontSize = 9.5, HorizontalAlignment = HorizontalAlignment.Center };
            var detail = new TextBlock { FontSize = 8.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(name);
            stack.Children.Add(detail);
            var box = new Border
            {
                Width = k.W - 2, Height = k.H - 2, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                Child = new Viewbox { Child = stack, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(2, 1, 2, 1) },
                Cursor = Cursors.Hand, ToolTip = $"{k.Name}  (key {k.Index})",
            };
            box.SetResourceReference(Border.BackgroundProperty, "Bg3");
            box.SetResourceReference(Border.BorderBrushProperty, "Line");
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg0");
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var button = new Button { Template = KeyTemplate, Content = box, Focusable = false };
            System.Windows.Automation.AutomationProperties.SetName(button, k.Name);
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, $"key-{k.Index}");
            button.Click += (_, _) => KeyClicked?.Invoke(k);
            Canvas.SetLeft(button, k.X - minX + 1);
            Canvas.SetTop(button, k.Y - minY + 1);
            _canvas.Children.Add(button);
            _keys[k.Index] = new KeyVisual { Key = k, Box = box, Name = name, Detail = detail };
        }
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas, MaxHeight = 380 };
    }

    /// <summary>Keys narrower than this show a dot instead of the remapped function (the tooltip and list have the full text).</summary>
    private const int DetailMinWidth = 44;

    /// <summary>Shows <paramref name="detail"/> under the key name and highlights the key, or resets it when null.</summary>
    public void SetDetail(int index, string? detail, string? tooltip = null)
    {
        if (!_keys.TryGetValue(index, out var v)) return;
        v.Detail.Text = detail is null ? "" : v.Key.W >= DetailMinWidth ? detail : "●";
        v.Detail.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        v.Box.SetResourceReference(Border.BackgroundProperty, detail is null ? "Bg3" : "AccentSoft");
        v.Box.ToolTip = tooltip ?? $"{v.Key.Name}  (key {v.Key.Index})";
        UpdateBorder(v);
    }

    public void ClearDetails()
    {
        foreach (var index in _keys.Keys) SetDetail(index, null);
    }

    public int? Selected
    {
        get => _selected;
        set
        {
            var old = _selected;
            _selected = value;
            if (old is int o && _keys.TryGetValue(o, out var a)) UpdateBorder(a);
            if (value is int n && _keys.TryGetValue(n, out var b)) UpdateBorder(b);
        }
    }

    private void UpdateBorder(KeyVisual v)
    {
        bool sel = _selected == v.Key.Index;
        v.Box.BorderThickness = new Thickness(sel ? 2 : 1);
        v.Box.SetResourceReference(Border.BorderBrushProperty, sel || v.Detail.Visibility == Visibility.Visible ? "Accent" : "Line");
    }
}
