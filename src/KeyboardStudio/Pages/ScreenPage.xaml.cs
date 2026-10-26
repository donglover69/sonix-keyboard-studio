using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sonix.Keyboard.Lcd;
using KeyboardStudio.Services;
using Microsoft.Win32;

namespace KeyboardStudio.Pages;

public partial class ScreenPage : UserControl
{
    /// <summary>Measured on the AK980 V2 and the F108 Pro: ~145 ms per 4 KiB page (the keyboard acks each flash write).</summary>
    private const double SecondsPerPage = 0.145;

    public sealed class Row : INotifyPropertyChanged
    {
        public required LibraryItem Item { get; init; }
        public string Name => Item.Name;
        private string _detail = "";
        public string Detail { get => _detail; set { _detail = value; Raise(); } }
        private ImageSource? _thumb;
        public ImageSource? Thumb { get => _thumb; set { _thumb = value; Raise(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
    }

    private readonly ObservableCollection<Row> _rows = new();
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _player = new();
    private readonly LcdGeometry _geo = App.Model.Lcd;

    private Row? _row;
    private LcdSource? _source;
    private List<BitmapSource> _frames = new();
    private List<int> _delays = new();
    private int _frame;
    private int _renderVersion;
    private bool _loading;
    private bool _uploading;

    public ScreenPage()
    {
        InitializeComponent();
        LibraryList.ItemsSource = _rows;
        foreach (var p in Enum.GetValues<OverlayPosition>()) OverlayPos.Items.Add(p);
        for (byte s = 1; s <= 8; s++) SlotBox.Items.Add(s);

        _loading = true;
        SlotBox.SelectedItem = App.Device.LcdSlot;
        SyncOnConnect.IsChecked = App.Device.SyncClockOnConnect;
        SyncHourly.IsChecked = App.Device.SyncClockHourly;
        SetEditor(new LcdOptionsDto());
        _loading = false;

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RenderAsync(); };
        _player.Tick += (_, _) => Advance();

        // First run: bring over the animations made in the stock app so the library isn't empty.
        if (Library.LoadAll().Count == 0)
        {
            try
            {
                foreach (var a in StockImporter.Find(App.Model)) Library.ImportStock(a);
            }
            catch (Exception)
            {
                // Importing is a convenience; the page works without it.
            }
        }
        LoadLibrary(App.Device.LastUploadedId);
    }

    // ------------------------------------------------------------------ library

    private void LoadLibrary(string? selectId = null)
    {
        _rows.Clear();
        foreach (var item in Library.LoadAll())
        {
            if (!File.Exists(item.ThumbPath)) Library.TrySaveThumbnail(item);
            var detail = item.LastUploaded is DateTime t ? $"Uploaded {t:MMM d}" : item.Origin is { } o ? $"From {o}" : $"Added {item.Created:MMM d}";
            var row = new Row { Item = item, Detail = detail };
            row.Thumb = LoadThumb(item);
            _rows.Add(row);
        }
        LibraryList.SelectedItem = _rows.FirstOrDefault(r => r.Item.Id == selectId) ?? _rows.FirstOrDefault();
    }

    private static ImageSource? LoadThumb(LibraryItem item)
    {
        if (!File.Exists(item.ThumbPath)) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
        bmp.UriSource = new Uri(item.ThumbPath);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Pictures and animations|*.gif;*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) ImportPaths(dlg.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder of numbered frames (0.png, 1.png, ...)" };
        if (dlg.ShowDialog() == true) ImportPaths([dlg.FolderName]);
    }

    private void ImportPaths(IEnumerable<string> paths)
    {
        string? last = null;
        foreach (var p in paths)
        {
            try
            {
                last = Library.Import(p).Id;
            }
            catch (Exception ex)
            {
                UploadStatus.Text = $"Could not import {Path.GetFileName(p)}: {ex.Message}";
            }
        }
        LoadLibrary(last);
    }

    private void ImportStock_Click(object sender, RoutedEventArgs e)
    {
        var existing = _rows.Select(r => r.Name).ToHashSet();
        var found = StockImporter.Find(App.Model);
        var fresh = found.Where(a => !existing.Contains(a.Name)).ToList();
        string? last = null;
        foreach (var a in fresh) last = Library.ImportStock(a).Id;
        UploadStatus.Text = found.Count == 0
            ? "No animations from the stock app for this keyboard were found."
            : $"Imported {fresh.Count} animation(s) from the stock app ({found.Count - fresh.Count} already in your library).";
        LoadLibrary(last ?? _row?.Item.Id);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_row is null) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Delete \"{_row.Name}\" from the library?", "Keyboard Studio",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Library.Delete(_row.Item);
        _row = null;
        LoadLibrary();
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ImportPaths(files);
    }

