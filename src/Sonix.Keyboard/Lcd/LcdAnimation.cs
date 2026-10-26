using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Sonix.Keyboard.Lcd;

public enum LcdFitMode
{
    /// <summary>Scale to fit inside the screen, keep aspect ratio, pad with the background color (what the stock app does).</summary>
    Fit,
    /// <summary>Scale to cover the whole screen, keep aspect ratio, crop the overflow.</summary>
    Fill,
    /// <summary>Stretch to exactly the screen size.</summary>
    Stretch,
}

/// <summary>One LCD frame: a full-screen bitmap plus how long it stays up.</summary>
public sealed class LcdFrame(Bitmap image, int delayMs) : IDisposable
{
    public Bitmap Image { get; } = image;
    public int DelayMs { get; set; } = delayMs;
    public void Dispose() => Image.Dispose();
}

/// <summary>
/// An animation sized for the keyboard's TFT, plus the encoder for the upload blob.
/// Blob layout (reverse engineered from DeviceDriver.exe 0x422960):
///   [0]            frame count
///   [1..count]     per-frame delay in 2 ms units (1..255)
///   [header..]     frames, RGB565 little-endian, top-down rows
///   padding 0xFF   up to a multiple of 4096 bytes (one page per HID output report)
/// </summary>
public sealed class LcdAnimation : IDisposable
{
    public const int DefaultDelayMs = 100;
    public const int MinDelayMs = 2;
    public const int MaxDelayMs = 510;

    public LcdGeometry Geometry { get; }
    public List<LcdFrame> Frames { get; } = new();

    public LcdAnimation(LcdGeometry geometry) => Geometry = geometry;

    /// <summary>Loads a GIF (all frames) or a still image (one frame) and scales it to the screen.</summary>
    public static LcdAnimation FromFile(string path, LcdGeometry geometry, LcdFitMode fit = LcdFitMode.Fit, Color? background = null)
    {
        var anim = new LcdAnimation(geometry);
        anim.AppendFile(path, fit, background);
        return anim;
    }

    /// <summary>Loads a GIF/still image, or a folder of numbered frames (0.png, 1.png, ... like the stock app saves).</summary>
    public static LcdAnimation FromPath(string path, LcdGeometry geometry, LcdFitMode fit = LcdFitMode.Fit, Color? background = null)
    {
        if (!Directory.Exists(path)) return FromFile(path, geometry, fit, background);
        var anim = new LcdAnimation(geometry);
        foreach (var file in FrameFiles(path))
            anim.AppendFile(file, fit, background);
        return anim;
    }

