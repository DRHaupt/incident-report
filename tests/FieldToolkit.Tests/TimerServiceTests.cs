using FieldToolkit.Core.Timers;

namespace FieldToolkit.Tests;

public class TimerServiceTests
{
    private DateTime _now = new(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

    private TimerService Create(string? path = null) => new(path, () => _now);

    [Fact]
    public void Stopwatch_counts_up_from_start()
    {
        var svc = Create();
        var t = svc.StartStopwatch("Tourniquet applied");
        _now = _now.AddMinutes(12).AddSeconds(5);
        Assert.Equal(TimeSpan.FromSeconds(725), t.Elapsed(_now));
        Assert.Equal("12:05", t.Display(_now));
    }

    [Fact]
    public void Backdated_stopwatch_uses_given_start_and_skips_missed_reminders()
    {
        var svc = Create();
        var alerts = new List<TimerAlert>();
        svc.Alert += (_, a) => alerts.Add(a);
        var t = svc.StartStopwatch("Stroke onset", remindEveryMinutes: 10, startedUtc: _now.AddMinutes(-25));
        svc.Tick();
        Assert.Empty(alerts);
        Assert.Equal(TimeSpan.FromMinutes(25), t.Elapsed(_now));
        _now = _now.AddMinutes(5);
        svc.Tick();
        Assert.Single(alerts);
    }

    [Fact]
    public void Countdown_raises_one_alert_when_finished()
    {
        var svc = Create();
        var alerts = new List<TimerAlert>();
        svc.Alert += (_, a) => alerts.Add(a);
        var t = svc.StartCountdown("Burn cooling", TimeSpan.FromMinutes(20));
        _now = _now.AddMinutes(19);
        svc.Tick();
        Assert.Empty(alerts);
        _now = _now.AddMinutes(1);
        svc.Tick();
        svc.Tick();
        Assert.Single(alerts);
        Assert.Equal(TimerAlertKind.CountdownFinished, alerts[0].Kind);
        Assert.True(t.NeedsAttention);
        svc.Acknowledge(t);
        Assert.False(t.NeedsAttention);
    }

    [Fact]
    public void Countdown_display_shows_overdue_time()
    {
        var svc = Create();
        var t = svc.StartCountdown("Recheck", TimeSpan.FromMinutes(1));
        Assert.Equal("01:00", t.Display(_now));
        _now = _now.AddSeconds(90);
        Assert.Equal("-00:30", t.Display(_now));
    }

    [Fact]
    public void Reminders_fire_each_interval_and_threshold_fires_once()
    {
        var svc = Create();
        var alerts = new List<TimerAlert>();
        svc.Alert += (_, a) => alerts.Add(a);
        svc.StartStopwatch("Seizure", remindEveryMinutes: 2, alertAfterMinutes: 5);
        for (var i = 0; i < 7 * 60; i += 15)
        {
            _now = _now.AddSeconds(15);
            svc.Tick();
        }
        Assert.Equal(3, alerts.Count(a => a.Kind == TimerAlertKind.Reminder));
        Assert.Equal(1, alerts.Count(a => a.Kind == TimerAlertKind.Threshold));
    }

    [Fact]
    public void Pause_and_resume_exclude_paused_time()
    {
        var svc = Create();
        var t = svc.StartStopwatch("Test");
        _now = _now.AddMinutes(2);
        svc.Pause(t);
        _now = _now.AddMinutes(10);
        Assert.Equal(TimeSpan.FromMinutes(2), t.Elapsed(_now));
        svc.Resume(t);
        _now = _now.AddMinutes(1);
        Assert.Equal(TimeSpan.FromMinutes(3), t.Elapsed(_now));
    }

    [Fact]
    public void Timers_survive_restart_and_keep_counting()
    {
        var path = Path.Combine(TestPaths.TempFolder(), "timers.json");
        var first = Create(path);
        first.StartStopwatch("Tourniquet applied", source: "First Aid · Severe bleeding", remindEveryMinutes: 30);
        first.StartCountdown("Cooling", TimeSpan.FromMinutes(20));

        _now = _now.AddMinutes(45); // app closed for 45 minutes
        var second = Create(path);
        Assert.Equal(2, second.Timers.Count);
        var tq = second.Timers.Single(t => t.Label == "Tourniquet applied");
        Assert.Equal("First Aid · Severe bleeding", tq.Source);
        Assert.Equal(TimeSpan.FromMinutes(45), tq.Elapsed(_now));

        var alerts = new List<TimerAlert>();
        second.Alert += (_, a) => alerts.Add(a);
        second.Tick();
        // One reminder for the tourniquet (not one per missed interval) and the finished countdown.
        Assert.Equal(2, alerts.Count);
    }

    [Fact]
    public void Corrupt_store_is_ignored()
    {
        var path = Path.Combine(TestPaths.TempFolder(), "timers.json");
        File.WriteAllText(path, "{ not json");
        var svc = Create(path);
        Assert.Empty(svc.Timers);
    }
}
