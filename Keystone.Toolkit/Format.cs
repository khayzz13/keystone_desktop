/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// Format - Generic formatting utilities

namespace Keystone.Toolkit;

public static class Format
{
    /// <summary>Format timespan as human-readable age: "just now", "3m ago", "2h ago", "5d ago"</summary>
    public static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 0) return "just now";
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    /// <summary>Format large numbers: 1234 -> "1.2K", 1234567 -> "1.2M"</summary>
    public static string FormatNumber(long value)
    {
        if (value >= 1_000_000_000) return $"{value / 1_000_000_000.0:F1}B";
        if (value >= 1_000_000) return $"{value / 1_000_000.0:F1}M";
        if (value >= 1_000) return $"{value / 1_000.0:F1}K";
        return value.ToString();
    }

    /// <summary>Format large numbers (int overload)</summary>
    public static string FormatNumber(int value) => FormatNumber((long)value);

    /// <summary>Format large numbers (double overload)</summary>
    public static string FormatNumber(double value) => FormatNumber((long)value);

    /// <summary>Format bytes as human-readable: "1.2 KB", "3.4 MB", "5.6 GB"</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    /// <summary>Format a percentage: 0.1234 -> "12.34%"</summary>
    public static string FormatPercent(double value, int decimals = 2)
        => (value * 100).ToString($"F{decimals}") + "%";

    /// <summary>Format duration: 90s -> "1:30", 3661s -> "1:01:01"</summary>
    public static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }
}
