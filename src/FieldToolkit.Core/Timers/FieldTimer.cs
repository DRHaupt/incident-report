namespace FieldToolkit.Core.Timers;

public enum TimerMode
{
    /// <summary>Counts up from a start time (tourniquet applied, symptom onset, seizure started).</summary>
    Stopwatch,
    /// <summary>Counts down a fixed duration (cooling a burn, re-check glucose).</summary>
    Countdown
}

/// <summary>
/// A running timer. Times are stored as absolute UTC instants, so a timer keeps counting while its window is closed
/// and while the application is not running.
/// </summary>
public sealed class FieldTimer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "";

    /// <summary>Where the timer was started from, e.g. "First Aid · Severe bleeding". Empty for manual timers.</summary>
    public string Source { get; set; } = "";

    public TimerMode Mode { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>Start of the current running segment (only meaningful while <see cref="Running"/>).</summary>
    public DateTime SegmentStartUtc { get; set; }

    /// <summary>Time counted in earlier running segments (before the last pause).</summary>
    public TimeSpan Accumulated { get; set; }

    public bool Running { get; set; }

    /// <summary>Countdown length.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Stopwatch only: raise a reminder every N minutes (0 = none).</summary>
    public int RemindEveryMinutes { get; set; }

    /// <summary>Stopwatch only: raise a single alert once this many minutes have passed (0 = none).</summary>
    public int AlertAfterMinutes { get; set; }

    /// <summary>Number of reminder intervals already alerted.</summary>
    public int RemindersRaised { get; set; }

    public bool AlertAfterRaised { get; set; }

    public bool ExpiredRaised { get; set; }

    /// <summary>An alert has fired and nobody has acknowledged it yet.</summary>
    public bool NeedsAttention { get; set; }

    public TimeSpan Elapsed(DateTime nowUtc)
    {
        var e = Accumulated + (Running ? nowUtc - SegmentStartUtc : TimeSpan.Zero);
        return e < TimeSpan.Zero ? TimeSpan.Zero : e;
    }

    /// <summary>Countdown: time left (negative once overdue). Stopwatch: elapsed time.</summary>
    public TimeSpan Remaining(DateTime nowUtc) => Duration - Elapsed(nowUtc);

    public bool IsExpired(DateTime nowUtc) => Mode == TimerMode.Countdown && Remaining(nowUtc) <= TimeSpan.Zero;

    /// <summary>The value to show: elapsed for a stopwatch, remaining for a countdown (or time overdue, with a minus sign).</summary>
    public string Display(DateTime nowUtc)
    {
        if (Mode == TimerMode.Stopwatch)
            return Format(Elapsed(nowUtc));
        var r = Remaining(nowUtc);
        return r < TimeSpan.Zero ? "-" + Format(-r) : Format(r + TimeSpan.FromMilliseconds(999));
    }

    /// <summary>Wall-clock time the timer was started (local), e.g. to write on a casualty card.</summary>
    public DateTime StartedLocal => (Running ? SegmentStartUtc - Accumulated : CreatedUtc).ToLocalTime();

    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
            t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}

public enum TimerAlertKind
{
    CountdownFinished,
    Reminder,
    Threshold
}

public sealed record TimerAlert(FieldTimer Timer, TimerAlertKind Kind, string Message);
