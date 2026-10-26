using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using Sonix.Keyboard;
using Sonix.Keyboard.Lcd;

namespace KeyboardStudio.Services;

/// <summary>Editor settings in a JSON-friendly shape (colors as hex).</summary>
public sealed record LcdOptionsDto
{
    public LcdFitMode Fit { get; init; } = LcdFitMode.Fit;
    public string Background { get; init; } = "#000000";
    public double Zoom { get; init; } = 1;
    public double PanX { get; init; }
    public double PanY { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public double Saturation { get; init; }
    public double Speed { get; init; } = 1;
    public int? FixedDelayMs { get; init; }
    public int TrimStart { get; init; }
    public int TrimEnd { get; init; }
    public int FrameStep { get; init; } = 1;
    public string? OverlayText { get; init; }
    public string OverlayColor { get; init; } = "#FFFFFF";
    public float OverlaySize { get; init; } = 18;
    public OverlayPosition OverlayPosition { get; init; } = OverlayPosition.BottomRight;
    public bool OverlayShadow { get; init; } = true;
    public bool Dither { get; init; } = true;

    public LcdRenderOptions ToOptions() => new()
    {
        Fit = Fit, Background = ColorTranslator.FromHtml(Background), Zoom = Zoom, PanX = PanX, PanY = PanY,
        FlipHorizontal = FlipHorizontal, FlipVertical = FlipVertical, Brightness = Brightness, Contrast = Contrast,
        Saturation = Saturation, Speed = Speed, FixedDelayMs = FixedDelayMs, TrimStart = TrimStart, TrimEnd = TrimEnd,
        FrameStep = FrameStep, OverlayText = OverlayText, OverlayColor = ColorTranslator.FromHtml(OverlayColor),
        OverlaySize = OverlaySize, OverlayPosition = OverlayPosition, OverlayShadow = OverlayShadow, Dither = Dither,
    };
}

public sealed class LibraryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastUploaded { get; set; }
    public string? Origin { get; set; }
    /// <summary>File name of the copied source inside the item folder, or "frames" for a frame folder.</summary>
    public string Source { get; set; } = "";
    /// <summary>Per-frame delays for frame folders (images carry no timing of their own).</summary>
    public List<int>? FrameDelays { get; set; }
    public LcdOptionsDto Options { get; set; } = new();

    public string Folder => Path.Combine(Library.Root, Id);
    public string SourcePath => Path.Combine(Folder, Source);
    public string ThumbPath => Path.Combine(Folder, "thumb.png");

    public LcdSource LoadSource()
    {
        var src = LcdSource.Load(SourcePath);
        if (FrameDelays is { Count: > 0 })
            for (int i = 0; i < src.Frames.Count && i < FrameDelays.Count; i++)
                src.Frames[i] = (src.Frames[i].Image, FrameDelays[i]);
        return src;
    }
}

/// <summary>
/// Saved animations under %LOCALAPPDATA%\KeyboardStudio\library\&lt;id&gt;\ (meta.json, source copy, thumb.png).
/// One library for all keyboards: every supported screen is 240×135.
/// </summary>
public static class Library
{
    public static string Root { get; } = Path.Combine(AppSettings.Folder, "library");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Copies the libraries of AK980 Studio and F108 Studio in (entries have unique ids, existing ones are kept).</summary>
    public static void MigrateLegacy()
    {
        var local = AppSettings.DataRoot;
        foreach (var model in KeyboardModel.All)
        {
            var old = Path.Combine(local, model.LegacyAppFolder, "library");
            if (!Directory.Exists(old)) continue;
            foreach (var dir in Directory.EnumerateDirectories(old))
            {
                var dst = Path.Combine(Root, Path.GetFileName(dir));
                if (Directory.Exists(dst)) continue;
                try
                {
                    CopyDirectory(dir, dst);
                }
                catch (IOException)
                {
                    // Skip an entry that can't be copied; the old folder stays untouched.
                }
            }
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    public static List<LibraryItem> LoadAll()
    {
        if (!Directory.Exists(Root)) return [];
        var items = new List<LibraryItem>();
        foreach (var meta in Directory.EnumerateFiles(Root, "meta.json", SearchOption.AllDirectories))
        {
            try
            {
                var item = JsonSerializer.Deserialize<LibraryItem>(File.ReadAllText(meta), Json);
                if (item is not null) items.Add(item);
            }
            catch (JsonException)
            {
                // Ignore a damaged entry rather than losing the whole library.
            }
        }
        return items.OrderByDescending(i => i.LastUploaded ?? i.Created).ToList();
    }

    public static void Save(LibraryItem item)
    {
        Directory.CreateDirectory(item.Folder);
        File.WriteAllText(Path.Combine(item.Folder, "meta.json"), JsonSerializer.Serialize(item, Json));
    }

    /// <summary>Copies a picture, GIF or frame folder into the library.</summary>
    public static LibraryItem Import(string path, string? name = null, List<int>? delays = null)
    {
        var item = new LibraryItem { Name = name ?? Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/')) };
        Directory.CreateDirectory(item.Folder);
        if (Directory.Exists(path))
        {
            item.Source = "frames";
            var dst = Path.Combine(item.Folder, "frames");
            Directory.CreateDirectory(dst);
            int n = 0;
            foreach (var f in LcdAnimation.FrameFiles(path))
                File.Copy(f, Path.Combine(dst, $"{n++}{Path.GetExtension(f)}"));
        }
        else
        {
            item.Source = "source" + Path.GetExtension(path).ToLowerInvariant();
            File.Copy(path, item.SourcePath);
        }
        item.FrameDelays = delays;
        Save(item);
        TrySaveThumbnail(item);
        return item;
    }

    public static LibraryItem ImportStock(StockImporter.StockAnimation a)
    {
        var item = new LibraryItem { Name = a.Name, Source = "frames" };
        var dst = Path.Combine(item.Folder, "frames");
        Directory.CreateDirectory(dst);
        for (int i = 0; i < a.Frames.Count; i++)
            File.Copy(a.Frames[i].File, Path.Combine(dst, $"{i}{Path.GetExtension(a.Frames[i].File)}"));
        item.FrameDelays = a.Frames.Select(f => f.DelayMs).ToList();
        item.Origin = "stock app";
        Save(item);
        TrySaveThumbnail(item);
        return item;
    }

    public static void SaveThumbnail(LibraryItem item, Bitmap frame)
    {
        using var thumb = new Bitmap(120, 68);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(frame, new Rectangle(0, 0, 120, 68));
        }
        thumb.Save(item.ThumbPath, ImageFormat.Png);
    }

    /// <summary>Renders the first frame with the item's settings; thumbnails are cosmetic, so failures are ignored.</summary>
    public static void TrySaveThumbnail(LibraryItem item)
    {
        try
        {
            using var src = LcdSource.Load(item.SourcePath, maxFrames: 1);
            if (src.Frames.Count == 0) return;
            using var frame = src.RenderFrame(0, item.Options.ToOptions(), new LcdGeometry());
            SaveThumbnail(item, frame);
        }
        catch (Exception)
        {
        }
    }

    public static void Delete(LibraryItem item)
    {
        if (Directory.Exists(item.Folder)) Directory.Delete(item.Folder, recursive: true);
    }
}
