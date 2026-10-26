using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace KeyboardStudio.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action show, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Keyboard Studio", null, (_, _) => show());
        foreach (var d in App.Devices)
        {
            var device = d;
            menu.Items.Add($"Sync {d.Model.ShortName} screen clock", null, async (_, _) => await device.Service.SyncClockAsync());
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        menu.Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColors());
        menu.ForeColor = Color.FromArgb(0xEC, 0xEE, 0xF3);

        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon.CreateIcon(32),
            Text = "Keyboard Studio",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
    }

    /// <summary>Tray tooltip text (Windows caps it at 127 characters).</summary>
    public void SetText(string text) => _icon.Text = text.Length > 127 ? text[..127] : text;

    public void Notify(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(8000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(0x1C, 0x1F, 0x26);
        public override Color ImageMarginGradientBegin => Color.FromArgb(0x1C, 0x1F, 0x26);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(0x1C, 0x1F, 0x26);
        public override Color ImageMarginGradientEnd => Color.FromArgb(0x1C, 0x1F, 0x26);
        public override Color MenuItemSelected => Color.FromArgb(0x33, 0xB4, 0x8C, 0xFF);
        public override Color MenuItemBorder => Color.FromArgb(0xB4, 0x8C, 0xFF);
        public override Color MenuBorder => Color.FromArgb(0x2F, 0x34, 0x40);
        public override Color SeparatorDark => Color.FromArgb(0x2F, 0x34, 0x40);
    }
}

/// <summary>App icon drawn in code: a little keyboard with a glowing screen.</summary>
public static class AppIcon
{
    public static Bitmap Draw(int size)
    {
        var accent = ThemeManager.ParseColor(App.Settings.Accent, System.Windows.Media.Color.FromRgb(0xB4, 0x8C, 0xFF));
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = size / 32f;
        using var body = new SolidBrush(Color.FromArgb(0x26, 0x2A, 0x33));
        using var screen = new SolidBrush(Color.FromArgb(accent.R, accent.G, accent.B));
        using var key = new SolidBrush(Color.FromArgb(0xA9, 0xAF, 0xBC));
        g.FillPath(body, Rounded(new RectangleF(1 * s, 7 * s, 30 * s, 19 * s), 4 * s));
        g.FillRectangle(screen, 20 * s, 10 * s, 8 * s, 5 * s);
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 4; c++)
                g.FillRectangle(key, (4 + c * 4) * s, (10 + r * 4) * s, 3 * s, 3 * s);
        g.FillRectangle(key, 4 * s, 19 * s, 24 * s, 3 * s);
        return bmp;
    }

    public static Icon CreateIcon(int size)
    {
        using var bmp = Draw(size);
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    public static BitmapSource CreateImageSource(int size)
    {
        using var bmp = Draw(size);
        var h = bmp.GetHbitmap();
        try
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(h);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    private static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
        p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
        p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
        p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
        p.CloseFigure();
        return p;
    }
}

public static class Autostart
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "KeyboardStudio";

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enabled)
            key.SetValue(Name, $"\"{Environment.ProcessPath}\" --minimized");
        else
            key.DeleteValue(Name, throwOnMissingValue: false);
    }

    /// <summary>
    /// On the first start after AK980 Studio or F108 Studio: start this app with Windows instead of those, so two apps
    /// never drive the same keyboard at login.
    /// </summary>
    public static void ReplaceLegacy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        foreach (var old in Sonix.Keyboard.KeyboardModel.All.Select(m => m.LegacyAppFolder))
            key.DeleteValue(old, throwOnMissingValue: false);
        key.SetValue(Name, $"\"{Environment.ProcessPath}\" --minimized");
    }
}
