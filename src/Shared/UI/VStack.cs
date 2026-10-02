namespace Shared.UI;

/// <summary>
/// Stacks its children vertically at full width and sizes its own height to fit them. Used for banners and boxes whose
/// wrapped text changes height with the window width and the touch/standard mode.
/// </summary>
public sealed class VStack : Panel
{
    public VStack()
    {
        this.FixedSize();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var y = Padding.Top;
        foreach (Control c in Controls)
        {
            if (!c.Visible)
                continue;
            y += c.Margin.Top;
            c.SetBounds(Padding.Left + c.Margin.Left, y, Math.Max(10, ClientSize.Width - Padding.Horizontal - c.Margin.Horizontal), c.Height);
            y += c.Height + c.Margin.Bottom;
        }
        var height = y + Padding.Bottom;
        if (Height != height)
            Height = height;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        PerformLayout();
    }
}
