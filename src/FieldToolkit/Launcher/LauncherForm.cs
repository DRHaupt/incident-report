using FieldToolkit.Core.Timers;
using FieldToolkit.Core.Windows;
using FieldToolkit.Framework;
using FieldToolkit.Tools.Timers;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Launcher;

/// <summary>
/// The home window: large tiles for each tool, the window arrange commands, active timers and open windows, and the
/// Touch / Daylight switches. Closing it closes the toolkit (open windows are remembered for next time).
/// </summary>
public sealed class LauncherForm : Form
{
    private readonly Panel _side = new() { Dock = DockStyle.Right, Width = 330, Padding = new Padding(8) };
    private readonly FlowLayoutPanel _sideStack;
    private readonly FlowLayoutPanel _tiles = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
    private readonly Panel _restoreBanner = new() { Dock = DockStyle.Top, Height = 64, BackColor = Brand.Amber, Visible = false, Padding = new Padding(10, 6, 10, 6) };
    private readonly Button _alertBanner;
    private readonly Dictionary<Guid, (Label Time, Control Card)> _timerCards = new();
    private SessionState? _pendingSession;
    private bool _closingConfirmed;

    public LauncherForm()
    {
        Text = Branding.ProductName;
        Icon = Branding.AppIcon;
        MinimumSize = new Size(480, 420);
        KeyPreview = true;
        RestorePlacement();

        _sideStack = Ui.Stack(new Padding(0));
        _side.FixedSize();
        _side.Controls.Add(_sideStack);

        _alertBanner = Ui.Button("", ButtonStyle.Danger, AcknowledgeAlerts, FontRole.Heading);
        _alertBanner.Dock = DockStyle.Top;
        _alertBanner.Height = 64;
        _alertBanner.Visible = false;

        BuildTiles();
        BuildRestoreBanner();

        Controls.Add(_tiles);
        Controls.Add(_side);
        Controls.Add(BuildArrangeBar());
        Controls.Add(_restoreBanner);
        Controls.Add(_alertBanner);
        Controls.Add(BuildHeader());

        UiMode.Track(this);
        AppServices.ShowLauncher = BringToFrontFromOtherInstance;
        AppServices.Windows.CurrentScreen = () => Screen.FromControl(this);
        AppServices.Windows.Changed += (_, _) => RebuildSide();
        AppServices.Timers.Changed += (_, _) => RebuildSide();
        AppServices.ClockTick += (_, _) => UpdateTimes();
        UiMode.Changed += (_, _) => { LayoutTiles(); RebuildSide(); };
        Resize += (_, _) => AdaptToWidth();
    }