    /// <summary>Image files in a folder, ordered by their numeric name when they have one.</summary>
    public static IEnumerable<string> FrameFiles(string dir) =>
        Directory.EnumerateFiles(dir)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
            .OrderBy(f => long.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : long.MaxValue)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);

    public void AppendFile(string path, LcdFitMode fit = LcdFitMode.Fit, Color? background = null)
    {
        using var src = Image.FromFile(path);
        var bg = background ?? Color.Black;
        int count = 1;
        int[] delays = [DefaultDelayMs];
        if (src.FrameDimensionsList.Contains(FrameDimension.Time.Guid))
        {
            count = src.GetFrameCount(FrameDimension.Time);
            delays = ReadGifDelays(src, count);
        }

        for (int i = 0; i < count && Frames.Count < Geometry.MaxFrames; i++)
        {
            if (count > 1) src.SelectActiveFrame(FrameDimension.Time, i);
            Frames.Add(new LcdFrame(Render(src, fit, bg), delays[i]));
        }
    }

    public Bitmap Render(Image src, LcdFitMode fit, Color background)
    {
        var dst = new Bitmap(Geometry.Width, Geometry.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(background);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.DrawImage(src, PlaceImage(src.Width, src.Height, fit));
        return dst;
    }

    private Rectangle PlaceImage(int w, int h, LcdFitMode fit)
    {
        if (fit == LcdFitMode.Stretch || w <= 0 || h <= 0)
            return new Rectangle(0, 0, Geometry.Width, Geometry.Height);
        double sx = (double)Geometry.Width / w, sy = (double)Geometry.Height / h;
        double s = fit == LcdFitMode.Fit ? Math.Min(sx, sy) : Math.Max(sx, sy);
        int dw = (int)Math.Round(w * s), dh = (int)Math.Round(h * s);
        return new Rectangle((Geometry.Width - dw) / 2, (Geometry.Height - dh) / 2, dw, dh);
    }

    private static int[] ReadGifDelays(Image img, int count)
    {
        var delays = Enumerable.Repeat(DefaultDelayMs, count).ToArray();
        const int PropertyTagFrameDelay = 0x5100;
        if (!img.PropertyIdList.Contains(PropertyTagFrameDelay)) return delays;
        var raw = img.GetPropertyItem(PropertyTagFrameDelay)?.Value;
        if (raw is null) return delays;
        for (int i = 0; i < count && (i + 1) * 4 <= raw.Length; i++)
        {
            int centiseconds = BitConverter.ToInt32(raw, i * 4);
            // Browsers treat 0/1 cs as "as fast as sensible"; mirror that instead of a 0 ms frame.
            delays[i] = centiseconds <= 1 ? DefaultDelayMs : centiseconds * 10;
        }
        return delays;
    }

    /// <summary>Encodes the upload blob (header + RGB565 frames, padded to whole pages).</summary>
    public byte[] Encode()
    {
        if (Frames.Count == 0) throw new InvalidOperationException("Animation has no frames");
        int frames = Math.Min(Frames.Count, Geometry.MaxFrames);
        int frameBytes = Geometry.Width * Geometry.Height * 2;
        int payload = Geometry.HeaderLength + frames * frameBytes;
        int pages = (payload + LcdGeometry.PageSize - 1) / LcdGeometry.PageSize;
        var blob = new byte[pages * LcdGeometry.PageSize];
        Array.Fill(blob, (byte)0xFF);

        blob[0] = (byte)frames;
        for (int i = 0; i < frames; i++)
            blob[1 + i] = (byte)Math.Clamp(Frames[i].DelayMs / 2, 1, 255);

        for (int i = 0; i < frames; i++)
            WriteRgb565(Frames[i].Image, blob.AsSpan(Geometry.HeaderLength + i * frameBytes, frameBytes));
        return blob;
    }

    /// <summary>Upload size in 4 KiB pages for the current frame count.</summary>
    public int PageCount
    {
        get
        {
            int payload = Geometry.HeaderLength + Math.Min(Frames.Count, Geometry.MaxFrames) * Geometry.Width * Geometry.Height * 2;
            return (payload + LcdGeometry.PageSize - 1) / LcdGeometry.PageSize;
        }
    }

    /// <summary>What the panel will actually show for one frame: RGB565 (dithered if enabled) expanded back to BGRA32.</summary>
    public byte[] DevicePreviewBgra(int index)
    {
        var rgb565 = new byte[Geometry.Width * Geometry.Height * 2];
        WriteRgb565(Frames[index].Image, rgb565);
        var bgra = new byte[Geometry.Width * Geometry.Height * 4];
        for (int p = 0, o = 0; p < rgb565.Length; p += 2, o += 4)
        {
            int v = rgb565[p] | rgb565[p + 1] << 8;
            int r = v >> 11 & 0x1F, g = v >> 5 & 0x3F, b = v & 0x1F;
            bgra[o] = (byte)(b << 3 | b >> 2);
            bgra[o + 1] = (byte)(g << 2 | g >> 4);
            bgra[o + 2] = (byte)(r << 3 | r >> 2);
            bgra[o + 3] = 255;
        }
        return bgra;
    }

    /// <summary>Floyd–Steinberg dither when reducing to RGB565 (smoother gradients than the stock app's truncation).</summary>
    public bool Dither { get; set; } = true;

    private void WriteRgb565(Bitmap bmp, Span<byte> dst)
    {
        int w = Geometry.Width, h = Geometry.Height;
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            // Error buffers for this row and the next, 3 channels each, with a 1-pixel margin on both sides.
            var err = new float[w + 2, 3];
            var next = new float[w + 2, 3];
            int o = 0;
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    float r = row[x * 4 + 2], g = row[x * 4 + 1], b = row[x * 4];
                    if (Dither)
                    {
                        r = Math.Clamp(r + err[x + 1, 0], 0, 255);
                        g = Math.Clamp(g + err[x + 1, 1], 0, 255);
                        b = Math.Clamp(b + err[x + 1, 2], 0, 255);
                    }
                    int r5 = (int)(r + 0.5f) >> 3, g6 = (int)(g + 0.5f) >> 2, b5 = (int)(b + 0.5f) >> 3;
                    int v = (r5 << 11) | (g6 << 5) | b5;
                    dst[o++] = (byte)v;
                    dst[o++] = (byte)(v >> 8);

                    if (Dither)
                    {
                        Spread(0, r - (r5 << 3 | r5 >> 2));
                        Spread(1, g - (g6 << 2 | g6 >> 4));
                        Spread(2, b - (b5 << 3 | b5 >> 2));
                    }

                    void Spread(int c, float e)
                    {
                        err[x + 2, c] += e * 7 / 16;
                        next[x, c] += e * 3 / 16;
                        next[x + 1, c] += e * 5 / 16;
                        next[x + 2, c] += e * 1 / 16;
                    }
                }
                (err, next) = (next, err);
                Array.Clear(next);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        foreach (var f in Frames) f.Dispose();
        Frames.Clear();
    }
}

/// <summary>
/// Screen parameters from layouts/rgb-keyboard.xml (&lt;screen gif_headlength="256" gif_maxframes="140" width="240" height="135"/&gt;;
/// the F108 Pro allows 141 frames). Each <see cref="KeyboardModel"/> carries its own.
/// </summary>
public sealed record LcdGeometry(int Width = 240, int Height = 135, int HeaderLength = 256, int MaxFrames = 140)
{
    public const int PageSize = 4096;
}
