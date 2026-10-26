using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KeyboardStudio.Services;

/// <summary>Swaps the color dictionary and accent brushes at runtime; all XAML uses DynamicResource.</summary>
public static class ThemeManager
{
    public static readonly string[] AccentPresets =
        ["#B48CFF", "#FF7AB6", "#5CC8FF", "#5AD18A", "#F0B54A", "#FF6B6B", "#7AE7E0", "#E8E8F0"];

    public static void Apply(AppSettings s)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var colors = new ResourceDictionary
        {
            Source = new Uri(s.Theme == "Light" ? "Themes/Colors.Light.xaml" : "Themes/Colors.Dark.xaml", UriKind.Relative),
        };
        dicts[0] = colors;

        var accent = ParseColor(s.Accent, s.Theme == "Light" ? Color.FromRgb(0x7A, 0x4C, 0xE0) : Color.FromRgb(0xB4, 0x8C, 0xFF));
        SetBrush("Accent", accent);
        SetBrush("AccentSoft", Color.FromArgb(0x33, accent.R, accent.G, accent.B));
        // Pick readable text on the accent: dark text on light accents, white on dark ones.
        double luminance = (0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B) / 255;
        SetBrush("AccentFg", luminance > 0.55 ? Color.FromRgb(0x0E, 0x0F, 0x13) : Colors.White);

        foreach (Window w in Application.Current.Windows)
            ApplyTitleBar(w, s.Theme != "Light");
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex ?? ""); }
        catch (FormatException) { return fallback; }
    }

    private static void SetBrush(string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        Application.Current.Resources[key] = b;
    }

    /// <summary>Dark (or light) native title bar on Windows 10 20H1+ / 11.</summary>
    public static void ApplyTitleBar(Window w, bool dark)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        if (Application.Current.Resources["Bg1"] is SolidColorBrush bg)
        {
            int colorRef = bg.Color.R | bg.Color.G << 8 | bg.Color.B << 16;
            DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR, Windows 11 */, ref colorRef, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
