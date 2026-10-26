namespace KeyboardStudio.Services;

public static class BatteryFormat
{
    /// <summary>"live" while the keyboard answers over 2.4G, otherwise how old the last reading is.</summary>
    public static string Age(KeyboardService kb)
    {
        if (kb.IsWireless) return "live";
        if (kb.BatteryTime is not DateTime t) return "";
        var age = DateTime.Now - t;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : t.ToString("MMM d");
    }

}
