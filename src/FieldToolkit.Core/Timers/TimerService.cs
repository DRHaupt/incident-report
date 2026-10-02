namespace FieldToolkit.Core.Timers;

/// <summary>
/// The shared timer system. Every tool creates timers here; the UI calls <see cref="Tick"/> from a single clock and
/// shows the alerts it raises. The list is saved after every change when a store path is given.
/// </summary>
public sealed class TimerService
{
    private sealed class Store
    {
        public List<FieldTimer> Timers { get; set; } = new();
    }

    private readonly List<FieldTimer> _timers = new();
    private readonly string? _storePath;
    private readonly Func<DateTime> _clock;

    public TimerService(string? storePath = null, Func<DateTime>? utcClock = null)
    {
        _storePath = storePath;
        _clock = utcClock ?? (() => DateTime.UtcNow);
        Load();
    }

    /// <summary>Raised when timers are added, removed or change state (not on every tick).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised from <see cref="Tick"/> when a countdown ends or a stopwatch reminder is due.</summary>
    public event EventHandler<TimerAlert>? Alert;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<FieldTimer> Timers => _timers;

    public DateTime Now => _clock();

    /// <summary>Starts a stopwatch. <paramref name="startedUtc"/> lets a user back-date it, e.g. "symptoms started at 10:32".</summary>
    public FieldTimer StartStopwatch(string label, string source = "", int remindEveryMinutes = 0, int alertAfterMinutes = 0, DateTime? startedUtc = null)
    {
        var now = _clock();
        var start = startedUtc is { } s && s < now ? s : now;
        var t = new FieldTimer
        {
            Label = label,
            Source = source,
            Mode = TimerMode.Stopwatch,
            CreatedUtc = start,
            SegmentStartUtc = start,
            Running = true,
            RemindEveryMinutes = Math.Max(0, remindEveryMinutes),
            AlertAfterMinutes = Math.Max(0, alertAfterMinutes)
        };
        // A back-dated stopwatch shouldn't immediately fire every reminder it "missed".
        if (t.RemindEveryMinutes > 0)
            t.RemindersRaised = (int)(t.Elapsed(now).TotalMinutes / t.RemindEveryMinutes);
        if (t.AlertAfterMinutes > 0 && t.Elapsed(now).TotalMinutes >= t.AlertAfterMinutes)
            t.AlertAfterRaised = true;
        return Add(t);
    }

    public FieldTimer StartCountdown(string label, TimeSpan duration, string source = "")
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "A countdown must be longer than zero.");
        var now = _clock();
        return Add(new FieldTimer
        {
            Label = label,
            Source = source,
            Mode = TimerMode.Countdown,
            CreatedUtc = now,
            SegmentStartUtc = now,
            Running = true,
            Duration = duration
        });
    }

    private FieldTimer Add(FieldTimer t)
    {
        _timers.Insert(0, t);
        OnChanged();
        return t;
    }

    public FieldTimer? Find(Guid id) => _timers.FirstOrDefault(t => t.Id == id);

    public void Pause(FieldTimer t)
    {
        if (!t.Running)
            return;
        t.Accumulated = t.Elapsed(_clock());
        t.Running = false;
        OnChanged();
    }

    public void Resume(FieldTimer t)
    {
        if (t.Running)
            return;
        t.SegmentStartUtc = _clock();
        t.Running = true;
        OnChanged();
    }

    /// <summary>Back to zero (stopwatch) or the full duration (countdown), and running.</summary>
    public void Restart(FieldTimer t)
    {
        var now = _clock();
        t.Accumulated = TimeSpan.Zero;
        t.SegmentStartUtc = now;
        t.CreatedUtc = now;
        t.Running = true;
        t.RemindersRaised = 0;
        t.AlertAfterRaised = false;
        t.ExpiredRaised = false;
        t.NeedsAttention = false;
        OnChanged();
    }

    public void Rename(FieldTimer t, string label)
    {
        t.Label = label;
        OnChanged();
    }

    public void Acknowledge(FieldTimer t)
    {
        if (!t.NeedsAttention)
            return;
        t.NeedsAttention = false;
        OnChanged();
    }

    public void Remove(FieldTimer t)
    {
        if (_timers.Remove(t))
            OnChanged();
    }

    public void Clear()
    {
        _timers.Clear();
        OnChanged();
    }

    /// <summary>Checks every timer against the clock and raises any alerts that have become due.</summary>
    public void Tick()
    {
        var now = _clock();
        var alerts = new List<TimerAlert>();
        foreach (var t in _timers)
        {
            if (!t.Running)
                continue;
            if (t.Mode == TimerMode.Countdown)
            {
                if (!t.ExpiredRaised && t.IsExpired(now))
                {
                    t.ExpiredRaised = true;
                    alerts.Add(new TimerAlert(t, TimerAlertKind.CountdownFinished, $"{t.Label}: time is up"));
                }
                continue;
            }

            var minutes = t.Elapsed(now).TotalMinutes;
            if (t.AlertAfterMinutes > 0 && !t.AlertAfterRaised && minutes >= t.AlertAfterMinutes)
            {
                t.AlertAfterRaised = true;
                alerts.Add(new TimerAlert(t, TimerAlertKind.Threshold, $"{t.Label}: {t.AlertAfterMinutes} min reached"));
            }
            if (t.RemindEveryMinutes > 0)
            {
                var due = (int)(minutes / t.RemindEveryMinutes);
                if (due > t.RemindersRaised)
                {
                    t.RemindersRaised = due;
                    alerts.Add(new TimerAlert(t, TimerAlertKind.Reminder, $"{t.Label}: {FieldTimer.Format(t.Elapsed(now))} elapsed"));
                }
            }
        }

        if (alerts.Count == 0)
            return;
        foreach (var a in alerts)
            a.Timer.NeedsAttention = true;
        Save();
        foreach (var a in alerts)
            Alert?.Invoke(this, a);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnChanged()
    {
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        if (_storePath == null || !File.Exists(_storePath))
            return;
        try
        {
            _timers.AddRange(Json.Deserialize<Store>(File.ReadAllText(_storePath)).Timers);
        }
        catch (Exception)
        {
            // An unreadable timer file must never stop the toolkit starting.
        }
    }

    private void Save()
    {
        if (_storePath == null)
            return;
        try
        {
            Json.WriteFileAtomic(_storePath, Json.Serialize(new Store { Timers = _timers }));
        }
        catch (IOException)
        {
            // Timers still run in memory; the next change retries the save.
        }
    }
}
