using FieldMap.Framework;
using FieldMap.Views;
using Shared;
using Shared.Core.Layout;
using Shared.Location;
using Shared.UI;

namespace FieldMap.Launcher;

/// <summary>
/// Home window: big tiles (map following GPS, incident map, drawings, data, about), map data status with the Update
/// button, GPS state, open windows, arrange commands and the Touch / Daylight / Night switches.
/// </summary>
public sealed class LauncherForm : Form
{
    private readonly FlowLayoutPanel _tiles = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
    private readonly Panel _side = new() { Dock = DockStyle.Right, Width = 340, Padding = new Padding(8) };
    private readonly FlowLayoutPanel _sideStack;
    private readonly Panel _restoreBanner = new() { Dock = DockStyle.Top, Height = 64, BackColor = Brand.Amber, Visible = false, Padding = new Padding(10, 6, 10, 6) };
    private readonly Label _sampleBanner;
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
        _sampleBanner = new Label
        {
            Dock = DockStyle.Top, Height = 34, BackColor = Brand.Danger, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true,
            Text = "SAMPLE DATA – fictitious places. Install British Columbia / Alberta under Data & updates."
        }.Styled(FontRole.Bold);
        var disclaimer = new Label { Dock = DockStyle.Top, Height = 44, BackColor = Brand.Panel, ForeColor = Brand.Navy, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Text = Branding.Disclaimer, Padding = new Padding(8, 0, 8, 0) };

        foreach (var tile in ToolRegistry.Tiles)
            _tiles.Controls.Add(BuildTile(tile));
        BuildRestoreBanner();

        Controls.Add(_tiles);
        Controls.Add(_side);
        Controls.Add(BuildArrangeBar());
        Controls.Add(disclaimer);
        Controls.Add(_restoreBanner);
        Controls.Add(_sampleBanner);
        Controls.Add(BuildHeader());

