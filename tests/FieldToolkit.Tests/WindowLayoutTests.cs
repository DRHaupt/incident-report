using System.Drawing;
using FieldToolkit.Core.Windows;

namespace FieldToolkit.Tests;

public class WindowLayoutTests
{
    // A 12" Toughbook at 1366×768 with a 40 px taskbar.
    private static readonly Rectangle Area = new(0, 0, 1366, 728);
    private static readonly Size Min = new(420, 300);

    [Fact]
    public void Side_by_side_splits_width()
    {
        var r = WindowLayout.Arrange(ArrangeMode.SideBySide, 2, Area, Min);
        Assert.Equal(new Rectangle(0, 0, 683, 728), r[0]);
        Assert.Equal(new Rectangle(683, 0, 683, 728), r[1]);
    }

    [Fact]
    public void Side_by_side_falls_back_to_grid_when_too_narrow()
    {
        var r = WindowLayout.Arrange(ArrangeMode.SideBySide, 4, Area, Min);
        Assert.Equal(4, r.Count);
        Assert.All(r, x => Assert.True(x.Width >= Min.Width));
        Assert.Equal(2, r.Select(x => x.Top).Distinct().Count());
    }

    [Fact]
    public void Stacked_splits_height()
    {
        var r = WindowLayout.Arrange(ArrangeMode.Stacked, 2, Area, Min);
        Assert.Equal(0, r[0].Top);
        Assert.Equal(364, r[1].Top);
        Assert.All(r, x => Assert.Equal(1366, x.Width));
    }

    [Fact]
    public void Grid_of_three_gives_last_window_full_width()
    {
        var r = WindowLayout.Arrange(ArrangeMode.Grid, 3, Area, Min);
        Assert.Equal(683, r[0].Width);
        Assert.Equal(683, r[1].Width);
        Assert.Equal(1366, r[2].Width);
        Assert.Equal(364, r[2].Top);
    }

    [Fact]
    public void Grid_of_four_is_two_by_two_and_tiles_area_exactly()
    {
        var r = WindowLayout.Arrange(ArrangeMode.Grid, 4, Area, Min);
        Assert.Equal(Area.Width * Area.Height, r.Sum(x => x.Width * x.Height));
        for (var i = 0; i < r.Count; i++)
            for (var j = i + 1; j < r.Count; j++)
                Assert.False(r[i].IntersectsWith(r[j]));
    }

    [Fact]
    public void Cascade_stays_inside_area()
    {
        var r = WindowLayout.Arrange(ArrangeMode.Cascade, 12, Area, Min);
        Assert.All(r, x => Assert.True(Area.Contains(x)));
        Assert.NotEqual(r[0].Location, r[1].Location);
    }

    [Fact]
    public void Single_window_fills_area()
    {
        Assert.Equal(Area, WindowLayout.Arrange(ArrangeMode.Grid, 1, Area, Min)[0]);
    }

    [Fact]
    public void Offscreen_bounds_are_moved_onto_primary_screen()
    {
        var saved = new Rectangle(2500, 100, 800, 600); // was on a second monitor that is gone
        var fixedBounds = WindowLayout.EnsureVisible(saved, new[] { Area }, Min);
        Assert.True(Area.Contains(fixedBounds));
        Assert.Equal(saved.Size, fixedBounds.Size);
    }

    [Fact]
    public void Visible_bounds_are_left_alone()
    {
        var saved = new Rectangle(100, 100, 800, 600);
        Assert.Equal(saved, WindowLayout.EnsureVisible(saved, new[] { Area }, Min));
    }
}
