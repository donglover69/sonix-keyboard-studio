using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyboardStudio.Services;

namespace KeyboardStudio.Controls;

/// <summary>Minimal themed "type a name" dialog.</summary>
public static class Prompt
{
    public static string? Ask(Window? owner, string title, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 280 };
        var ok = new Button { Content = "OK", IsDefault = true, Style = (Style)Application.Current.FindResource("Primary"), Margin = new Thickness(8, 0, 0, 0), MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var w = new Window
        {
            Title = "Keyboard Studio", Content = panel, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        w.SetResourceReference(Window.BackgroundProperty, "Bg1");
        w.SetResourceReference(Window.ForegroundProperty, "Fg0");
        w.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont");
        w.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(w, App.Settings.Theme != "Light");
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) w.DialogResult = true; };
        return w.ShowDialog() == true ? box.Text : null;
    }
}
