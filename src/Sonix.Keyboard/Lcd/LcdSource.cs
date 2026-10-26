using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Sonix.Keyboard.Lcd;

public enum OverlayPosition { TopLeft, TopCenter, TopRight, Center, BottomLeft, BottomCenter, BottomRight }

/// <summary>Every knob of the LCD editor. Defaults reproduce the stock app's output.</summary>
public sealed record LcdRenderOptions
{
    public LcdFitMode Fit { get; init; } = LcdFitMode.Fit;
    public Color Background { get; init; } = Color.Black;

    /// <summary>Extra zoom on top of the fit (1 = none) and pan in -1..1 of the overflow.</summary>
    public double Zoom { get; init; } = 1.0;
    public double PanX { get; init; }
    public double PanY { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }

    /// <summary>-1..1 adjustments; 0 = unchanged.</summary>
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Saturation { get; init; }

    /// <summary>Playback speed multiplier (2 = twice as fast).</summary>
    public double Speed { get; init; } = 1.0;
    /// <summary>When set, every frame uses this delay instead of the source timing.</summary>
    public int? FixedDelayMs { get; init; }
    public int TrimStart { get; init; }
    public int TrimEnd { get; init; }
    /// <summary>Keep every Nth frame (delays of dropped frames are folded into the kept ones).</summary>
    public int FrameStep { get; init; } = 1;

    public string? OverlayText { get; init; }
    public Color OverlayColor { get; init; } = Color.White;
    public float OverlaySize { get; init; } = 18;
    public OverlayPosition OverlayPosition { get; init; } = OverlayPosition.BottomRight;
    public bool OverlayShadow { get; init; } = true;

    public bool Dither { get; init; } = true;
}

/// <summary>Original, unscaled frames of a picture/GIF/frame folder, re-rendered on demand with <see cref="LcdRenderOptions"/>.</summary>
public sealed class LcdSource : IDisposable
{
    public List<(Bitmap Image, int DelayMs)> Frames { get; } = new();
    public int Width => Frames.Count > 0 ? Frames[0].Image.Width : 0;
    public int Height => Frames.Count > 0 ? Frames[0].Image.Height : 0;

    public static LcdSource Load(string path, int maxFrames = 1000)
    {
        var src = new LcdSource();
        var files = Directory.Exists(path) ? LcdAnimation.FrameFiles(path).ToList() : [path];
        foreach (var f in files)
        {
            using var img = Image.FromFile(f);
            int count = img.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? img.GetFrameCount(FrameDimension.Time) : 1;
            var delays = count > 1 ? ReadDelays(img, count) : [LcdAnimation.DefaultDelayMs];
            for (int i = 0; i < count && src.Frames.Count < maxFrames; i++)
            {
                if (count > 1) img.SelectActiveFrame(FrameDimension.Time, i);
                src.Frames.Add((new Bitmap(img), delays[i]));
            }
        }
        return src;
    }

    private static int[] ReadDelays(Image img, int count)
    {
        var delays = Enumerable.Repeat(LcdAnimation.DefaultDelayMs, count).ToArray();
        const int PropertyTagFrameDelay = 0x5100;
        if (!img.PropertyIdList.Contains(PropertyTagFrameDelay)) return delays;
        var raw = img.GetPropertyItem(PropertyTagFrameDelay)?.Value;
        for (int i = 0; raw is not null && i < count && (i + 1) * 4 <= raw.Length; i++)
        {
            int cs = BitConverter.ToInt32(raw, i * 4);
            delays[i] = cs <= 1 ? LcdAnimation.DefaultDelayMs : cs * 10;
        }
        return delays;
    }

    /// <summary>Indices of the source frames that survive trim + step, with their merged delays.</summary>
    public List<(int Index, int DelayMs)> SelectFrames(LcdRenderOptions o, int maxFrames)
    {
        var picked = new List<(int, int)>();
        int start = Math.Clamp(o.TrimStart, 0, Math.Max(0, Frames.Count - 1));
        int end = Math.Clamp(Frames.Count - o.TrimEnd, start + 1, Frames.Count);
        int step = Math.Max(1, o.FrameStep);
        for (int i = start; i < end && picked.Count < maxFrames; i += step)
        {
            int delay = 0;
            for (int j = i; j < Math.Min(i + step, end); j++) delay += Frames[j].DelayMs;
            if (o.FixedDelayMs is int fixedMs) delay = fixedMs;
            delay = (int)Math.Round(delay / Math.Max(0.05, o.Speed));
            picked.Add((i, Math.Clamp(delay, LcdAnimation.MinDelayMs, LcdAnimation.MaxDelayMs)));
        }
        return picked;
    }

