using System.Globalization;
using System.Runtime.InteropServices;

namespace Shared.UI;

/// <summary>
/// A date/time picker that follows the Night and Daylight themes. Windows draws the standard picker in its own light
/// colours whatever the app sets, so in those themes this one draws the box itself (the check box, the value, the
/// drop-down or up/down arrows) in the theme's colours. Typing, the check box and the calendar still work as usual;
/// the calendar that drops down is shown in the theme's colours too. In the Standard theme it is the normal picker.
/// </summary>
public class ThemedDateTimePicker : DateTimePicker
{
    private const int WmPaint = 0x000F;
    private const int DtmGetMonthCal = 0x1008;
    private const int McmGetMinReqRect = 0x1009;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore, IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct ps);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref Rect lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    private static bool Themed => UiMode.Night || UiMode.Daylight;

    public ThemedDateTimePicker()
    {
        ValueChanged += (_, _) => Invalidate();
        UiMode.Changed += OnModeChanged;
    }

    private void OnModeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;
        if (IsHandleCreated)
            ApplyCalendarColours();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            UiMode.Changed -= OnModeChanged;
        base.Dispose(disposing);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        base.OnMouseUp(mevent);
        Invalidate(); // the check box may have been toggled
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmPaint && Themed)
        {
            var hdc = BeginPaint(Handle, out var ps);
            try
            {
                using var g = Graphics.FromHdc(hdc);
                PaintThemed(g);
            }
            finally
            {
                EndPaint(Handle, ref ps);
            }
            return;
        }
        base.WndProc(ref m);
    }

    private void PaintThemed(Graphics g)
    {
        var night = UiMode.Night;
        var back = night ? UiMode.NightPalette.Raised : Color.White;
        var fore = night ? UiMode.NightPalette.Text : Color.Black;
        var border = Focused ? (night ? UiMode.NightPalette.Amber : Color.Black) : night ? UiMode.NightPalette.Border : Color.Black;
        var muted = night ? UiMode.NightPalette.MutedText : Color.DimGray;
        var r = ClientRectangle;
        using (var b = new SolidBrush(back))
            g.FillRectangle(b, r);
        using (var p = new Pen(border, UiMode.Daylight ? 2 : 1))
            g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);

        var scale = DeviceDpi / 96f;
        var x = (int)(4 * scale);
        if (ShowCheckBox)
        {
            var size = (int)(13 * scale);
            var box = new Rectangle(x, (r.Height - size) / 2, size, size);
            using (var p = new Pen(fore))
                g.DrawRectangle(p, box);
            if (Checked)
            {
                using var p = new Pen(fore, Math.Max(2f, 2f * scale));
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLines(p, new[]
                {
                    new PointF(box.Left + size * 0.2f, box.Top + size * 0.55f),
                    new PointF(box.Left + size * 0.42f, box.Top + size * 0.78f),
                    new PointF(box.Left + size * 0.82f, box.Top + size * 0.25f)
                });
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
            }
            x = box.Right + (int)(6 * scale);
        }

        var buttonWidth = (int)(SystemInformation.VerticalScrollBarWidth * 1.0);
        var textRect = new Rectangle(x, 0, Math.Max(0, r.Width - x - buttonWidth - (int)(2 * scale)), r.Height);
        TextRenderer.DrawText(g, FormattedValue(), Font, textRect, ShowCheckBox && !Checked ? muted : fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        // Drop-down arrow, or up/down arrows for a time picker.
        var cx = r.Width - buttonWidth / 2f - 1;
        using var arrow = new SolidBrush(fore);
        var a = 4 * scale;
        if (ShowUpDown)
        {
            var mid = r.Height / 2f;
            g.FillPolygon(arrow, new[] { new PointF(cx - a, mid - 2), new PointF(cx + a, mid - 2), new PointF(cx, mid - 2 - a) });
            g.FillPolygon(arrow, new[] { new PointF(cx - a, mid + 2), new PointF(cx + a, mid + 2), new PointF(cx, mid + 2 + a) });
        }
        else
        {
            var cy = r.Height / 2f - a / 2;
            g.FillPolygon(arrow, new[] { new PointF(cx - a, cy), new PointF(cx + a, cy), new PointF(cx, cy + a) });
        }
    }

    private string FormattedValue() => Format switch
    {
        DateTimePickerFormat.Long => Value.ToString(CultureInfo.CurrentCulture.DateTimeFormat.LongDatePattern, CultureInfo.CurrentCulture),
        DateTimePickerFormat.Time => Value.ToString(CultureInfo.CurrentCulture.DateTimeFormat.LongTimePattern, CultureInfo.CurrentCulture),
        DateTimePickerFormat.Custom when !string.IsNullOrEmpty(CustomFormat) => Value.ToString(CustomFormat, CultureInfo.CurrentCulture),
        _ => Value.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern, CultureInfo.CurrentCulture)
    };

    // The calendar that drops down: plain (unthemed) so it takes the theme's colours, resized to fit.
    protected override void OnDropDown(EventArgs eventargs)
    {
        base.OnDropDown(eventargs);
        if (!Themed)
            return;
        try
        {
            var cal = SendMessage(Handle, DtmGetMonthCal, IntPtr.Zero, IntPtr.Zero);
            if (cal == IntPtr.Zero)
                return;
            SetWindowTheme(cal, "", "");
            var rect = new Rect();
            SendMessage(cal, McmGetMinReqRect, IntPtr.Zero, ref rect);
            var w = rect.Right - rect.Left + 2;
            var h = rect.Bottom - rect.Top + 2;
            const uint noMoveZOrder = 0x0002 | 0x0004;
            SetWindowPos(cal, IntPtr.Zero, 0, 0, w, h, noMoveZOrder);
            var host = GetParent(cal);
            if (host != IntPtr.Zero)
                SetWindowPos(host, IntPtr.Zero, 0, 0, w + 2, h + 2, noMoveZOrder);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyCalendarColours();
    }

    private void ApplyCalendarColours()
    {
        if (IsDisposed)
            return;
        var night = UiMode.Night;
        CalendarMonthBackground = night ? UiMode.NightPalette.Surface : SystemColors.Window;
        CalendarForeColor = night ? UiMode.NightPalette.Text : SystemColors.WindowText;
        CalendarTitleBackColor = night ? UiMode.NightPalette.Header : UiMode.Daylight ? Color.Black : SystemColors.ActiveCaption;
        CalendarTitleForeColor = night ? UiMode.NightPalette.Amber : UiMode.Daylight ? Color.White : SystemColors.ActiveCaptionText;
        CalendarTrailingForeColor = night ? UiMode.NightPalette.MutedText : SystemColors.GrayText;
    }
}
