namespace UbaTrayTool;

internal static class Format
{
    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Short form for running actions: "4.2s", "1m 05s".</summary>
    public static string Seconds(double seconds)
    {
        if (seconds < 10)
            return $"{seconds:0.0}s";
        if (seconds < 60)
            return $"{seconds:0}s";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }

    public static string Words(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.TotalSeconds:0.0}s";
    }

    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:0} {units[unit]}" : $"{bytes:0.#} {units[unit]}";
    }

    public static string Rate(double bytesPerSecond) => $"{Bytes(bytesPerSecond)}/s";

    public static string Count(long n) => n.ToString("N0");
}