    public LcdAnimation Render(LcdRenderOptions o, LcdGeometry g)
    {
        var anim = new LcdAnimation(g) { Dither = o.Dither };
        foreach (var (i, delay) in SelectFrames(o, g.MaxFrames))
            anim.Frames.Add(new LcdFrame(RenderFrame(i, o, g), delay));
        return anim;
    }

    public Bitmap RenderFrame(int index, LcdRenderOptions o, LcdGeometry g)
    {
        var src = Frames[index].Image;
        var dst = new Bitmap(g.Width, g.Height, PixelFormat.Format32bppArgb);
        using var gr = Graphics.FromImage(dst);
        gr.Clear(o.Background);
        gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
        gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
        gr.SmoothingMode = SmoothingMode.HighQuality;

        var rect = Place(src.Width, src.Height, o, g);
        if (o.FlipHorizontal || o.FlipVertical)
        {
            gr.TranslateTransform(o.FlipHorizontal ? g.Width : 0, o.FlipVertical ? g.Height : 0);
            gr.ScaleTransform(o.FlipHorizontal ? -1 : 1, o.FlipVertical ? -1 : 1);
        }
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // avoids dark fringes at the image edge when scaling
        if (o.Brightness != 0 || o.Contrast != 0 || o.Saturation != 0)
            attrs.SetColorMatrix(ColorAdjust(o.Brightness, o.Contrast, o.Saturation));
        PointF[] corners = [new(rect.Left, rect.Top), new(rect.Right, rect.Top), new(rect.Left, rect.Bottom)];
        gr.DrawImage(src, corners, new RectangleF(0, 0, src.Width, src.Height), GraphicsUnit.Pixel, attrs);
        gr.ResetTransform();

        if (!string.IsNullOrWhiteSpace(o.OverlayText))
            DrawOverlay(gr, o, g);
        return dst;
    }

    private static RectangleF Place(int w, int h, LcdRenderOptions o, LcdGeometry g)
    {
        double sx = (double)g.Width / w, sy = (double)g.Height / h;
        double fx, fy;
        switch (o.Fit)
        {
            case LcdFitMode.Stretch: fx = sx; fy = sy; break;
            case LcdFitMode.Fill: fx = fy = Math.Max(sx, sy); break;
            default: fx = fy = Math.Min(sx, sy); break;
        }
        double z = Math.Clamp(o.Zoom, 0.1, 10);
        double dw = w * fx * z, dh = h * fy * z;
        // Pan moves across whatever overflows the screen; centered when it fits.
        double x = (g.Width - dw) / 2 + (dw > g.Width ? -o.PanX * (dw - g.Width) / 2 : 0);
        double y = (g.Height - dh) / 2 + (dh > g.Height ? -o.PanY * (dh - g.Height) / 2 : 0);
        return new RectangleF((float)x, (float)y, (float)dw, (float)dh);
    }

    private static ColorMatrix ColorAdjust(double brightness, double contrast, double saturation)
    {
        float c = (float)(1 + contrast);            // 0..2
        float b = (float)brightness * 0.5f;          // -0.5..0.5 offset
        float t = (1 - c) / 2 + b;                   // contrast pivots around mid grey
        float s = (float)(1 + saturation);           // 0 = greyscale, 2 = vivid
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float sr = (1 - s) * lr, sg = (1 - s) * lg, sb = (1 - s) * lb;
        return new ColorMatrix(
        [
            [c * (sr + s), c * sr, c * sr, 0, 0],
            [c * sg, c * (sg + s), c * sg, 0, 0],
            [c * sb, c * sb, c * (sb + s), 0, 0],
            [0, 0, 0, 1, 0],
            [t, t, t, 0, 1],
        ]);
    }

    private static void DrawOverlay(Graphics gr, LcdRenderOptions o, LcdGeometry g)
    {
        gr.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI Semibold", o.OverlaySize, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = gr.MeasureString(o.OverlayText, font);
        const float m = 6;
        float x = o.OverlayPosition switch
        {
            OverlayPosition.TopLeft or OverlayPosition.BottomLeft => m,
            OverlayPosition.TopRight or OverlayPosition.BottomRight => g.Width - size.Width - m,
            _ => (g.Width - size.Width) / 2,
        };
        float y = o.OverlayPosition switch
        {
            OverlayPosition.TopLeft or OverlayPosition.TopCenter or OverlayPosition.TopRight => m,
            OverlayPosition.Center => (g.Height - size.Height) / 2,
            _ => g.Height - size.Height - m,
        };
        if (o.OverlayShadow)
        {
            using var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            gr.DrawString(o.OverlayText, font, shadow, x + 1.5f, y + 1.5f);
        }
        using var brush = new SolidBrush(o.OverlayColor);
        gr.DrawString(o.OverlayText, font, brush, x, y);
    }

    public void Dispose()
    {
        foreach (var (img, _) in Frames) img.Dispose();
        Frames.Clear();
    }
}