        UiMode.Track(this);
        AppServices.ShowLauncher = BringToFrontFromOtherInstance;
        AppServices.Windows.CurrentScreen = () => Screen.FromControl(this);
        AppServices.Windows.Changed += (_, _) => RebuildSide();
        AppServices.Data.Changed += (_, _) => RebuildSide();
        AppServices.DrawingsChanged += (_, _) => RebuildSide();
        AppServices.Location.Changed += (_, _) => UpdateGps();
        AppServices.PointReceived += (_, m) =>
        {
            // No map window open: open one for the point from Field Toolkit.
            if (!AppServices.Windows.Windows.OfType<MapWindow>().Any() && AppServices.Windows.Open(MapWindow.ToolId) is MapWindow map)
                map.ShowWhatsHere(new GeoPoint(m.Lat, m.Lon), $"{m.Name} (from {m.From})");
        };
        UiMode.Changed += (_, _) => { LayoutTiles(); RebuildSide(); };
        Resize += (_, _) => AdaptToWidth();
    }

    private Label? _gps;

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 76, ColumnCount = 6, RowCount = 1, BackColor = Brand.Navy, Padding = new Padding(10, 8, 10, 8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++)
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new PictureBox { Image = Branding.Logo(48), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        header.Controls.Add(new Label { Text = Branding.ProductName, ForeColor = Color.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }.Styled(FontRole.Title), 1, 0);
        Button Toggle(string text, Action click)
        {
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                BackColor = Color.FromArgb(0x41, 0x52, 0x6A), ForeColor = Color.White, Margin = new Padding(4, 0, 0, 0), Padding = new Padding(10, 0, 10, 0), Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => click();
            return b.Styled(FontRole.Bold);
        }
        var touch = Toggle("Touch Mode", () => UiMode.SetTouch(!UiMode.Touch));
        var daylight = Toggle("Daylight", () => UiMode.SetDaylight(!UiMode.Daylight));
        var night = Toggle("Night", () => UiMode.SetNight(!UiMode.Night));
        void Sync()
        {
            void Set(Button b, bool on, string text)
            {
                b.Text = on ? text + " ✓" : text;
                UiMode.SetColors(b, on ? Brand.Amber : Color.FromArgb(0x41, 0x52, 0x6A), on ? Brand.Navy : Color.White);
            }
            Set(touch, UiMode.Touch, "Touch Mode");
            Set(daylight, UiMode.Daylight, "Daylight");
            Set(night, UiMode.Night, "Night");
        }
        UiMode.Changed += (_, _) => Sync();
        Load += (_, _) => Sync();
        header.Controls.Add(touch, 2, 0);
        header.Controls.Add(daylight, 3, 0);
        header.Controls.Add(night, 4, 0);
        header.Controls.Add(Toggle("Settings", () => { using var d = new SettingsDialog(); d.ShowDialog(this); }), 5, 0);
        return header;
    }

    private static Control BuildTile(ToolRegistry.Tile tool)
    {
        var tile = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Margin = new Padding(8), Padding = new Padding(12), BackColor = Brand.Panel, BorderStyle = BorderStyle.FixedSingle };
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
        Ui.OnTap(tile, () => AppServices.Windows.Open(tool.ToolId, tool.State));
        return tile;
    }

    private void LayoutTiles()
    {
        var available = _tiles.ClientSize.Width - _tiles.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        var columns = available >= Ui.Scaled(560) ? 2 : 1;
        var width = available / columns - 16;
        _tiles.SuspendLayout();
        foreach (Control tile in _tiles.Controls)
            tile.Size = new Size(Math.Max(200, width), Ui.Scaled(120));
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

    private void AdaptToWidth()
    {
        var narrow = ClientSize.Width < Ui.Scaled(760);
        _side.Dock = narrow ? DockStyle.Bottom : DockStyle.Right;
        if (narrow)
            _side.Height = Math.Min(Ui.Scaled(280), ClientSize.Height / 3);
        else
            _side.Width = Ui.Scaled(340);
        LayoutTiles();
    }

    // ------------------------------------------------------------------ side column

    private void RebuildSide()
    {
        if (IsDisposed)
            return;
        _sampleBanner.Visible = AppServices.Data.IsSample || !AppServices.Data.HasData;
        _sideStack.SuspendLayout();
        foreach (Control c in _sideStack.Controls.Cast<Control>().ToList())
            c.Dispose();
        _sideStack.Controls.Clear();

        var data = AppServices.Data;
        _sideStack.Controls.Add(Ui.Text("Map data", FontRole.Heading, Brand.Navy, new Padding(0, 0, 0, 4)));
        if (!data.HasData)
            _sideStack.Controls.Add(Ui.Text("No map data installed.", FontRole.Bold, Brand.Danger));
        foreach (var r in data.Regions)
            _sideStack.Controls.Add(Ui.Text($"{r.Manifest.RegionName}: data {r.Manifest.DataDate?.ToLocalTime():yyyy-MM-dd} (v {r.Manifest.Version})",
                FontRole.Normal, r.Manifest.IsSample ? Brand.Danger : Brand.Navy));
        var update = Ui.Button(data.IsSample || !data.HasData ? "Install map data…" : "Update map data…", ButtonStyle.Accent, () => AppServices.Windows.Open(DataWindow.ToolId));
        update.Margin = new Padding(0, 6, 0, 10);
        _sideStack.Controls.Add(update);

        _gps = Ui.Text("", FontRole.Bold, Brand.Navy);
        _sideStack.Controls.Add(Ui.Text("Position", FontRole.Heading, Brand.Navy, new Padding(0, 6, 0, 4)));
        _sideStack.Controls.Add(_gps);
        UpdateGps();

        var closures = AppServices.Drawings.Items.Count(d => d.AffectsRouting);
        var incidents = AppServices.Drawings.Items.Count(d => d.Kind == Core.Drawings.DrawingKind.Incident && d.Active);
        if (closures + incidents > 0)
            _sideStack.Controls.Add(Ui.Text($"{incidents} incident pin{(incidents == 1 ? "" : "s")} · {closures} active road closure{(closures == 1 ? "" : "s")}", FontRole.Bold, Brand.Danger, new Padding(0, 8, 0, 0)));

        var windows = AppServices.Windows.Windows;
        _sideStack.Controls.Add(Ui.Text($"Open windows ({windows.Count})", FontRole.Heading, Brand.Navy, new Padding(0, 10, 0, 4)));
        foreach (var w in windows)
        {
            var window = w;
            var b = Ui.Button((w.Pinned ? "[Pinned] " : "") + w.Text, ButtonStyle.Secondary, () => WindowManager.BringForward(window), FontRole.Normal);
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.Margin = new Padding(0, 2, 0, 2);
            _sideStack.Controls.Add(b);
        }
        UiMode.Apply(_sideStack);
        _sideStack.ResumeLayout(true);
    }

    private void UpdateGps()
    {
        if (_gps == null || _gps.IsDisposed)
            return;
        var loc = AppServices.Location;
        _gps.Text = loc.Fix is { } f && loc.State == LocationState.Ready
            ? $"{CoordinateFormat.Decimal(f.Latitude, f.Longitude)}\nMGRS {CoordinateFormat.Mgrs(f.Latitude, f.Longitude)}\n{loc.Status}"
            : loc.Running ? loc.Status : "GPS starts when a map window is open.";
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
        if (count > 0 && MessageBox.Show(this, $"Close all {count} windows?", Branding.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            AppServices.Windows.CloseAll();
    }

    public void BringToFrontFromOtherInstance()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    private void RestorePlacement()
    {
        var s = AppServices.Settings;
        var area = Screen.PrimaryScreen!.WorkingArea;
        if (s.LauncherWidth > 0 && s.LauncherHeight > 0)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = WindowLayout.EnsureVisible(new Rectangle(s.LauncherX, s.LauncherY, s.LauncherWidth, s.LauncherHeight), Screen.AllScreens.Select(x => x.WorkingArea).ToList(), MinimumSize);
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
        if (!_closingConfirmed && e.CloseReason == CloseReason.UserClosing && open > 0 &&
            MessageBox.Show(this, $"Close Field Map and its {open} open window{(open == 1 ? "" : "s")}?\n\nDrawings and the breadcrumb trail are saved.", Branding.ProductName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        UiModeButtons.HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);
}
