using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shared.Core.Touch;

namespace Shared.UI;

/// <summary>
/// Finger (and pen) scrolling for every window in the app, like a phone: drag anywhere on a scrolling page or list –
/// including on top of its buttons and text – and the content follows the finger, a flick keeps it gliding, a tap still
/// presses whatever is under the finger, and press-and-hold hands the touch to the control (to select text, drag…).
/// <para>
/// WinForms doesn't do this by itself: a finger on a button inside a scrolling panel presses the button instead. This
/// filter looks at touch/pen pointer messages before any window gets them. Where the finger lands on something that can
/// scroll it holds the touch back until it knows what the finger is doing, then either scrolls or replays the tap as a
/// normal click. Everywhere else (and for the mouse) nothing changes. Call <see cref="Install"/> once at startup.
/// </para>
/// </summary>
public static class TouchScroll
{
    // Message filters belong to a UI thread's message loop, so each UI thread gets its own.
    [ThreadStatic]
    private static Filter? _filter;
    private static readonly ConditionalWeakTable<Control, object> _excluded = new();

    /// <summary>Turns finger scrolling on for windows on this UI thread (safe to call more than once).</summary>
    public static void Install()
    {
        if (_filter != null)
            return;
        _filter = new Filter();
        Application.AddMessageFilter(_filter);
    }

    /// <summary>
    /// Leaves touches on this control (and its children) alone – for controls that handle touch themselves, such as the
    /// map, or where dragging means something else.
    /// </summary>
    public static void Exclude(Control control) => _excluded.AddOrUpdate(control, true);

    /// <summary>For tests: this thread's filter.</summary>
    internal static Filter? Instance => _filter;

    internal enum PointerAction
    {
        Down,
        Move,
        Up,
        Cancel
    }

    internal sealed class Filter : IMessageFilter
    {
        private const int WM_POINTERUPDATE = 0x0245;
        private const int WM_POINTERDOWN = 0x0246;
        private const int WM_POINTERUP = 0x0247;
        private const int WM_POINTERCAPTURECHANGED = 0x024C;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int MK_LBUTTON = 0x0001;
        private const uint PT_TOUCH = 2;
        private const uint PT_PEN = 3;
        private const int GCL_STYLE = -26;
        private const int CS_DBLCLKS = 0x0008;

        private readonly TouchPanGesture _gesture = new();
        private readonly TouchInertia _inertia = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
        private long _lastTick;

        // The contact being tracked.
        private uint _pointer;
        private IntPtr _hwnd;
        private List<ScrollTarget> _chain = new();
        private ScrollTarget? _target;
        private Point _lastScreen;

        // The scroll target gliding after a flick.
        private ScrollTarget? _gliding;

        // Last replayed tap, to turn two quick taps into a double-click.
        private IntPtr _lastTapHwnd;
        private Point _lastTapPoint;
        private long _lastTapTime;

        public Filter() => _timer.Tick += (_, _) => OnTick();

        public bool PreFilterMessage(ref Message m)
        {
            PointerAction action;
            switch (m.Msg)
            {
                case WM_POINTERDOWN: action = PointerAction.Down; break;
                case WM_POINTERUPDATE: action = PointerAction.Move; break;
                case WM_POINTERUP: action = PointerAction.Up; break;
                case WM_POINTERCAPTURECHANGED: action = PointerAction.Cancel; break;
                default: return false;
            }
            var id = (uint)(m.WParam.ToInt64() & 0xFFFF);
            // Mouse input is never touched; only fingers and pens.
            if (!GetPointerType(id, out var type) || (type != PT_TOUCH && type != PT_PEN))
                return false;
            var lp = m.LParam.ToInt64();
            var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            return Handle(action, m.HWnd, screen, id, Environment.TickCount64);
        }

