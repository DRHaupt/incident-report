using FieldToolkit.Core.FirstAid;

namespace FieldToolkit.Tests;

public class CprTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static List<(TimeSpan At, CprEvent Event)> Run(CprMetronome m, TimeSpan duration, int stepMs = 10)
    {
        var log = new List<(TimeSpan, CprEvent)>();
        m.Start(T0);
        for (var t = TimeSpan.Zero; t <= duration; t += TimeSpan.FromMilliseconds(stepMs))
            foreach (var e in m.Update(T0 + t))
                log.Add((t, e));
        return log;
    }

    [Fact]
    public void Beats_at_the_set_rate()
    {
        var m = new CprMetronome { Rate = 120, CompressionsPerCycle = 0 };
        var log = Run(m, TimeSpan.FromSeconds(10) - TimeSpan.FromMilliseconds(10));
        Assert.Equal(20, log.Count(x => x.Event == CprEvent.Beat));
    }

    [Fact]
    public void Thirty_compressions_then_breaths_then_resume()
    {
        var m = new CprMetronome { Rate = 120, CompressionsPerCycle = 30, BreathPause = TimeSpan.FromSeconds(5) };
        var log = Run(m, TimeSpan.FromSeconds(21));
        var breaths = log.Single(x => x.Event == CprEvent.BreathsDue);
        Assert.Equal(TimeSpan.FromSeconds(14.5), breaths.At);          // 30th beat at 29 × 0.5 s
        var resume = log.Single(x => x.Event == CprEvent.CompressionsResume);
        Assert.Equal(TimeSpan.FromSeconds(19.5), resume.At);
        Assert.Equal(1, m.CyclesCompleted);
        Assert.Equal(4, m.Count);                                       // beats at 19.5, 20.0, 20.5, 21.0
    }

    [Fact]
    public void Breaths_done_resumes_early()
    {
        var m = new CprMetronome { Rate = 100, CompressionsPerCycle = 15, BreathPause = TimeSpan.FromSeconds(10) };
        m.Start(T0);
        var now = T0;
        while (m.Phase != CprPhase.Breaths)
        {
            m.Update(now);
            now += TimeSpan.FromMilliseconds(10);
        }
        m.BreathsDone(now);
        Assert.Contains(CprEvent.CompressionsResume, m.Update(now));
        Assert.Equal(CprPhase.Compressions, m.Phase);
    }

    [Fact]
    public void Rate_is_clamped_to_guideline_range()
    {
        var m = new CprMetronome { MinRate = 100, MaxRate = 120, Rate = 140 };
        Assert.Equal(120, m.Rate);
        m.Rate = 60;
        Assert.Equal(100, m.Rate);
    }

    [Fact]
    public void Switch_prompt_every_two_minutes()
    {
        var m = new CprMetronome { Rate = 110, CompressionsPerCycle = 0 };
        var log = Run(m, TimeSpan.FromMinutes(4.5), stepMs: 50);
        Assert.Equal(2, log.Count(x => x.Event == CprEvent.SwitchDue));
    }

    [Fact]
    public void Stalled_ui_does_not_burst_beats()
    {
        var m = new CprMetronome { Rate = 120, CompressionsPerCycle = 0 };
        m.Start(T0);
        m.Update(T0);
        var events = m.Update(T0 + TimeSpan.FromSeconds(3));
        Assert.Single(events, e => e == CprEvent.Beat);
    }

    [Theory]
    [InlineData("30:2", 30, 2)]
    [InlineData("15:2", 15, 2)]
    [InlineData("continuous", 0, 0)]
    public void Ratios_parse(string ratio, int c, int b) => Assert.Equal((c, b), CprPreset.ParseRatio(ratio));

    [Fact]
    public void Shipped_cpr_settings_match_guideline_ranges()
    {
        var lib = FirstAidLibrary.Load(TestPaths.Content("firstaid"));
        Assert.Equal(100, lib.Cpr.MinRate);
        Assert.Equal(120, lib.Cpr.MaxRate);
        Assert.Equal(new[] { "adult", "child", "infant" }, lib.Cpr.Presets.Select(p => p.Id));
        Assert.All(lib.Cpr.Presets, p => Assert.Contains("30:2", p.Ratios));
        Assert.NotEmpty(lib.Cpr.Sources);
    }
}
