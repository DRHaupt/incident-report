using System.Runtime.InteropServices;
using Shared.UI;
using Action = Shared.UI.TouchScroll.PointerAction;

// Real windows and injected input: run the UI test classes one at a time so windows don't cover each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FieldToolkit.UiTests;

/// <summary>
/// Finger scrolling in real windows: drags scroll panels, lists, text boxes and grids (even when they start on a
/// button), taps and holds still reach the control, the mouse and excluded controls are left alone. The first tests
/// feed pointer events straight into the filter; the last one injects real touch input through Windows.
/// </summary>
public class TouchScrollTests
{
    private const uint Finger = 77;

    private static void Pump(int milliseconds = 50)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Application.DoEvents();
            Thread.Sleep(5);
        } while (DateTime.UtcNow < until);
    }

    private static void RunSta(System.Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "Touch test timed out.");
        if (error != null)
            throw new Exception("Touch test failed: " + error, error);
    }

    private static TouchScroll.Filter Filter()
    {
        TouchScroll.Install();
        return TouchScroll.Instance!;
    }

    private static Point Center(Control c) => c.PointToScreen(new Point(c.Width / 2, c.Height / 2));

    /// <summary>Drags a finger from <paramref name="from"/> by <paramref name="dy"/> pixels in small steps, then lifts.</summary>
    private static void Drag(TouchScroll.Filter f, Control on, Point from, int dx, int dy, bool flick = false)
    {
        var t = Environment.TickCount64;
        Assert.True(f.Handle(Action.Down, on.Handle, from, Finger, t));
        const int steps = 10;
        for (var i = 1; i <= steps; i++)
        {
            t += flick ? 8 : 30;
            Assert.True(f.Handle(Action.Move, on.Handle, new Point(from.X + dx * i / steps, from.Y + dy * i / steps), Finger, t));
            Pump(5);
        }
        Assert.True(f.Handle(Action.Up, on.Handle, new Point(from.X + dx, from.Y + dy), Finger, t + (flick ? 4 : 200)));
        Pump(30);
    }

    private static void Tap(TouchScroll.Filter f, Control on)
    {
        var p = Center(on);
        var t = Environment.TickCount64;
        Assert.True(f.Handle(Action.Down, on.Handle, p, Finger, t));
        Assert.True(f.Handle(Action.Up, on.Handle, p, Finger, t + 60));
        Pump(100);
    }

    private static (Form Form, FlowLayoutPanel Panel, List<Button> Buttons, int[] Clicks) ScrollingPage()
    {
        var form = new Form { Width = 500, Height = 400, StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        var clicks = new int[40];
        var buttons = new List<Button>();
        for (var i = 0; i < 40; i++)
        {
            var n = i;
            var b = new Button { Text = "Button " + i, Width = 400, Height = 52 };
            b.Click += (_, _) => clicks[n]++;
            buttons.Add(b);
            panel.Controls.Add(b);
        }
        form.Controls.Add(panel);
        form.Show();
        Pump(150);
        return (form, panel, buttons, clicks);
    }

    [Fact]
    public void Dragging_on_a_button_scrolls_the_page_and_does_not_press_it() => RunSta(() =>
    {
        var f = Filter();
        var (form, panel, buttons, clicks) = ScrollingPage();
        Assert.Equal(0, panel.AutoScrollPosition.Y);

        Drag(f, buttons[2], Center(buttons[2]), 0, -200);
        // Content followed the finger up by 200 px (within a pixel of rounding).
        Assert.InRange(-panel.AutoScrollPosition.Y, 195, 205);
        Assert.All(clicks, c => Assert.Equal(0, c));

        // Dragging down brings it back; the start of the page stops it.
        Drag(f, buttons[6], Center(buttons[6]), 0, 400);
        Assert.Equal(0, panel.AutoScrollPosition.Y);
        Assert.All(clicks, c => Assert.Equal(0, c));

        // A flick keeps gliding after the finger lifts.
        Drag(f, buttons[1], Center(buttons[1]), 0, -100, flick: true);
        var afterLift = -panel.AutoScrollPosition.Y;
        Pump(400);
        Assert.True(-panel.AutoScrollPosition.Y > afterLift + 50, $"no glide: {afterLift} -> {-panel.AutoScrollPosition.Y}");
        form.Close();
    });

    [Fact]
    public void A_tap_presses_the_button_under_the_finger_and_a_hold_does_too() => RunSta(() =>
    {
        var f = Filter();
        var (form, _, buttons, clicks) = ScrollingPage();

        Tap(f, buttons[3]);
        Assert.Equal(1, clicks[3]);
        Assert.Equal(0, clicks[2] + clicks[4]);

        // Press and hold: after the hold time the button gets a normal press, released on lift.
        var p = Center(buttons[4]);
        Assert.True(f.Handle(Action.Down, buttons[4].Handle, p, Finger, Environment.TickCount64));
        Pump(700);
        Assert.True(f.Handle(Action.Up, buttons[4].Handle, p, Finger, Environment.TickCount64));
        Pump(100);
        Assert.Equal(1, clicks[4]);
        form.Close();
    });

    [Fact]
    public void Lists_text_boxes_and_grids_scroll_with_a_finger() => RunSta(() =>
    {
        var f = Filter();
        var form = new Form { Width = 900, Height = 400, StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        var list = new ListBox { Left = 0, Top = 0, Width = 200, Height = 300, IntegralHeight = false };
        for (var i = 0; i < 200; i++)
            list.Items.Add("Item " + i);
        var text = new TextBox { Left = 210, Top = 0, Width = 200, Height = 300, Multiline = true, ScrollBars = ScrollBars.Vertical };
        text.Text = string.Join(Environment.NewLine, Enumerable.Range(0, 200).Select(i => "Line " + i));
        var view = new ListView { Left = 420, Top = 0, Width = 200, Height = 300, View = View.Details };
        view.Columns.Add("Name", 150);
        for (var i = 0; i < 200; i++)
            view.Items.Add("Row " + i);
        var grid = new DataGridView { Left = 630, Top = 0, Width = 240, Height = 300, AllowUserToAddRows = false };
        grid.Columns.Add("a", "A");
        for (var i = 0; i < 200; i++)
            grid.Rows.Add("Cell " + i);
        form.Controls.AddRange(new Control[] { list, text, view, grid });
        form.Show();
        Pump(150);

        Drag(f, list, Center(list), 0, -150);
        Assert.True(list.TopIndex >= 5, "ListBox TopIndex " + list.TopIndex);

        Drag(f, text, Center(text), 0, -150);
        Assert.True(GetScrollPos(text.Handle, 1) >= 5, "TextBox line " + GetScrollPos(text.Handle, 1));

        Drag(f, view, Center(view), 0, -150);
        Assert.True(view.TopItem!.Index >= 5, "ListView top " + view.TopItem.Index);

        Drag(f, grid, Center(grid), 0, -150);
        Assert.True(grid.FirstDisplayedScrollingRowIndex >= 4, "Grid first row " + grid.FirstDisplayedScrollingRowIndex);
        form.Close();
    });

    [Fact]
    public void Content_that_fits_and_excluded_controls_are_left_alone() => RunSta(() =>
    {
        var f = Filter();
        var form = new Form { Width = 500, Height = 400, StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        var fits = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 120, AutoScroll = true };
        var lone = new Button { Text = "Fits", Width = 100, Height = 40 };
        fits.Controls.Add(lone);
        var scrolling = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var track = new TrackBar { Top = 0, Width = 300 };
        var custom = new Panel { Top = 60, Width = 300, Height = 100, BackColor = Color.Gray };
        TouchScroll.Exclude(custom);
        scrolling.Controls.AddRange(new Control[] { track, custom, new Label { Top = 2000, Text = "far" } });
        form.Controls.Add(scrolling);
        form.Controls.Add(fits);
        form.Show();
        Pump(150);

        var t = Environment.TickCount64;
        // Nothing to scroll: the touch goes straight through as usual.
        Assert.False(f.Handle(Action.Down, lone.Handle, Center(lone), Finger, t));
        Assert.False(f.Handle(Action.Up, lone.Handle, Center(lone), Finger, t + 50));
        // A slider inside a scrolling page keeps its own drag; so does a control that opted out.
        Assert.False(f.Handle(Action.Down, track.Handle, Center(track), Finger, t));
        Assert.False(f.Handle(Action.Up, track.Handle, Center(track), Finger, t + 50));
        Assert.False(f.Handle(Action.Down, custom.Handle, Center(custom), Finger, t));
        Assert.False(f.Handle(Action.Up, custom.Handle, Center(custom), Finger, t + 50));
        form.Close();
    });

    [Fact]
    public void Real_touch_input_injected_through_Windows_scrolls_the_page() => RunSta(() =>
    {
        TouchScroll.Install();
        var (form, panel, buttons, clicks) = ScrollingPage();
        form.TopMost = true;
        form.Activate();
        Pump(200);
        Assert.True(InitializeTouchInjection(1, 1 /* TOUCH_FEEDBACK_NONE */), "InitializeTouchInjection failed: " + Marshal.GetLastWin32Error());

        var start = Center(buttons[3]);
        Inject(start, PointerDown);
        Pump(30);
        for (var i = 1; i <= 12; i++)
        {
            Inject(new Point(start.X, start.Y - i * 15), PointerUpdate);
            Pump(30);
        }
        Pump(200); // pause so the lift doesn't flick
        Inject(new Point(start.X, start.Y - 180), PointerUp);
        Pump(300);

        Assert.InRange(-panel.AutoScrollPosition.Y, 150, 200);
        Assert.All(clicks, c => Assert.Equal(0, c));

        // A real tap still clicks the button under the finger.
        var target = buttons.First(b => panel.ClientRectangle.Contains(panel.PointToClient(Center(b))) && Center(b).Y > panel.PointToScreen(Point.Empty).Y + 60);
        var index = buttons.IndexOf(target);
        Inject(Center(target), PointerDown);
        Pump(40);
        Inject(Center(target), PointerUp);
        Pump(300);
        Assert.Equal(1, clicks[index]);
        form.Close();
    });

    // ------------------------------------------------------------------ touch injection

    private const uint PointerDown = 0x00010000 | 0x2 | 0x4; // DOWN | INRANGE | INCONTACT
    private const uint PointerUpdate = 0x00020000 | 0x2 | 0x4; // UPDATE | INRANGE | INCONTACT
    private const uint PointerUp = 0x00040000; // UP

    private static void Inject(Point p, uint flags)
    {
        var info = new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO { pointerType = 2, pointerId = 0, pointerFlags = flags, ptPixelLocation = p },
            touchFlags = 0,
            touchMask = 0
        };
        Assert.True(InjectTouchInput(1, new[] { info }), "InjectTouchInput failed: " + Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public Point ptPixelLocation;
        public Point ptHimetricLocation;
        public Point ptPixelLocationRaw;
        public Point ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public Rectangle rcContact;
        public Rectangle rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    [DllImport("user32.dll")]
    private static extern int GetScrollPos(IntPtr hWnd, int nBar);
}