        /// <summary>Returns true when the message is consumed (the window must not see it).</summary>
        internal bool Handle(PointerAction action, IntPtr hwnd, Point screen, uint pointer, long now)
        {
            var tracking = _gesture.State != TouchPanState.Idle;
            if (tracking && pointer != _pointer)
            {
                // A second finger while one is scrolling is ignored; any other time it goes through as usual.
                if (IsWindow(_hwnd))
                    return true;
                Reset();
                tracking = false;
            }

            switch (action)
            {
                case PointerAction.Down:
                    return Down(hwnd, screen, pointer, now);

                case PointerAction.Move when tracking:
                    _lastScreen = screen;
                    if (_gesture.State == TouchPanState.Holding)
                    {
                        PostMouse(WM_MOUSEMOVE, MK_LBUTTON, screen);
                        return true;
                    }
                    var wasPanning = _gesture.State == TouchPanState.Panning;
                    var delta = _gesture.Move(ToVec(screen), now);
                    if (!wasPanning && _gesture.State == TouchPanState.Panning)
                        _target = ChooseTarget(delta);
                    if (_target != null && (delta.X != 0 || delta.Y != 0))
                        _target.ScrollBy(delta);
                    return true;

                case PointerAction.Up when tracking:
                    var end = _gesture.Up(ToVec(screen), now);
                    switch (end)
                    {
                        case TouchPanEnd.Tap:
                            Tap(screen, now);
                            break;
                        case TouchPanEnd.Pan when _target != null:
                            _inertia.Start(_gesture.Velocity);
                            if (_inertia.Active)
                            {
                                _gliding = _target;
                                StartTimer();
                            }
                            break;
                        case TouchPanEnd.Hold:
                            PostMouse(WM_LBUTTONUP, 0, screen);
                            break;
                    }
                    Reset();
                    return true;

                case PointerAction.Cancel when tracking:
                    if (_gesture.State == TouchPanState.Holding)
                        PostMouse(WM_LBUTTONUP, 0, _lastScreen);
                    Reset();
                    return false;

                default:
                    return false;
            }
        }

        private bool Down(IntPtr hwnd, Point screen, uint pointer, long now)
        {
            // A finger on gliding content stops it, like a phone.
            _inertia.Stop();
            _gliding = null;

            var control = Control.FromChildHandle(hwnd);
            var chain = control == null ? null : FindTargets(control);
            if (chain == null || chain.Count == 0)
                return false;

            _pointer = pointer;
            _hwnd = hwnd;
            _chain = chain;
            _target = null;
            _lastScreen = screen;
            // Glove-sized slop: a bit of finger wobble is still a tap.
            var dpi = control!.DeviceDpi > 0 ? control.DeviceDpi : 96;
            _gesture.Slop = 14 * dpi / 96.0 * (UiMode.Touch ? 1.3 : 1);
            _gesture.Down(ToVec(screen), now);
            StartTimer();
            return true;
        }

        private void Tap(Point screen, long now)
        {
            var size = SystemInformation.DoubleClickSize;
            var doubleClick = _lastTapHwnd == _hwnd && now - _lastTapTime <= SystemInformation.DoubleClickTime &&
                              Math.Abs(screen.X - _lastTapPoint.X) <= size.Width * 3 &&
                              Math.Abs(screen.Y - _lastTapPoint.Y) <= size.Height * 3 &&
                              (GetClassLongPtr(_hwnd, GCL_STYLE).ToInt64() & CS_DBLCLKS) != 0;
            PostMouse(WM_MOUSEMOVE, 0, screen);
            PostMouse(doubleClick ? WM_LBUTTONDBLCLK : WM_LBUTTONDOWN, MK_LBUTTON, screen);
            PostMouse(WM_LBUTTONUP, 0, screen);
            if (doubleClick)
            {
                _lastTapHwnd = IntPtr.Zero;
            }
            else
            {
                _lastTapHwnd = _hwnd;
                _lastTapPoint = screen;
                _lastTapTime = now;
            }
        }

        /// <summary>The innermost scrollable control that can move the way the finger went; else the innermost.</summary>
        private ScrollTarget? ChooseTarget(Vec delta)
        {
            foreach (var t in _chain)
                if (t.CanScroll(delta))
                    return t;
            return _chain.Count > 0 ? _chain[0] : null;
        }

