using System.Drawing.Drawing2D;
using Shared;
using Shared.UI;

using Shared.Location;

namespace FieldToolkit.Tools.Location;

/// <summary>North-up dial with an arrow pointing along a bearing (degrees true).</summary>
internal sealed class BearingArrow : Control
{
    private double? _bearing;

    public BearingArrow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public double? Bearing
    {
        get => _bearing;
        set
        {
            if (_bearing == value)
                return;
            _bearing = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var d = Math.Min(Width, Height) - 4;
        if (d < 10)
            return;
        var c = new PointF(Width / 2f, Height / 2f);
        var navy = UiMode.Map(Brand.Navy, false);
        using (var ring = new Pen(navy, 2))
            g.DrawEllipse(ring, c.X - d / 2f, c.Y - d / 2f, d, d);
        using (var font = new Font("Segoe UI", Math.Max(7, d / 6f), FontStyle.Bold, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, "N", font, new Rectangle((int)(c.X - d / 4f), (int)(c.Y - d / 2f), (int)(d / 2f), (int)(d / 4f)), navy, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        if (_bearing is not { } b)
            return;
        var state = g.Save();
        g.TranslateTransform(c.X, c.Y);
        g.RotateTransform((float)b);
        var r = d / 2f - 4;
        using var brush = new SolidBrush(UiMode.Map(Brand.Danger, true));
        g.FillPolygon(brush, new[] { new PointF(0, -r), new PointF(r * 0.35f, r * 0.55f), new PointF(0, r * 0.3f), new PointF(-r * 0.35f, r * 0.55f) });
        g.Restore(state);
    }
}
