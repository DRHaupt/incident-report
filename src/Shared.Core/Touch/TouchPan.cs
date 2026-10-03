namespace Shared.Core.Touch;

/// <summary>What a finger is doing, as decided by <see cref="TouchPanGesture"/>.</summary>
public enum TouchPanState
{
    Idle,
    /// <summary>Finger down, not yet moved past the slop: could still become a tap, a drag-scroll or a hold.</summary>
    Pending,
    /// <summary>Moved past the slop: the content follows the finger.</summary>
    Panning,
    /// <summary>Held still past the hold time: hand the touch to the control as a normal press (drag, select…).</summary>
    Holding
}

/// <summary>How a finger contact ended.</summary>
public enum TouchPanEnd
{
    None,
    /// <summary>Lifted without moving: a tap on whatever is under the finger.</summary>
    Tap,
    /// <summary>Lifted after scrolling; <see cref="TouchPanGesture.Velocity"/> gives the flick speed.</summary>
    Pan,
    /// <summary>Lifted after a hold (the control already has the press).</summary>
    Hold
}

/// <summary>Which directions a pan moves the content in.</summary>
[Flags]
public enum TouchPanAxes
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    Both = Horizontal | Vertical
}

/// <summary>A point or vector in pixels (UI-free, so the gesture logic can be tested anywhere).</summary>
public readonly record struct Vec(double X, double Y)
{
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec operator *(Vec a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>
/// Turns one finger's down / move / up into tap, drag-to-scroll or press-and-hold, like a phone: the content follows
/// the finger once it moves past a small slop, a quick lift is a tap, and holding still hands the press to the
/// control. Scroll amounts are returned in "scroll position" terms: dragging the finger up scrolls down (+Y).
/// </summary>
public sealed class TouchPanGesture
{
    private readonly List<(Vec Point, long Time)> _samples = new();
    private Vec _start;
    private Vec _last;
    private long _downTime;

    /// <param name="slop">Movement in pixels before a touch counts as a drag (larger with gloves).</param>
    /// <param name="holdMilliseconds">Time held still before the press goes to the control.</param>
    public TouchPanGesture(double slop = 12, int holdMilliseconds = 500)
    {
        Slop = slop;
        HoldMilliseconds = holdMilliseconds;
    }

    public double Slop { get; set; }
    public int HoldMilliseconds { get; set; }
    public TouchPanState State { get; private set; }

    /// <summary>The directions this pan was locked to when it started (a mostly vertical drag only scrolls vertically).</summary>
    public TouchPanAxes Axes { get; private set; }

    /// <summary>Where the finger went down.</summary>
    public Vec Start => _start;

    /// <summary>Finger speed at the end of a pan, in scroll pixels per second (same sign as the scroll deltas).</summary>
    public Vec Velocity { get; private set; }

    public void Down(Vec point, long timeMs)
    {
        State = TouchPanState.Pending;
        Axes = TouchPanAxes.None;
        Velocity = default;
        _start = _last = point;
        _downTime = timeMs;
        _samples.Clear();
        _samples.Add((point, timeMs));
    }

    /// <summary>
    /// Feeds a finger position. Returns the scroll delta to apply now (zero unless panning). When the move starts a pan,
    /// the delta includes the movement since the finger went down, so the content catches up with the finger.
    /// </summary>
    public Vec Move(Vec point, long timeMs)
    {
        if (State == TouchPanState.Idle)
            return default;
        Sample(point, timeMs);
        if (State == TouchPanState.Pending)
        {
            var moved = point - _start;
            if (moved.Length < Slop)
            {
                _last = point;
                return default;
            }
            State = TouchPanState.Panning;
            Axes = LockAxes(moved);
            _last = _start;
        }
        if (State != TouchPanState.Panning)
        {
            _last = point;
            return default;
        }
        var delta = Restrict(_last - point);
        _last = point;
        return delta;
    }

    /// <summary>Call periodically while pending; returns true once when the touch becomes a hold.</summary>
    public bool CheckHold(long timeMs)
    {
        if (State != TouchPanState.Pending || timeMs - _downTime < HoldMilliseconds)
            return false;
        State = TouchPanState.Holding;
        return true;
    }

    public TouchPanEnd Up(Vec point, long timeMs)
    {
        var state = State;
        if (state == TouchPanState.Panning)
        {
            Sample(point, timeMs);
            Velocity = Restrict(FingerVelocity(timeMs) * -1);
        }
        State = TouchPanState.Idle;
        return state switch
        {
            TouchPanState.Pending => TouchPanEnd.Tap,
            TouchPanState.Panning => TouchPanEnd.Pan,
            TouchPanState.Holding => TouchPanEnd.Hold,
            _ => TouchPanEnd.None
        };
    }

    public void Cancel()
    {
        State = TouchPanState.Idle;
        Velocity = default;
        _samples.Clear();
    }

    private static TouchPanAxes LockAxes(Vec moved)
    {
        double ax = Math.Abs(moved.X), ay = Math.Abs(moved.Y);
        if (ay >= ax * 2)
            return TouchPanAxes.Vertical;
        if (ax >= ay * 2)
            return TouchPanAxes.Horizontal;
        return TouchPanAxes.Both;
    }

    private Vec Restrict(Vec v) => new(Axes.HasFlag(TouchPanAxes.Horizontal) ? v.X : 0, Axes.HasFlag(TouchPanAxes.Vertical) ? v.Y : 0);

    private void Sample(Vec point, long timeMs)
    {
        _samples.Add((point, timeMs));
        // Keep the last ~100 ms: enough to measure a flick, short enough that a pause before lifting stops it.
        while (_samples.Count > 2 && timeMs - _samples[0].Time > 100)
            _samples.RemoveAt(0);
    }

    private Vec FingerVelocity(long nowMs)
    {
        if (_samples.Count < 2)
            return default;
        var first = _samples[0];
        var last = _samples[^1];
        // A finger that stopped before lifting should not fling the content.
        if (nowMs - last.Time > 80)
            return default;
        var dt = (last.Time - first.Time) / 1000.0;
        return dt <= 0.005 ? default : (last.Point - first.Point) * (1 / dt);
    }
}

/// <summary>
/// The glide after a flick: the content keeps moving and slows down smoothly. Call <see cref="Step"/> on a timer and
/// apply the returned scroll delta until <see cref="Active"/> is false (or the content hits its end).
/// </summary>
public sealed class TouchInertia
{
    /// <summary>Fraction of speed kept per second (lower stops sooner).</summary>
    public double FrictionPerSecond { get; set; } = 0.05;

    /// <summary>Flicks slower than this (px/s) don't glide.</summary>
    public double MinimumSpeed { get; set; } = 60;

    /// <summary>Cap so a wild flick doesn't throw the content miles.</summary>
    public double MaximumSpeed { get; set; } = 6000;

    public Vec Velocity { get; private set; }
    public bool Active => Velocity.Length >= MinimumSpeed;

    public void Start(Vec velocity)
    {
        var speed = velocity.Length;
        Velocity = speed > MaximumSpeed ? velocity * (MaximumSpeed / speed) : velocity;
        if (!Active)
            Velocity = default;
    }

    public void Stop() => Velocity = default;

    /// <summary>Advances by <paramref name="milliseconds"/> and returns the distance to scroll for that step.</summary>
    public Vec Step(double milliseconds)
    {
        if (!Active)
            return default;
        var dt = milliseconds / 1000.0;
        var decay = Math.Pow(FrictionPerSecond, dt);
        // Exact distance under exponential decay over the step: v * (decay - 1) / ln(friction).
        var factor = (decay - 1) / Math.Log(FrictionPerSecond);
        var delta = Velocity * factor;
        Velocity *= decay;
        if (!Active)
            Velocity = default;
        return delta;
    }
}