        private void OnTick()
        {
            var now = Environment.TickCount64;
            var elapsed = Math.Clamp(now - _lastTick, 1, 100);
            _lastTick = now;

            if (_gesture.State == TouchPanState.Pending && _gesture.CheckHold(now))
            {
                // Held still: from here on the control gets the touch as a normal mouse press.
                PostMouse(WM_MOUSEMOVE, 0, _lastScreen);
                PostMouse(WM_LBUTTONDOWN, MK_LBUTTON, _lastScreen);
            }

            if (_gliding != null)
            {
                var step = _inertia.Step(elapsed);
                if (!_inertia.Active || _gliding.Control.IsDisposed || !_gliding.ScrollBy(step))
                {
                    _inertia.Stop();
                    _gliding = null;
                }
            }

            if (_gesture.State != TouchPanState.Pending && _gliding == null)
                _timer.Stop();
        }

        private void StartTimer()
        {
            _lastTick = Environment.TickCount64;
            _timer.Start();
        }

        private void Reset()
        {
            _gesture.Cancel();
            _chain = new List<ScrollTarget>();
            _target = null;
            _hwnd = IntPtr.Zero;
        }

        private void PostMouse(int msg, int keys, Point screen)
        {
            if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
                return;
            var client = screen;
            ScreenToClient(_hwnd, ref client);
            if (msg != WM_LBUTTONUP)
                SetCursorPos(screen.X, screen.Y);
            var lParam = (IntPtr)(((client.Y & 0xFFFF) << 16) | (client.X & 0xFFFF));
            PostMessage(_hwnd, msg, (IntPtr)keys, lParam);
        }

        private static Vec ToVec(Point p) => new(p.X, p.Y);

        // ------------------------------------------------------------------ what can scroll

        private static List<ScrollTarget>? FindTargets(Control start)
        {
            var chain = new List<ScrollTarget>();
            for (var c = start; c != null; c = c.Parent)
            {
                if (IsExcluded(c, c == start))
                    return null;
                var target = ScrollTarget.For(c);
                if (target != null && target.HasRange)
                    chain.Add(target);
            }
            return chain;
        }

        private static bool IsExcluded(Control c, bool underFinger) =>
            _excluded.TryGetValue(c, out _) ||
            c is TrackBar or ScrollBar or Splitter ||
            // The splitter bar of a SplitContainer is the container itself (its panels are children).
            (underFinger && c is SplitContainer);

