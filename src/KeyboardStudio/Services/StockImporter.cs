using System.IO;
using System.Runtime.InteropServices;

namespace KeyboardStudio.Services;

/// <summary>
/// Reads LCD animations saved by the stock AJAZZ driver:
/// %LOCALAPPDATA%\&lt;driver name&gt; Files\db\*_datav1.db (t_ledlayer_data / t_ledframe_data) + led\&lt;device&gt;\&lt;dir&gt;\N.png.
/// Uses Windows' built-in winsqlite3.dll, so no extra dependencies.
/// </summary>
public static partial class StockImporter
{
    private const string TempPrefix = "kbstudio_";

    public sealed record StockAnimation(string Name, string FrameDir, List<(string File, int DelayMs)> Frames);

    public static List<StockAnimation> Find(Sonix.Keyboard.KeyboardModel model)
    {
        var result = new List<StockAnimation>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in Directory.EnumerateDirectories(local, model.StockFolderPattern))
        {
            var dbDir = Path.Combine(root, "db");
            if (!Directory.Exists(dbDir)) continue;
            foreach (var db in Directory.EnumerateFiles(dbDir, "*_datav1.db"))
            {
                var device = Path.GetFileName(db)[..^"_datav1.db".Length];
                try
                {
                    result.AddRange(ReadDb(db, Path.Combine(root, "led", device)));
                }
                catch (Exception)
                {
                    // Skip unreadable/locked databases; the stock app may be running.
                }
            }
        }
        return result;
    }

    /// <summary>The lighting effect the stock app last applied (t_light_data row with status = 1), if any.</summary>
    public static Sonix.Keyboard.LightSettings? ReadActiveLighting(Sonix.Keyboard.KeyboardModel model)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in Directory.EnumerateDirectories(local, model.StockFolderPattern))
        {
            var dbDir = Path.Combine(root, "db");
            if (!Directory.Exists(dbDir)) continue;
            foreach (var db in Directory.EnumerateFiles(dbDir, "*_datav1.db"))
            {
                var copy = Path.Combine(Path.GetTempPath(), $"{TempPrefix}{Guid.NewGuid():N}.db");
                try
                {
                    File.Copy(db, copy);
                    using var s = new Sqlite(copy);
                    var row = s.Query("SELECT mode, brightness, speed, direction, colorful, color_value FROM t_light_data WHERE profile = 1 AND status = 1 LIMIT 1").FirstOrDefault();
                    if (row is null) continue;
                    int color = int.Parse(row[5]); // COLORREF 0x00BBGGRR
                    return new Sonix.Keyboard.LightSettings
                    {
                        Mode = byte.Parse(row[0]),
                        Brightness = int.Parse(row[1]),
                        Speed = int.Parse(row[2]),
                        Direction = int.Parse(row[3]),
                        Rainbow = row[4] == "1",
                        R = (byte)color, G = (byte)(color >> 8), B = (byte)(color >> 16),
                    };
                }
                catch (Exception)
                {
                    // Fall through to defaults.
                }
                finally
                {
                    try { File.Delete(copy); } catch (IOException) { }
                }
            }
        }
        return null;
    }

    private static IEnumerable<StockAnimation> ReadDb(string dbPath, string ledRoot)
    {
        // Work on a copy so we never hold a lock on the stock app's database.
        var copy = Path.Combine(Path.GetTempPath(), $"{TempPrefix}{Guid.NewGuid():N}.db");
        File.Copy(dbPath, copy);
        try
        {
            using var db = new Sqlite(copy);
            var layers = db.Query("SELECT led_id, name, file_dir FROM t_ledlayer_data ORDER BY led_id");
            foreach (var layer in layers)
            {
                var dir = Path.Combine(ledRoot, layer[2]);
                if (!Directory.Exists(dir)) continue;
                var frames = db.Query($"SELECT file_name, delay_time FROM t_ledframe_data WHERE led_id = {int.Parse(layer[0])} ORDER BY frame_index")
                    .Select(r => (Path.Combine(dir, r[0]), int.TryParse(r[1], out var d) ? d : 100))
                    .Where(f => File.Exists(f.Item1))
                    .ToList();
                if (frames.Count > 0)
                    yield return new StockAnimation(FormatName(layer[1]), dir, frames);
            }
        }
        finally
        {
            try { File.Delete(copy); } catch (IOException) { }
        }
    }

    /// <summary>The stock app names layers by timestamp (yyyyMMddHHmmssfff); show that as a date.</summary>
    private static string FormatName(string raw) =>
        DateTime.TryParseExact(raw, "yyyyMMddHHmmssfff", null, System.Globalization.DateTimeStyles.None, out var t)
            ? $"{t:MMM d, HH:mm}"
            : raw;

    private sealed class Sqlite : IDisposable
    {
        private IntPtr _db;

        public Sqlite(string path)
        {
            if (sqlite3_open_v2(path, out _db, 1 /* READONLY */, IntPtr.Zero) != 0)
                throw new IOException($"Cannot open {path}");
        }

        public List<string[]> Query(string sql)
        {
            var rows = new List<string[]>();
            if (sqlite3_prepare_v2(_db, sql, -1, out var stmt, IntPtr.Zero) != 0)
                throw new IOException("SQLite prepare failed");
            try
            {
                int cols = sqlite3_column_count(stmt);
                while (sqlite3_step(stmt) == 100 /* SQLITE_ROW */)
                {
                    var row = new string[cols];
                    for (int i = 0; i < cols; i++)
                        row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, i)) ?? "";
                    rows.Add(row);
                }
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
            return rows;
        }

        public void Dispose()
        {
            if (_db != IntPtr.Zero) sqlite3_close(_db);
            _db = IntPtr.Zero;
        }

        [DllImport("winsqlite3.dll", CharSet = CharSet.Ansi)]
        private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport("winsqlite3.dll")]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport("winsqlite3.dll")]
        private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int nByte, out IntPtr stmt, IntPtr tail);

        [DllImport("winsqlite3.dll")]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll")]
        private static extern int sqlite3_column_count(IntPtr stmt);

        [DllImport("winsqlite3.dll")]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

        [DllImport("winsqlite3.dll")]
        private static extern int sqlite3_finalize(IntPtr stmt);
    }
}