    // ------------------------------------------------------------------ layout

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 76, ColumnCount = 5, RowCount = 1, BackColor = Brand.Navy, Padding = new Padding(10, 8, 10, 8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++)
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new PictureBox { Image = Branding.Logo48, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        header.Controls.Add(new Label { Text = Branding.ProductName, ForeColor = Color.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }.Styled(FontRole.Title), 1, 0);

        Button Toggle(string text, Action click)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, BackColor = Color.FromArgb(0x41, 0x52, 0x6A), ForeColor = Color.White,
                Margin = new Padding(4, 0, 0, 0), Padding = new Padding(10, 0, 10, 0), Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => click();
            return b.Styled(FontRole.Bold);
        }
        var touch = Toggle("", () => UiMode.SetTouch(!UiMode.Touch));
        var daylight = Toggle("", () => UiMode.SetDaylight(!UiMode.Daylight));
        void Sync()
        {
            touch.Text = UiMode.Touch ? "Touch Mode ✓" : "Touch Mode";
            daylight.Text = UiMode.Daylight ? "Daylight ✓" : "Daylight";
            UiMode.SetColors(touch, UiMode.Touch ? Brand.Amber : Color.FromArgb(0x41, 0x52, 0x6A), UiMode.Touch ? Brand.Navy : Color.White);
            UiMode.SetColors(daylight, UiMode.Daylight ? Brand.Amber : Color.FromArgb(0x41, 0x52, 0x6A), UiMode.Daylight ? Brand.Navy : Color.White);
        }
        UiMode.Changed += (_, _) => Sync();
        Load += (_, _) => Sync();
        header.Controls.Add(touch, 2, 0);
        header.Controls.Add(daylight, 3, 0);
        header.Controls.Add(Toggle("Settings", ShowOptions), 4, 0);
        return header;
    }

    private void BuildTiles()
    {
        foreach (var tool in ToolRegistry.All)
            _tiles.Controls.Add(BuildTile(tool));
        LayoutTiles();
    }

    private static Control BuildTile(ToolDescriptor tool)
    {
        var tile = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 2, Margin = new Padding(8), Padding = new Padding(12),
            BackColor = Brand.Panel, Tag = tool, BorderStyle = BorderStyle.FixedSingle
        };
        tile.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        tile.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tile.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        tile.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        var icon = new PictureBox { Image = Branding.ToolImage(tool.IconName, 128), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0) };
        tile.Controls.Add(icon, 0, 0);
        tile.SetRowSpan(icon, 2);
        tile.Controls.Add(new Label { Text = tool.Title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, ForeColor = Brand.Navy, AutoEllipsis = true }.Styled(FontRole.Heading), 1, 0);
        tile.Controls.Add(new Label { Text = tool.Description, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft, ForeColor = Brand.Muted }, 1, 1);
        tile.FixedSize();
        Ui.OnTap(tile, () => AppServices.Windows.Open(tool.Id));
        return tile;
    }

    /// <summary>Tiles fill the width in one or two columns so they stay big on any screen.</summary>
    private void LayoutTiles()
    {
        var available = _tiles.ClientSize.Width - _tiles.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        var columns = available >= Ui.Scaled(560) ? 2 : 1;
        var width = available / columns - 16;
        var height = Ui.Scaled(120);
        _tiles.SuspendLayout();
        foreach (Control tile in _tiles.Controls)
            tile.Size = new Size(Math.Max(200, width), height);
        _tiles.ResumeLayout(true);
    }

    private Control BuildArrangeBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 70, ColumnCount = 1, RowCount = 1, Padding = new Padding(8, 6, 8, 6), BackColor = Brand.Panel };
        var row = Ui.ButtonRow(
            Ui.Button("Side by side", ButtonStyle.Secondary, () => AppServices.Windows.Arrange(ArrangeMode.SideBySide)),
            Ui.Button("Stacked", ButtonStyle.Secondary, () => AppServices.Windows.Arrange(ArrangeMode.Stacked)),
            Ui.Button("Grid", ButtonStyle.Secondary, () => AppServices.Windows.Arrange(ArrangeMode.Grid)),
            Ui.Button("Cascade", ButtonStyle.Secondary, () => AppServices.Windows.Arrange(ArrangeMode.Cascade)),
            Ui.Button("Minimize all", ButtonStyle.Secondary, AppServices.Windows.MinimizeAll),
            Ui.Button("Close all", ButtonStyle.Secondary, CloseAllWindows));
        row.Dock = DockStyle.Fill;
        bar.Controls.Add(row);
        return bar;
    }

    private void BuildRestoreBanner()
    {
        var text = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Brand.Navy, AutoEllipsis = true }.Styled(FontRole.Bold);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        var restore = Ui.Button("Restore", ButtonStyle.Primary, RestorePending);
        var dismiss = Ui.Button("Dismiss", ButtonStyle.Secondary, () => { _pendingSession = null; _restoreBanner.Visible = false; });
        restore.Width = dismiss.Width = 120;
        restore.Height = dismiss.Height = 48;
        buttons.Controls.Add(restore);
        buttons.Controls.Add(dismiss);
        _restoreBanner.Controls.Add(text);
        _restoreBanner.Controls.Add(buttons);
        _restoreBanner.Tag = text;
    }

    /// <summary>Puts the timers/windows column below the tiles on narrow screens (e.g. launcher snapped to half a 10" display).</summary>
    private void AdaptToWidth()
    {
        var narrow = ClientSize.Width < Ui.Scaled(760);
        _side.Dock = narrow ? DockStyle.Bottom : DockStyle.Right;
        if (narrow)
            _side.Height = Math.Min(Ui.Scaled(260), ClientSize.Height / 3);
        else
            _side.Width = Ui.Scaled(330);
        LayoutTiles();
    }

    // ------------------------------------------------------------------ side column: timers and open windows

    private void RebuildSide()
    {
        if (IsDisposed)
            return;
        _sideStack.SuspendLayout();
        foreach (Control c in _sideStack.Controls.Cast<Control>().ToList())
            c.Dispose();
        _sideStack.Controls.Clear();
        _timerCards.Clear();

        var timers = AppServices.Timers.Timers;
        _sideStack.Controls.Add(Ui.Text($"Active timers ({timers.Count})", FontRole.Heading, Brand.Navy, new Padding(0, 0, 0, 4)));
        if (timers.Count == 0)
            _sideStack.Controls.Add(Ui.Text("No timers running. Start one from a First Aid topic or the Timers tool.", fore: Brand.Muted));
        foreach (var t in timers)
            _sideStack.Controls.Add(BuildTimerCard(t));
        var timersButton = Ui.Button("Open Timers", ButtonStyle.Primary, () => AppServices.Windows.Open(TimersWindow.ToolId));
        timersButton.Margin = new Padding(0, 6, 0, 12);
        _sideStack.Controls.Add(timersButton);

        var windows = AppServices.Windows.Windows;
        _sideStack.Controls.Add(Ui.Text($"Open windows ({windows.Count})", FontRole.Heading, Brand.Navy, new Padding(0, 6, 0, 4)));
        foreach (var w in windows)
        {
            var window = w;
            var b = Ui.Button((w.Pinned ? "[Pinned] " : "") + w.Text, ButtonStyle.Secondary, () => WindowManager.BringForward(window), FontRole.Normal);
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.Margin = new Padding(0, 2, 0, 2);
            _sideStack.Controls.Add(b);
        }
        if (windows.Count > 0)
        {
            var show = Ui.Button("Show all", ButtonStyle.Secondary, AppServices.Windows.ShowAll);
            show.Margin = new Padding(0, 6, 0, 0);
            _sideStack.Controls.Add(show);
        }
        UiMode.Apply(_sideStack);
        _sideStack.ResumeLayout(true);
        UpdateAlertBanner();
    }

    private Control BuildTimerCard(FieldTimer t)
    {
        var card = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Height = Ui.Scaled(64), Margin = new Padding(0, 2, 0, 2), Padding = new Padding(8, 4, 8, 4) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var (back, fore) = t.NeedsAttention ? (Brand.Danger, Color.White) : (Brand.Navy, Color.White);
        card.BackColor = back;
        var label = new Label { Text = t.Label, Dock = DockStyle.Fill, ForeColor = fore, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false }.Styled(FontRole.Bold);
        var source = new Label { Text = t.NeedsAttention ? "Tap to acknowledge" : (t.Source.Length > 0 ? t.Source : "Started " + t.StartedLocal.ToString("HH:mm")), Dock = DockStyle.Fill, ForeColor = fore, AutoEllipsis = true, UseMnemonic = false };
        var time = new Label { Text = t.Display(AppServices.Timers.Now), AutoSize = true, ForeColor = t.NeedsAttention ? Color.White : Brand.Amber, Anchor = AnchorStyles.Right, Margin = new Padding(6, 0, 0, 0) }.Styled(FontRole.Large);
        card.Controls.Add(label, 0, 0);
        card.Controls.Add(source, 0, 1);
        card.Controls.Add(time, 1, 0);
        card.SetRowSpan(time, 2);
        card.FixedSize();
        Ui.OnTap(card, () =>
        {
            if (t.NeedsAttention)
                AppServices.Timers.Acknowledge(t);
            else
                AppServices.Windows.Open(TimersWindow.ToolId);
        });
        _timerCards[t.Id] = (time, card);
        return card;
    }

    private void UpdateTimes()
    {
        foreach (var t in AppServices.Timers.Timers)
            if (_timerCards.TryGetValue(t.Id, out var c))
            {
                var text = t.Display(AppServices.Timers.Now);
                if (c.Time.Text != text)
                    c.Time.Text = text;
            }
    }

    private void UpdateAlertBanner()
    {
        var alerting = AppServices.Timers.Timers.Where(t => t.NeedsAttention).ToList();
        _alertBanner.Visible = alerting.Count > 0;
        if (alerting.Count > 0)
            _alertBanner.Text = (alerting.Count == 1 ? $"TIMER: {alerting[0].Label}" : $"{alerting.Count} TIMERS NEED ATTENTION") + "  –  tap to acknowledge";
    }

    private void AcknowledgeAlerts()
    {
        foreach (var t in AppServices.Timers.Timers.Where(t => t.NeedsAttention).ToList())
            AppServices.Timers.Acknowledge(t);
    }

    // ------------------------------------------------------------------ session and lifetime

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        AdaptToWidth();
        RebuildSide();
        var session = AppServices.Windows.LoadSession();
        if (session == null)
            return;
        switch (AppServices.Settings.RestoreWindows)
        {
            case RestoreWindowsMode.Always:
                AppServices.Windows.Restore(session);
                break;
            case RestoreWindowsMode.Ask:
                _pendingSession = session;
                ((Label)_restoreBanner.Tag!).Text = $"Restore {session.Windows.Count} window{(session.Windows.Count == 1 ? "" : "s")} from your last session?";
                _restoreBanner.Visible = true;
                break;
        }
    }

    private void RestorePending()
    {
        _restoreBanner.Visible = false;
        if (_pendingSession != null)
            AppServices.Windows.Restore(_pendingSession);
        _pendingSession = null;
    }

    private void CloseAllWindows()
    {
        var count = AppServices.Windows.Windows.Count;
        if (count == 0)
            return;
        if (MessageBox.Show(this, $"Close all {count} tool windows?\n\nRunning timers are not affected.", Branding.ProductName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            AppServices.Windows.CloseAll();
    }

    public void BringToFrontFromOtherInstance()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    private void ShowOptions()
    {
        using var dialog = new OptionsDialog();
        dialog.ShowDialog(this);
    }

    private void RestorePlacement()
    {
        var s = AppServices.Settings;
        var area = Screen.PrimaryScreen!.WorkingArea;
        if (s.LauncherWidth > 0 && s.LauncherHeight > 0)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = WindowLayout.EnsureVisible(new Rectangle(s.LauncherX, s.LauncherY, s.LauncherWidth, s.LauncherHeight),
                Screen.AllScreens.Select(x => x.WorkingArea).ToList(), MinimumSize);
            if (s.LauncherMaximized)
                WindowState = FormWindowState.Maximized;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Math.Min(1100, area.Width), Math.Min(760, area.Height));
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var open = AppServices.Windows.Windows.Count;
        if (!_closingConfirmed && e.CloseReason == CloseReason.UserClosing && open > 0)
        {
            var answer = MessageBox.Show(this,
                $"Close the Field Toolkit and its {open} open window{(open == 1 ? "" : "s")}?\n\n" +
                "Running timers keep counting and will be shown when you open the toolkit again.",
                Branding.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        _closingConfirmed = true;
        AppServices.Windows.SaveSession();
        var s = AppServices.Settings;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        (s.LauncherX, s.LauncherY, s.LauncherWidth, s.LauncherHeight) = (b.X, b.Y, b.Width, b.Height);
        s.LauncherMaximized = WindowState == FormWindowState.Maximized;
        s.Save();
        AppServices.Windows.CloseAll();
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (UiModeButtons.HandleShortcut(keyData))
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
