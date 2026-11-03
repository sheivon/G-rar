using System.Globalization;

namespace GRAR.Helpers;

/// <summary>Formatting helpers used across the UI.</summary>
public static class Fmt
{
    public static string Size(long bytes)
    {
        if (bytes < 0)
            return string.Empty;
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        double value = bytes;
        string[] units = { "KB", "MB", "GB", "TB", "PB" };
        int unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);

        return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Ratio(long size, long packed)
    {
        if (size <= 0 || packed < 0)
            return string.Empty;
        var percent = packed * 100.0 / size;
        return ((int)Math.Round(percent)).ToString(CultureInfo.InvariantCulture) + "%";
    }

    public static string Date(DateTime? date)
        => date is { } d && d > DateTime.MinValue
            ? d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : string.Empty;

    public static string Crc(long crc)
        => crc > 0 ? crc.ToString("X8", CultureInfo.InvariantCulture) : string.Empty;

    public static string Elapsed(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", (int)time.TotalMinutes, time.Seconds);
    }

    public static string Speed(long bytesPerSecond)
        => bytesPerSecond > 0 ? Size(bytesPerSecond) + "/s" : string.Empty;

    public static string Plural(int count, string singular, string plural)
        => count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural);
}
