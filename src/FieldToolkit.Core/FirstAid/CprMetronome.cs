namespace FieldToolkit.Core.FirstAid;

public enum CprPhase
{
    Stopped,
    Compressions,
    Breaths
}

public enum CprEvent
{
    /// <summary>A compression beat: push now.</summary>
    Beat,
    /// <summary>The cycle's compressions are done: give rescue breaths.</summary>
    BreathsDue,
    /// <summary>Breath pause over: back to compressions.</summary>
    CompressionsResume,
    /// <summary>Swap compressors / follow AED prompts.</summary>
    SwitchDue
}

/// <summary>
/// Drift-free CPR metronome and compression counter. The UI calls <see cref="Update"/> frequently with the current
/// time and reacts to the returned events; beats are scheduled from the start time so timer jitter doesn't accumulate.
/// </summary>
public sealed class CprMetronome
{
    private DateTime _nextBeat;
    private DateTime _breathEnd;
    private DateTime _started;
    private TimeSpan _accumulated;
    private int _switchPromptsRaised;
    private int _rate = 110;

    public int MinRate { get; set; } = 100;
    public int MaxRate { get; set; } = 120;

    public int Rate
    {
        get => _rate;
        set => _rate = Math.Clamp(value, MinRate, MaxRate);
    }

    /// <summary>Compressions per cycle; 0 = continuous compressions (no breath pauses).</summary>
    public int CompressionsPerCycle { get; set; } = 30;
    public int BreathsPerCycle { get; set; } = 2;
    public TimeSpan BreathPause { get; set; } = TimeSpan.FromSeconds(6);
    public TimeSpan SwitchInterval { get; set; } = TimeSpan.FromMinutes(2);

    public CprPhase Phase { get; private set; } = CprPhase.Stopped;

    /// <summary>Compressions in the current cycle (or since start, in continuous mode).</summary>
    public int Count { get; private set; }

    public int TotalCompressions { get; private set; }
    public int CyclesCompleted { get; private set; }

    public TimeSpan BeatInterval => TimeSpan.FromMinutes(1.0 / Rate);

    public TimeSpan Elapsed(DateTime now) => _accumulated + (Phase == CprPhase.Stopped ? TimeSpan.Zero : now - _started);

    /// <summary>Seconds left in the breath pause.</summary>
    public TimeSpan BreathTimeLeft(DateTime now) => Phase == CprPhase.Breaths && _breathEnd > now ? _breathEnd - now : TimeSpan.Zero;

    public void Start(DateTime now)
    {
        if (Phase != CprPhase.Stopped)
            return;
        _started = now;
        Phase = CprPhase.Compressions;
        _nextBeat = now;
    }

    public void Stop(DateTime now)
    {
        if (Phase == CprPhase.Stopped)
            return;
        _accumulated += now - _started;
        Phase = CprPhase.Stopped;
    }

    public void Reset()
    {
        Phase = CprPhase.Stopped;
        Count = TotalCompressions = CyclesCompleted = _switchPromptsRaised = 0;
        _accumulated = TimeSpan.Zero;
    }

    /// <summary>Rescuer finished the breaths early: resume compressions now.</summary>
    public void BreathsDone(DateTime now)
    {
        if (Phase == CprPhase.Breaths)
            _breathEnd = now;
    }

    public IReadOnlyList<CprEvent> Update(DateTime now)
    {
        var events = new List<CprEvent>();
        if (Phase == CprPhase.Stopped)
            return events;

        if (SwitchInterval > TimeSpan.Zero)
        {
            var due = (int)(Elapsed(now).Ticks / SwitchInterval.Ticks);
            if (due > _switchPromptsRaised)
            {
                _switchPromptsRaised = due;
                events.Add(CprEvent.SwitchDue);
            }
        }

        if (Phase == CprPhase.Breaths)
        {
            if (now < _breathEnd)
                return events;
            Phase = CprPhase.Compressions;
            Count = 0;
            CyclesCompleted++;
            _nextBeat = now;
            events.Add(CprEvent.CompressionsResume);
        }

        if (now >= _nextBeat)
        {
            Count++;
            TotalCompressions++;
            events.Add(CprEvent.Beat);
            _nextBeat += BeatInterval;
            // If the UI stalled for more than a beat, re-anchor rather than firing a burst of catch-up beats.
            if (now - _nextBeat > BeatInterval)
                _nextBeat = now + BeatInterval;

            if (CompressionsPerCycle > 0 && Count >= CompressionsPerCycle)
            {
                Phase = CprPhase.Breaths;
                _breathEnd = now + BreathPause;
                events.Add(CprEvent.BreathsDue);
            }
        }
        return events;
    }
}
