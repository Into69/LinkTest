using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LinkTest.Core;

/// <summary>Monotonic microsecond clock. Values are only meaningful within one process.</summary>
public static class Clock
{
    static readonly double UsPerTick = 1_000_000.0 / Stopwatch.Frequency;

    public static long NowUs => (long)(Stopwatch.GetTimestamp() * UsPerTick);

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint period);

    /// <summary>Raise the Windows timer resolution to 1 ms so the pacer's Thread.Sleep(1) is accurate.</summary>
    public static void EnableHighResolutionTimer()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { timeBeginPeriod(1); } catch { /* best effort */ }
    }

    /// <summary>Elapsed time as h:mm:ss with total hours (26:00:00, not "2:00:00" for a day and two hours).</summary>
    public static string FormatDuration(double seconds) => FormatDuration(TimeSpan.FromSeconds(Math.Max(0, seconds)));

    public static string FormatDuration(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
}
