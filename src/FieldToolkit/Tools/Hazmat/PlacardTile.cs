using System.Drawing.Drawing2D;
using FieldToolkit.Core.Hazmat;
using Shared.UI;

namespace FieldToolkit.Tools.Hazmat;

/// <summary>A tappable placard drawn as a diamond in the class colours, so the picker needs no image files.</summary>
internal sealed class PlacardTile : Control
{
    private readonly ErgPlacard _placard;
    private bool _selected;

    public PlacardTile(ErgPlacard placard)
    {
        _placard = placard;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Margin = new Padding(4);
        AccessibleName = $"Placard {placard.Code} {placard.Label}";
    }

    public ErgPlacard Placard => _placard;

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Invalidate();
        }
    }

    private static readonly Color Orange = Color.FromArgb(0xF2, 0x8C, 0x28);
    private static readonly Color Red = Color.FromArgb(0xD7, 0x1F, 0x26);
    private static readonly Color Green = Color.FromArgb(0x00, 0x84, 0x3D);
    private static readonly Color Blue = Color.FromArgb(0x00, 0x66, 0xB3);
    private static readonly Color Yellow = Color.FromArgb(0xFF, 0xD1, 0x00);

    /// <summary>(top colour, bottom colour, text colour, striped).</summary>
    private static (Color Top, Color Bottom, Color Text, bool Stripes) Style(string code) => code switch
    {
        "1" or "1.1" or "1.2" or "1.3" or "1.4" or "1.5" or "1.6" => (Orange, Orange, Color.Black, false),
        "2.1" or "3" => (Red, Red, Color.White, false),
        "2.2" => (Green, Green, Color.White, false),
        "OXYGEN" => (Yellow, Yellow, Color.Black, false),
        "DANGEROUS" => (Red, Red, Color.White, false),
        "4.1" => (Red, Red, Color.Black, true),
        "4.2" => (Color.White, Red, Color.Black, false),
        "4.3" => (Blue, Blue, Color.White, false),
        "5.1" => (Yellow, Yellow, Color.Black, false),
        "5.2" => (Red, Yellow, Color.Black, false),
        "7" => (Yellow, Color.White, Color.Black, false),
        "8" => (Color.White, Color.Black, Color.Black, false),
        "9" or "LITHIUM-ION" or "LITHIUM-METAL" => (Color.White, Color.White, Color.Black, true),
        _ => (Color.White, Color.White, Color.Black, false)
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(UiMode.Map(Parent?.BackColor ?? Color.White, true));

        var size = Math.Min(Width, Height) - 6;
        var cx = Width / 2f;
        var cy = Height / 2f;
        var half = size / 2f;
        var diamond = new[] { new PointF(cx, cy - half), new PointF(cx + half, cy), new PointF(cx, cy + half), new PointF(cx - half, cy) };
        using var path = new GraphicsPath();
        path.AddPolygon(diamond);

        var (top, bottom, text, stripes) = Style(_placard.Code);
        g.SetClip(path);
        using (var b = new SolidBrush(top))
            g.FillRectangle(b, cx - half, cy - half, size, half);
        using (var b = new SolidBrush(bottom))
            g.FillRectangle(b, cx - half, cy, size, half);
        if (stripes)
        {
            // 4.1: red and white vertical stripes over the whole diamond; 9: black stripes on the top half.
            var stripeColor = _placard.Code == "9" ? Color.Black : Color.White;
            var stripeBottom = _placard.Code == "9" ? cy : cy + half;
            using var sb = new SolidBrush(stripeColor);
            for (var x = cx - half; x < cx + half; x += size / 7f)
                g.FillRectangle(sb, x, cy - half, size / 14f, stripeBottom - (cy - half));
        }
        g.ResetClip();

        using (var border = new Pen(_selected ? Color.Black : Color.FromArgb(0x30, 0x30, 0x30), _selected ? 6 : 2))
            g.DrawPolygon(border, diamond);
        using (var inner = new Pen(text == Color.White ? Color.White : Color.Black, 1.5f))
        {
            var inset = size * 0.06f;
            g.DrawPolygon(inner, new[] { new PointF(cx, cy - half + inset), new PointF(cx + half - inset, cy), new PointF(cx, cy + half - inset), new PointF(cx - half + inset, cy) });
        }

        var labelColor = _placard.Code == "8" ? Color.Black : text;
        using var labelFont = new Font("Segoe UI", Math.Max(7, size / 13f), FontStyle.Bold, GraphicsUnit.Pixel);
        using var codeFont = new Font("Segoe UI", Math.Max(9, size / 7f), FontStyle.Bold, GraphicsUnit.Pixel);
        var labelRect = new Rectangle((int)(cx - half * 0.62f), (int)(cy - half * 0.45f), (int)(half * 1.24f), (int)(half * 0.62f));
        TextRenderer.DrawText(g, _placard.Label, labelFont, labelRect, labelColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        var codeColor = _placard.Code == "8" ? Color.White : text;
        var codeRect = new Rectangle((int)(cx - half * 0.4f), (int)(cy + half * 0.38f), (int)(half * 0.8f), (int)(half * 0.42f));
        // Class numbers sit in the bottom corner; named placards (DANGEROUS, HOT…) show their class there instead.
        var corner = _placard.Code switch { "LITHIUM-ION" or "LITHIUM-METAL" => "9", "OXYGEN" => "2", var c when c.Length <= 4 => c, _ => "" };
        TextRenderer.DrawText(g, corner, codeFont, codeRect, codeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
