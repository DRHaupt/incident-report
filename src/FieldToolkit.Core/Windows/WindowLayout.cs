using System.Drawing;

namespace FieldToolkit.Core.Windows;

public enum ArrangeMode
{
    SideBySide,
    Stacked,
    Grid,
    Cascade
}

/// <summary>Rectangle maths for the launcher's Arrange commands and for restoring saved window positions.</summary>
public static class WindowLayout
{
    /// <summary>
    /// Bounds for <paramref name="count"/> windows in <paramref name="area"/>. Side-by-side and stacked fall back to a grid
    /// when the windows would be narrower / shorter than <paramref name="minSize"/>, so they stay usable on a 10" screen.
    /// </summary>
    public static IReadOnlyList<Rectangle> Arrange(ArrangeMode mode, int count, Rectangle area, Size minSize, int gap = 0)
    {
        if (count <= 0)
            return Array.Empty<Rectangle>();
        if (count == 1 && mode != ArrangeMode.Cascade)
            return new[] { area };

        switch (mode)
        {
            case ArrangeMode.SideBySide when area.Width / count >= minSize.Width:
                return Cells(area, rows: 1, columns: count, count, gap);
            case ArrangeMode.Stacked when area.Height / count >= minSize.Height:
                return Cells(area, rows: count, columns: 1, count, gap);
            case ArrangeMode.Cascade:
                return Cascade(count, area);
            default:
                var columns = (int)Math.Ceiling(Math.Sqrt(count));
                var rows = (int)Math.Ceiling(count / (double)columns);
                return Cells(area, rows, columns, count, gap);
        }
    }

    /// <summary>A rows×columns grid; when the last row isn't full its windows share the full width.</summary>
    private static Rectangle[] Cells(Rectangle area, int rows, int columns, int count, int gap)
    {
        var result = new Rectangle[count];
        var index = 0;
        for (var r = 0; r < rows; r++)
        {
            var inRow = Math.Min(columns, count - index);
            var top = area.Top + area.Height * r / rows;
            var bottom = area.Top + area.Height * (r + 1) / rows;
            for (var c = 0; c < inRow; c++)
            {
                var left = area.Left + area.Width * c / inRow;
                var right = area.Left + area.Width * (c + 1) / inRow;
                result[index++] = Rectangle.FromLTRB(left, top, right - (c < inRow - 1 ? gap : 0), bottom - (r < rows - 1 ? gap : 0));
            }
        }
        return result;
    }

    private static Rectangle[] Cascade(int count, Rectangle area)
    {
        var width = area.Width * 3 / 4;
        var height = area.Height * 3 / 4;
        const int step = 36;
        // Wrap back to the top-left once the next window would run off the area.
        var perRun = Math.Max(1, Math.Min((area.Width - width) / step, (area.Height - height) / step) + 1);
        var result = new Rectangle[count];
        for (var i = 0; i < count; i++)
        {
            var k = i % perRun;
            result[i] = new Rectangle(area.Left + k * step, area.Top + k * step, width, height);
        }
        return result;
    }

    /// <summary>
    /// Makes saved bounds safe to restore: if the window's title bar wouldn't be reachable on any current screen (monitor
    /// unplugged, resolution changed) it is moved and if necessary shrunk onto the primary working area.
    /// </summary>
    public static Rectangle EnsureVisible(Rectangle bounds, IReadOnlyList<Rectangle> workingAreas, Size minSize)
    {
        if (workingAreas.Count == 0)
            return bounds;
        var titleBar = new Rectangle(bounds.Left, bounds.Top, bounds.Width, Math.Min(40, bounds.Height));
        foreach (var area in workingAreas)
        {
            var visible = Rectangle.Intersect(area, titleBar);
            if (visible.Width >= Math.Min(120, bounds.Width / 2) && visible.Height >= Math.Min(20, titleBar.Height))
                return bounds;
        }
        var primary = workingAreas[0];
        var w = Math.Clamp(bounds.Width, Math.Min(minSize.Width, primary.Width), primary.Width);
        var h = Math.Clamp(bounds.Height, Math.Min(minSize.Height, primary.Height), primary.Height);
        return new Rectangle(primary.Left + (primary.Width - w) / 2, primary.Top + (primary.Height - h) / 2, w, h);
    }
}