        [DllImport("user32.dll")]
        private static extern bool GetPointerType(uint pointerId, out uint pointerType);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref Point point);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);
    }

    /// <summary>Something that scrolls, and how to move it by pixels.</summary>
    internal abstract class ScrollTarget
    {
        protected ScrollTarget(Control control) => Control = control;

        public Control Control { get; }

        /// <summary>True when the content is bigger than the view on at least one axis.</summary>
        public abstract bool HasRange { get; }

        /// <summary>True when it can still move in the direction of <paramref name="delta"/> on some axis.</summary>
        public abstract bool CanScroll(Vec delta);

        /// <summary>Scrolls by <paramref name="delta"/> pixels; returns false when it didn't move (hit the end).</summary>
        public abstract bool ScrollBy(Vec delta);

        public static ScrollTarget? For(Control c) => c switch
        {
            DataGridView grid => new GridTarget(grid),
            ScrollableControl { AutoScroll: true } sc => new AutoScrollTarget(sc),
            ScrollableControl => null,
            ListView lv => new WindowTarget(lv, ListViewLineHeight(lv), 0),
            ListBox lb => new WindowTarget(lb, lb.ItemHeight, 0),
            TreeView tv => new WindowTarget(tv, tv.ItemHeight, 0),
            TextBoxBase tb => new WindowTarget(tb, tb.Font.Height, Math.Max(4, tb.Font.Height / 2)),
            _ => new WindowTarget(c, 16, 16)
        };

        private static int ListViewLineHeight(ListView lv)
        {
            try
            {
                if (lv.Items.Count > 0)
                    return Math.Max(4, lv.GetItemRect(lv.TopItem?.Index ?? 0).Height);
            }
            catch (ArgumentException)
            {
            }
            return lv.Font.Height + 4;
        }

        protected static bool Allows(double delta, int position, int max) =>
            (delta > 0 && position < max) || (delta < 0 && position > 0);
    }

    /// <summary>Panels and pages with AutoScroll (FlowLayoutPanel, TableLayoutPanel, Panel, forms).</summary>
    private sealed class AutoScrollTarget : ScrollTarget
    {
        private readonly ScrollableControl _sc;
        private double _carryX, _carryY;

        public AutoScrollTarget(ScrollableControl sc) : base(sc) => _sc = sc;

        private int MaxX => Math.Max(0, _sc.DisplayRectangle.Width - _sc.ClientSize.Width);
        private int MaxY => Math.Max(0, _sc.DisplayRectangle.Height - _sc.ClientSize.Height);
        private Point Position => new(-_sc.AutoScrollPosition.X, -_sc.AutoScrollPosition.Y);

        public override bool HasRange => (_sc.VerticalScroll.Visible && MaxY > 0) || (_sc.HorizontalScroll.Visible && MaxX > 0);

        public override bool CanScroll(Vec d)
        {
            var p = Position;
            return (_sc.HorizontalScroll.Visible && Allows(d.X, p.X, MaxX)) || (_sc.VerticalScroll.Visible && Allows(d.Y, p.Y, MaxY));
        }

        public override bool ScrollBy(Vec d)
        {
            var before = Position;
            _carryX += _sc.HorizontalScroll.Visible ? d.X : 0;
            _carryY += _sc.VerticalScroll.Visible ? d.Y : 0;
            int dx = (int)_carryX, dy = (int)_carryY;
            if (dx == 0 && dy == 0)
                return true;
            _carryX -= dx;
            _carryY -= dy;
            var x = Math.Clamp(before.X + dx, 0, MaxX);
            var y = Math.Clamp(before.Y + dy, 0, MaxY);
            if (x == before.X && y == before.Y)
                return false;
            _sc.AutoScrollPosition = new Point(x, y);
            _sc.Update();
            return true;
        }
    }

    /// <summary>Native lists and text boxes with standard window scroll bars: scrolled a line at a time.</summary>
    private sealed class WindowTarget : ScrollTarget
    {
        private const int WM_HSCROLL = 0x0114;
        private const int WM_VSCROLL = 0x0115;
        private const int SB_HORZ = 0;
        private const int SB_VERT = 1;
        private const int SB_LINEUP = 0;
        private const int SB_LINEDOWN = 1;
        private const int SB_ENDSCROLL = 8;
        private const int GWL_STYLE = -16;
        private const int WS_HSCROLL = 0x00100000;
        private const int WS_VSCROLL = 0x00200000;
        private const int LVM_SCROLL = 0x1014;

        private readonly int _lineHeight;
        private readonly int _lineWidth;
        private double _carryX, _carryY;

        public WindowTarget(Control c, int lineHeight, int lineWidth) : base(c)
        {
            _lineHeight = Math.Max(1, lineHeight);
            _lineWidth = lineWidth;
        }

        private bool Has(int bar) =>
            Control.IsHandleCreated &&
            (GetWindowLong(Control.Handle, GWL_STYLE) & (bar == SB_VERT ? WS_VSCROLL : WS_HSCROLL)) != 0 &&
            Info(bar) is { } i && i.nMax - i.nMin + 1 > Math.Max(1, i.nPage);

        private SCROLLINFO? Info(int bar)
        {
            var info = new SCROLLINFO { cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(), fMask = 0x17 };
            return GetScrollInfo(Control.Handle, bar, ref info) ? info : null;
        }

        private bool Allows(int bar, double delta)
        {
            if (delta == 0 || !Has(bar) || Info(bar) is not { } i)
                return false;
            var max = i.nMax - Math.Max(0, (int)i.nPage - 1);
            return (delta > 0 && i.nPos < max) || (delta < 0 && i.nPos > i.nMin);
        }

        public override bool HasRange => Has(SB_VERT) || (_lineWidth > 0 && Has(SB_HORZ)) || (Control is ListView && Has(SB_HORZ));

        public override bool CanScroll(Vec d) => Allows(SB_VERT, d.Y) || Allows(SB_HORZ, d.X);

        public override bool ScrollBy(Vec d)
        {
            var moved = false;
            var any = false;
            if (Has(SB_VERT))
            {
                _carryY += d.Y;
                var lines = (int)(_carryY / _lineHeight);
                _carryY -= lines * _lineHeight;
                any |= lines != 0;
                moved |= Lines(WM_VSCROLL, SB_VERT, lines);
            }
            if (Has(SB_HORZ))
            {
                if (Control is ListView)
                {
                    // List views scroll sideways by pixels.
                    _carryX += d.X;
                    var px = (int)_carryX;
                    _carryX -= px;
                    if (px != 0)
                    {
                        any = true;
                        var before = Info(SB_HORZ)?.nPos;
                        SendMessage(Control.Handle, LVM_SCROLL, (IntPtr)px, IntPtr.Zero);
                        moved |= Info(SB_HORZ)?.nPos != before;
                    }
                }
                else if (_lineWidth > 0)
                {
                    _carryX += d.X;
                    var cols = (int)(_carryX / _lineWidth);
                    _carryX -= cols * _lineWidth;
                    any |= cols != 0;
                    moved |= Lines(WM_HSCROLL, SB_HORZ, cols);
                }
            }
            // Below one line of movement nothing happens yet; that isn't "hit the end".
            return moved || !any;
        }

        private bool Lines(int msg, int bar, int count)
        {
            if (count == 0)
                return false;
            var before = Info(bar)?.nPos;
            for (var n = 0; n < Math.Abs(count); n++)
                SendMessage(Control.Handle, msg, (IntPtr)(count > 0 ? SB_LINEDOWN : SB_LINEUP), IntPtr.Zero);
            SendMessage(Control.Handle, msg, (IntPtr)SB_ENDSCROLL, IntPtr.Zero);
            return Info(bar)?.nPos != before;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SCROLLINFO
        {
            public uint cbSize;
            public uint fMask;
            public int nMin;
            public int nMax;
            public uint nPage;
            public int nPos;
            public int nTrackPos;
        }

        [DllImport("user32.dll")]
        private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO info);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>DataGridView draws its own scroll bars: rows vertically, pixels sideways.</summary>
    private sealed class GridTarget : ScrollTarget
    {
        private readonly DataGridView _grid;
        private double _carryX, _carryY;

        public GridTarget(DataGridView grid) : base(grid) => _grid = grid;

        private int RowHeight => Math.Max(4, _grid.RowTemplate.Height);
        private int FirstRow => Math.Max(0, _grid.FirstDisplayedScrollingRowIndex);
        private int MaxFirstRow => Math.Max(0, _grid.RowCount - _grid.DisplayedRowCount(false));

        private int MaxOffset
        {
            get
            {
                var width = 0;
                foreach (DataGridViewColumn col in _grid.Columns)
                    if (col.Visible && !col.Frozen)
                        width += col.Width;
                var view = _grid.DisplayRectangle.Width - (_grid.RowHeadersVisible ? _grid.RowHeadersWidth : 0);
                return Math.Max(0, width - view);
            }
        }

        public override bool HasRange => MaxFirstRow > 0 || MaxOffset > 0;

        public override bool CanScroll(Vec d) =>
            Allows(d.Y, FirstRow, MaxFirstRow) || Allows(d.X, _grid.HorizontalScrollingOffset, MaxOffset);

        public override bool ScrollBy(Vec d)
        {
            var moved = false;
            var any = false;
            _carryY += d.Y;
            var rows = (int)(_carryY / RowHeight);
            _carryY -= rows * RowHeight;
            if (rows != 0 && _grid.RowCount > 0)
            {
                any = true;
                var target = Math.Clamp(FirstRow + rows, 0, MaxFirstRow);
                if (target != FirstRow)
                {
                    _grid.FirstDisplayedScrollingRowIndex = target;
                    moved = true;
                }
            }
            _carryX += d.X;
            var px = (int)_carryX;
            _carryX -= px;
            if (px != 0)
            {
                any = true;
                var target = Math.Clamp(_grid.HorizontalScrollingOffset + px, 0, MaxOffset);
                if (target != _grid.HorizontalScrollingOffset)
                {
                    _grid.HorizontalScrollingOffset = target;
                    moved = true;
                }
            }
            return moved || !any;
        }
    }
}
