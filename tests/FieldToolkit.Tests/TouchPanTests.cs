using Shared.Core.Touch;

namespace FieldToolkit.Tests;

public class TouchPanTests
{
    [Fact]
    public void Quick_lift_without_moving_is_a_tap()
    {
        var g = new TouchPanGesture(slop: 12);
        g.Down(new Vec(100, 100), 0);
        Assert.Equal(default, g.Move(new Vec(105, 104), 40));
        Assert.Equal(TouchPanState.Pending, g.State);
        Assert.Equal(TouchPanEnd.Tap, g.Up(new Vec(105, 104), 80));
        Assert.Equal(TouchPanState.Idle, g.State);
    }

    [Fact]
    public void Dragging_up_scrolls_down_and_catches_up_with_the_finger()
    {
        var g = new TouchPanGesture(slop: 12);
        g.Down(new Vec(100, 300), 0);
        // First move past the slop: the whole 20 px since touch-down is applied at once.
        Assert.Equal(new Vec(0, 20), g.Move(new Vec(101, 280), 16));
        Assert.Equal(TouchPanState.Panning, g.State);
        Assert.Equal(TouchPanAxes.Vertical, g.Axes);
        // Mostly vertical drag: sideways wobble is ignored.
        Assert.Equal(new Vec(0, 30), g.Move(new Vec(104, 250), 32));
        Assert.Equal(new Vec(0, -10), g.Move(new Vec(104, 260), 48));
        Assert.Equal(TouchPanEnd.Pan, g.Up(new Vec(104, 260), 64));
    }

    [Fact]
    public void Diagonal_drag_scrolls_both_ways_and_sideways_drag_only_sideways()
    {
        var g = new TouchPanGesture(slop: 10);
        g.Down(new Vec(0, 0), 0);
        Assert.Equal(new Vec(-15, -15), g.Move(new Vec(15, 15), 10));
        Assert.Equal(TouchPanAxes.Both, g.Axes);

        g.Down(new Vec(0, 0), 100);
        Assert.Equal(new Vec(-30, 0), g.Move(new Vec(30, 4), 110));
        Assert.Equal(TouchPanAxes.Horizontal, g.Axes);
    }

    [Fact]
    public void Holding_still_becomes_a_hold_and_then_never_scrolls()
    {
        var g = new TouchPanGesture(slop: 12, holdMilliseconds: 500);
        g.Down(new Vec(50, 50), 1000);
        Assert.False(g.CheckHold(1400));
        Assert.True(g.CheckHold(1500));
        Assert.False(g.CheckHold(1600));
        Assert.Equal(TouchPanState.Holding, g.State);
        Assert.Equal(default, g.Move(new Vec(50, 150), 1700));
        Assert.Equal(TouchPanEnd.Hold, g.Up(new Vec(50, 150), 1800));
    }

    [Fact]
    public void A_pan_cannot_turn_into_a_hold()
    {
        var g = new TouchPanGesture(slop: 12, holdMilliseconds: 500);
        g.Down(new Vec(0, 0), 0);
        g.Move(new Vec(0, 40), 100);
        Assert.False(g.CheckHold(900));
        Assert.Equal(TouchPanState.Panning, g.State);
    }

    [Fact]
    public void Flick_gives_velocity_in_scroll_direction_but_a_pause_before_lifting_does_not()
    {
        var g = new TouchPanGesture(slop: 12);
        g.Down(new Vec(0, 500), 0);
        for (var t = 10; t <= 100; t += 10)
            g.Move(new Vec(0, 500 - t * 2), t); // finger moves up at 2 px/ms
        Assert.Equal(TouchPanEnd.Pan, g.Up(new Vec(0, 300), 105));
        Assert.InRange(g.Velocity.Y, 1800, 2200); // scrolls down at ~2000 px/s
        Assert.Equal(0, g.Velocity.X);

        g.Down(new Vec(0, 500), 1000);
        g.Move(new Vec(0, 400), 1050);
        g.Move(new Vec(0, 300), 1100);
        Assert.Equal(TouchPanEnd.Pan, g.Up(new Vec(0, 300), 1400)); // held 300 ms before lifting
        Assert.Equal(default, g.Velocity);
    }

    [Fact]
    public void Inertia_slows_down_and_stops()
    {
        var i = new TouchInertia();
        i.Start(new Vec(0, 2000));
        Assert.True(i.Active);
        double total = 0, previous = double.MaxValue;
        var steps = 0;
        while (i.Active && steps < 1000)
        {
            var d = i.Step(16).Y;
            Assert.True(d > 0 && d <= previous);
            previous = d;
            total += d;
            steps++;
        }
        Assert.False(i.Active);
        // Distance for exponential decay: v / -ln(friction) ≈ 2000 / 3.0 ≈ 668 px, minus the cut-off tail.
        Assert.InRange(total, 600, 670);
        Assert.InRange(steps, 30, 200);
    }

    [Fact]
    public void Inertia_ignores_slow_flicks_and_caps_wild_ones()
    {
        var i = new TouchInertia();
        i.Start(new Vec(10, 10));
        Assert.False(i.Active);
        Assert.Equal(default, i.Step(16));

        i.Start(new Vec(0, 100_000));
        Assert.Equal(i.MaximumSpeed, i.Velocity.Length, 3);
    }
}