    private async void Library_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryList.SelectedItem is not Row row || row == _row) return;
        _row = row;
        ItemTitle.Text = row.Name;
        Editor.IsEnabled = false;
        _player.Stop();
        PreviewHint.Text = "Loading...";

        await _renderLock.WaitAsync();
        try
        {
            _source?.Dispose();
            _source = null;
            _source = await Task.Run(row.Item.LoadSource);
        }
        catch (Exception ex)
        {
            PreviewHint.Text = $"Cannot open: {ex.Message}";
            return;
        }
        finally
        {
            _renderLock.Release();
        }
        if (_row != row) return;

        _loading = true;
        TrimStart.Maximum = TrimEnd.Maximum = Math.Max(0, _source.Frames.Count - 1);
        SetEditor(row.Item.Options);
        _loading = false;
        Editor.IsEnabled = true;
        await RenderAsync();
    }

    // ------------------------------------------------------------------ editor

    private void SetEditor(LcdOptionsDto o)
    {
        FitFit.IsChecked = o.Fit == LcdFitMode.Fit;
        FitFill.IsChecked = o.Fit == LcdFitMode.Fill;
        FitStretch.IsChecked = o.Fit == LcdFitMode.Stretch;
        Zoom.Value = o.Zoom;
        PanX.Value = o.PanX;
        PanY.Value = o.PanY;
        FlipH.IsChecked = o.FlipHorizontal;
        FlipV.IsChecked = o.FlipVertical;
        BgColor.Color = ThemeManager.ParseColor(o.Background, Colors.Black);
        Brightness.Value = o.Brightness;
        Contrast.Value = o.Contrast;
        Saturation.Value = o.Saturation;
        Dither.IsChecked = o.Dither;
        Speed.Value = o.Speed;
        FixedDelayOn.IsChecked = o.FixedDelayMs.HasValue;
        FixedDelay.Value = o.FixedDelayMs ?? 100;
        FixedDelay.IsEnabled = o.FixedDelayMs.HasValue;
        TrimStart.Value = o.TrimStart;
        TrimEnd.Value = o.TrimEnd;
        FrameStep.Value = o.FrameStep;
        OverlayText.Text = o.OverlayText ?? "";
        OverlaySize.Value = o.OverlaySize;
        OverlayPos.SelectedItem = o.OverlayPosition;
        OverlayShadow.IsChecked = o.OverlayShadow;
        OverlayColor.Color = ThemeManager.ParseColor(o.OverlayColor, Colors.White);
    }

    private LcdOptionsDto ReadEditor() => new()
    {
        Fit = FitFill.IsChecked == true ? LcdFitMode.Fill : FitStretch.IsChecked == true ? LcdFitMode.Stretch : LcdFitMode.Fit,
        Zoom = Zoom.Value,
        PanX = PanX.Value,
        PanY = PanY.Value,
        FlipHorizontal = FlipH.IsChecked == true,
        FlipVertical = FlipV.IsChecked == true,
        Background = Hex(BgColor.Color),
        Brightness = Brightness.Value,
        Contrast = Contrast.Value,
        Saturation = Saturation.Value,
        Dither = Dither.IsChecked == true,
        Speed = Speed.Value,
        FixedDelayMs = FixedDelayOn.IsChecked == true ? (int)FixedDelay.Value : null,
        TrimStart = (int)TrimStart.Value,
        TrimEnd = (int)TrimEnd.Value,
        FrameStep = (int)FrameStep.Value,
        OverlayText = string.IsNullOrWhiteSpace(OverlayText.Text) ? null : OverlayText.Text,
        OverlaySize = (float)OverlaySize.Value,
        OverlayPosition = OverlayPos.SelectedItem is OverlayPosition p ? p : OverlayPosition.BottomRight,
        OverlayShadow = OverlayShadow.IsChecked == true,
        OverlayColor = Hex(OverlayColor.Color),
    };

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _row is null) return;
        FixedDelay.IsEnabled = FixedDelayOn.IsChecked == true;
        _row.Item.Options = ReadEditor();
        _debounce.Stop();
        _debounce.Start();
    }

    private void ResetOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_row is null) return;
        _loading = true;
        SetEditor(new LcdOptionsDto());
        _loading = false;
        Option_Changed(sender, e);
    }

    private void PreviewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading && _source is not null) _ = RenderAsync();
    }

    // ------------------------------------------------------------------ preview

    private async Task RenderAsync()
    {
        if (_source is null || _row is null) return;
        int version = ++_renderVersion;
        var dto = _row.Item.Options;
        var options = dto.ToOptions();
        bool deviceColors = DeviceColors.IsChecked == true;

        await _renderLock.WaitAsync();
        try
        {
            if (version != _renderVersion || _source is null || _row is null) return; // a newer request superseded this one
            var source = _source;
            var item = _row.Item;
            var result = await Task.Run(() =>
            {
                int requested = source.SelectFrames(options, int.MaxValue).Count;
                using var anim = source.Render(options, _geo);
                var frames = new List<BitmapSource>(anim.Frames.Count);
                for (int i = 0; i < anim.Frames.Count; i++)
                    frames.Add(deviceColors ? FromBgra(anim.DevicePreviewBgra(i)) : FromBitmap(anim.Frames[i].Image));
                if (!File.Exists(item.ThumbPath) && anim.Frames.Count > 0)
                    Library.SaveThumbnail(item, anim.Frames[0].Image);
                return (frames, delays: anim.Frames.Select(f => f.DelayMs).ToList(), anim.PageCount, requested);
            });
            if (version != _renderVersion || _row?.Item != item) return;

            _frames = result.frames;
            _delays = result.delays;
            Library.Save(item);
            _row.Thumb ??= LoadThumb(item);
            ShowInfo(result.PageCount, result.requested);
            FrameSlider.Maximum = Math.Max(0, _frames.Count - 1);
            _frame = Math.Min(_frame, Math.Max(0, _frames.Count - 1));
            ShowFrame(_frame);
            PreviewHint.Text = "";
            if (_frames.Count > 1 && PlayButton.Tag is not "paused") Play();
        }
        catch (Exception ex)
        {
            PreviewHint.Text = $"Render failed: {ex.Message}";
        }
        finally
        {
            _renderLock.Release();
        }
    }

    private void ShowInfo(int pages, int requested)
    {
        double loop = _delays.Sum() / 1000.0;
        double mb = pages * 4096 / 1024.0 / 1024.0;
        var eta = TimeSpan.FromSeconds(pages * SecondsPerPage);
        InfoText.Text = $"{_frames.Count} frame(s) · {loop:0.0} s loop · {mb:0.0} MB · ~{(eta.TotalMinutes >= 1 ? $"{(int)eta.TotalMinutes} min {eta.Seconds} s" : $"{eta.Seconds} s")} to upload";
        if (requested > _geo.MaxFrames)
        {
            WarnText.Text = $"This clip has {requested} frames but the keyboard holds {_geo.MaxFrames}. The rest is cut; raise \"Frame step\" or trim to fit the whole animation.";
            WarnText.Visibility = Visibility.Visible;
        }
        else
        {
            WarnText.Visibility = Visibility.Collapsed;
        }
    }

    private static BitmapSource FromBgra(byte[] bgra)
    {
        var bmp = BitmapSource.Create(240, 135, 96, 96, PixelFormats.Bgra32, null, bgra, 240 * 4);
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource FromBitmap(System.Drawing.Bitmap b)
    {
        var rect = new System.Drawing.Rectangle(0, 0, b.Width, b.Height);
        var data = b.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var bmp = BitmapSource.Create(b.Width, b.Height, 96, 96, PixelFormats.Bgra32, null, data.Scan0, data.Stride * b.Height, data.Stride);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            b.UnlockBits(data);
        }
    }

    private void ShowFrame(int i)
    {
        if (_frames.Count == 0) { PreviewImage.Source = null; FrameLabel.Text = ""; return; }
        _frame = Math.Clamp(i, 0, _frames.Count - 1);
        PreviewImage.Source = _frames[_frame];
        FrameLabel.Text = $"{_frame + 1} / {_frames.Count}  {_delays[_frame]} ms";
        _loading = true;
        FrameSlider.Value = _frame;
        _loading = false;
    }

    private void Play()
    {
        PlayButton.Content = ""; // pause glyph
        PlayButton.Tag = null;
        _player.Interval = TimeSpan.FromMilliseconds(Math.Max(10, _delays.ElementAtOrDefault(_frame)));
        _player.Start();
    }

    private void Advance()
    {
        if (_frames.Count == 0) { _player.Stop(); return; }
        ShowFrame((_frame + 1) % _frames.Count);
        _player.Interval = TimeSpan.FromMilliseconds(Math.Max(10, _delays[_frame]));
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_player.IsEnabled)
        {
            _player.Stop();
            PlayButton.Content = ""; // play glyph
            PlayButton.Tag = "paused";
        }
        else if (_frames.Count > 1)
        {
            Play();
        }
    }

    private void FrameSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _player.Stop();
        PlayButton.Content = "";
        PlayButton.Tag = "paused";
        ShowFrame((int)e.NewValue);
    }

    // ------------------------------------------------------------------ upload & clock

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null || _row is null || _uploading) return;
        if (!App.Keyboard.IsConnected)
        {
            UploadStatus.Text = "Connect the keyboard with the USB cable to upload.";
            return;
        }
        _uploading = true;
        UploadButton.IsEnabled = false;
        UploadProgress.Value = 0;
        UploadProgress.Visibility = Visibility.Visible;
        var row = _row;
        var options = row.Item.Options.ToOptions();
        var sw = Stopwatch.StartNew();
        try
        {
            UploadStatus.Text = "Preparing frames...";
            LcdAnimation anim;
            await _renderLock.WaitAsync();
            var source = _source;
            try { anim = await Task.Run(() => source.Render(options, _geo)); }
            finally { _renderLock.Release(); }

            using (anim)
            {
                var progress = new Progress<double>(p =>
                {
                    UploadProgress.Value = p;
                    var left = p > 0.02 ? TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / p * (1 - p)) : (TimeSpan?)null;
                    UploadStatus.Text = $"Uploading {p:P0}" + (left is { } l ? $" · {(int)l.TotalMinutes}:{l.Seconds:00} left" : "");
                });
                if (await App.Keyboard.UploadAsync(anim, progress))
                {
                    row.Item.LastUploaded = DateTime.Now;
                    Library.Save(row.Item);
                    row.Detail = $"Uploaded {DateTime.Now:MMM d}";
                    App.Device.LastUploadedId = row.Item.Id;
                    App.Device.Save();
                    UploadStatus.Text = $"Done in {sw.Elapsed:m\\:ss}. \"{row.Name}\" is on your keyboard.";
                }
                else
                {
                    UploadStatus.Text = App.Keyboard.Status;
                }
            }
        }
        catch (Exception ex)
        {
            UploadStatus.Text = $"Upload failed: {ex.Message}";
        }
        finally
        {
            _uploading = false;
            UploadButton.IsEnabled = true;
            UploadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void SyncClock_Click(object sender, RoutedEventArgs e)
    {
        UploadStatus.Text = await App.Keyboard.SyncClockAsync() ? $"Clock set to {DateTime.Now:HH:mm}." : App.Keyboard.Status;
    }

    private void ClockOption_Click(object sender, RoutedEventArgs e)
    {
        App.Device.SyncClockOnConnect = SyncOnConnect.IsChecked == true;
        App.Device.SyncClockHourly = SyncHourly.IsChecked == true;
        App.Device.Save();
    }

    private void Slot_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SlotBox.SelectedItem is not byte slot) return;
        App.Device.LcdSlot = slot;
        App.Device.Save();
    }
}
