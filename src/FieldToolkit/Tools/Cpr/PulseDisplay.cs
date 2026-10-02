using System.Drawing.Drawing2D;
using Shared;
using Shared.UI;

namespace FieldToolkit.Tools.Cpr;

/// <summary>The big beat indicator: a circle that flashes on every compression with the count in the middle.</summary>
internal sealed class PulseDisplay : Control
{
    private string _main = "READY";
    private string _sub = "Tap Start";
    private bool _flash;
    private bool _breaths;

    public PulseDisplay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public void Show(string main, string sub, bool breaths)
    {
        _main = main;
        _sub = sub;
        _breaths = breaths;
        Invalidate();
    }

    public bool Flash
    {
        set
        {
            if (_flash == value)
                return;
            _flash = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(UiMode.Map(BackColor, true));

        var d = Math.Max(10, Math.Min(Width, Height) - 16);
        var circle = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d);
        var fill = _breaths ? Brand.Amber : _flash ? Brand.Danger : Brand.Navy;
        using (var b = new SolidBrush(UiMode.Map(fill, true)))
            g.FillEllipse(b, circle);
        if (_flash && !_breaths)
            using (var ring = new Pen(UiMode.Map(Brand.Amber, true), Math.Max(6, d / 30)))
                g.DrawEllipse(ring, circle);

        var textColor = _breaths ? Color.Black : Color.White;
        using var mainFont = new Font("Segoe UI", Math.Max(12, d / (_main.Length > 3 ? 7f : 3.6f)), FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", Math.Max(10, d / 13f), FontStyle.Bold, GraphicsUnit.Pixel);
        var mainRect = new Rectangle(circle.X, circle.Y + d / 6, d, d / 2);
        var subRect = new Rectangle(circle.X + d / 10, circle.Y + d * 2 / 3 - d / 16, d * 8 / 10, d / 4);
        TextRenderer.DrawText(g, _main, mainFont, mainRect, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, _sub, subFont, subRect, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }
}
