using System.Globalization;
using FieldMap.Core.Build;
using FieldMap.Core.Drawings;
using FieldMap.Core.Routing;
using FieldMap.Framework;
using FieldMap.Map;
using FieldMap.Panels;
using Shared;
using Shared.Core.Interop;
using Shared.Core.Radio;
using Shared.Location;
using Shared.UI;

namespace FieldMap.Views;

/// <summary>
/// A map window: the map with big on-screen controls, a toolbar (search, layers, nearest, directions, draw, what's here),
/// a side panel, the navigation banner while navigating, and a status bar with coordinates, GPS state, data date and theme.
/// Several can be open (e.g. one following the GPS and one zoomed on the incident).
/// </summary>
public sealed class MapWindow : ToolWindow, IAcceptsState
{
    public const string ToolId = "map";
    public const string FollowState = "follow";
    public const string IncidentState = "incident";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly MapView _map = new() { Dock = DockStyle.Fill };
    private readonly Panel _sideHost = new() { Dock = DockStyle.Right, Width = 430, Visible = false };
    private readonly Splitter _splitter = new() { Dock = DockStyle.Right, Width = 6, Visible = false };
    private readonly TextBox _searchBox = new() { Width = 260 };
    private readonly Button _follow, _heading, _zoomIn, _zoomOut;
    private readonly FlowLayoutPanel _status = new() { Dock = DockStyle.Bottom, Height = 58, WrapContents = true, Padding = new Padding(6, 4, 6, 2) };
    private readonly Label _gpsLabel = StatusLabel(), _decLabel = StatusLabel(), _dmsLabel = StatusLabel(), _mgrsLabel = StatusLabel(), _utmLabel = StatusLabel(), _dataLabel = StatusLabel(), _themeLabel = StatusLabel(), _aprsLabel = StatusLabel();
    private readonly NavBanner _nav;
    private SidePanel? _panel;
    private bool _following;
    private bool _headingUp;
    private bool _locationAcquired;
    private NavigationTracker? _tracker;
    private bool _rerouting;
    private bool _voice;

    public MapWindow(string? state) : base(ToolRegistry.Find(ToolId)!)
    {
        _voice = AppServices.Settings.Voice;

        // Toolbar
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 64, WrapContents = false, AutoScroll = false, Padding = new Padding(4), BackColor = Brand.Panel };
        _searchBox.Styled(FontRole.Large);
        _searchBox.Margin = new Padding(4, 10, 4, 4);
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                RunSearch();
            }
        };
        toolbar.Controls.Add(_searchBox);
        toolbar.Controls.Add(ToolButton("Search", ButtonStyle.Primary, RunSearch));
        toolbar.Controls.Add(ToolButton("Layers", ButtonStyle.Secondary, () => ShowPanel(new LayersPanel(this))));
        toolbar.Controls.Add(ToolButton("Nearest", ButtonStyle.Secondary, () => ShowPanel(new NearestPanel(this, null, null))));
        toolbar.Controls.Add(ToolButton("Directions", ButtonStyle.Secondary, () => ShowPanel(new DirectionsPanel(this, null, null))));
        toolbar.Controls.Add(ToolButton("Draw", ButtonStyle.Secondary, () => ShowPanel(new DrawPanel(this))));
        toolbar.Controls.Add(ToolButton("What's here?", ButtonStyle.Secondary, () => ShowWhatsHere(MyPositionOrCenter, null)));
        toolbar.Controls.Add(ToolButton("More ▾", ButtonStyle.Secondary, ShowMoreMenu));

        // Overlay buttons on the map (big, for gloves).
        _zoomIn = OverlayButton("+", () => { _map.ZoomBy(1); });
        _zoomOut = OverlayButton("−", () => { _map.ZoomBy(-1); });
        _follow = OverlayButton("Me", ToggleFollow);
        _heading = OverlayButton("N↑", ToggleHeadingUp);
        foreach (var b in new[] { _zoomIn, _zoomOut, _follow, _heading })
            _map.Controls.Add(b);
        _map.Resize += (_, _) => LayoutOverlay();

        // Status bar
        foreach (var l in new[] { _gpsLabel, _aprsLabel, _decLabel, _dmsLabel, _mgrsLabel, _utmLabel, _dataLabel, _themeLabel })
            _status.Controls.Add(l);
        _aprsLabel.Cursor = Cursors.Hand;
        _aprsLabel.Click += (_, _) =>
        {
            if (AppServices.Aprs.ActiveEmergencies.FirstOrDefault() is { } key)
                SelectAprs(key, center: true);
            else if (AppServices.Aprs.Messenger.Unread > 0)
                ShowPanel(new AprsMessagesPanel(this, AppServices.Aprs.Messenger.Snapshot().LastOrDefault(m => !m.Outgoing && !m.Read && m.State == Core.Aprs.AprsMessageState.Received)?.From));
            else
                ShowPanel(new AprsPanel(this));
        };
        foreach (var l in new[] { _decLabel, _dmsLabel, _mgrsLabel, _utmLabel })
        {
            var label = l;
            label.Cursor = Cursors.Hand;
            label.Click += (_, _) => CopyText(label.Text.Contains(':') ? label.Text[(label.Text.IndexOf(':') + 1)..].Trim() : label.Text, "Copied " + label.Text);
        }
        _status.BackColor = Brand.Navy;

        _nav = new NavBanner(this) { Visible = false };

        var mapArea = new Panel { Dock = DockStyle.Fill };
        mapArea.Controls.Add(_map);
        mapArea.Controls.Add(_splitter);
        mapArea.Controls.Add(_sideHost);
        Body.Controls.Add(mapArea);
        Body.Controls.Add(_nav);
        Body.Controls.Add(toolbar);
        Body.Controls.Add(_status);

        _map.Tapped += OnMapTapped;
        _map.LongPressed += (_, e) => ShowContextMenu(e.Point, e.Screen);
        _map.UserMoved += (_, _) => SetFollowing(false);
        _map.ViewChanged += (_, _) => UpdateStatus();
        AppServices.ClockTick += OnClock;
        AppServices.PointReceived += OnPointReceived;
        AppServices.Aprs.StatusChanged += OnAprsStatus;
        AppServices.Aprs.Changed += OnAprsStatus;
        AppServices.Aprs.EmergencyHeard += OnAprsEmergency;
        AppServices.Aprs.MessageReceived += OnAprsMessage;
        AppServices.Aprs.MessagesChanged += OnAprsStatus;
        FormClosed += (_, _) =>
        {
            AppServices.ClockTick -= OnClock;
            AppServices.PointReceived -= OnPointReceived;
            AppServices.Aprs.StatusChanged -= OnAprsStatus;
            AppServices.Aprs.Changed -= OnAprsStatus;
            AppServices.Aprs.EmergencyHeard -= OnAprsEmergency;
            AppServices.Aprs.MessageReceived -= OnAprsMessage;
            AppServices.Aprs.MessagesChanged -= OnAprsStatus;
            AppServices.Location.Changed -= OnLocationChanged;
            if (_locationAcquired)
                AppServices.Location.Release();
            AppServices.Speech.Stop();
        };
        Shown += (_, _) =>
        {
            AppServices.Location.Changed += OnLocationChanged;
            AppServices.Location.Acquire();
            _locationAcquired = true;
            OnLocationChanged(null, EventArgs.Empty);
            LayoutOverlay();
            ShowStartupMessages();
        };
        ApplyState(state);
        UpdateStatus();
    }

    public MapView MapView => _map;

    // ------------------------------------------------------------------ state and restore

    public void ApplyState(string? state)
    {
        var data = AppServices.Data;
        var home = data.HasData ? data.Bounds : new GeoBounds(48.3, -139.1, 60, -110);
        switch (state)
        {
            case null or "" or FollowState:
                _map.SetView(home.Center, 12);
                if (data.HasData)
                    _map.ZoomToFit(home);
                SetFollowing(true);
                SetTitle("Map – follow GPS");
                break;
            case IncidentState:
            {
                var incident = AppServices.Drawings.Items.Where(d => d.Kind == DrawingKind.Incident && d.Active).OrderByDescending(d => d.CreatedUtc).FirstOrDefault();
                if (incident != null)
                    _map.SetView(incident.Anchor, 16, 0);
                else
                    _map.ZoomToFit(home);
                SetFollowing(false);
                SetTitle(incident != null ? $"Incident – {incident.Title}" : "Map – incident");
                break;
            }
            default:
            {
                var p = state.Split('|');
                if (p.Length >= 7 && p[0] == "v1" &&
                    double.TryParse(p[1], NumberStyles.Float, Inv, out var lat) && double.TryParse(p[2], NumberStyles.Float, Inv, out var lon) &&
                    double.TryParse(p[3], NumberStyles.Float, Inv, out var zoom) && double.TryParse(p[4], NumberStyles.Float, Inv, out var bearing))
                {
                    _map.SetView(new GeoPoint(lat, lon), zoom, bearing);
                    _headingUp = p[6] == "1";
                    SetFollowing(p[5] == "1");
                    if (p.Length >= 8 && p[7].Length > 0)
                        SetTitle(p[7]);
                }
                else
                {
                    _map.ZoomToFit(home);
                }
                break;
            }
        }
        UpdateHeadingButton();
    }

    public override string SaveState()
    {
        var c = _map.Center;
        return string.Create(Inv, $"v1|{c.Lat:0.######}|{c.Lon:0.######}|{_map.Zoom:0.##}|{_map.Bearing:0.#}|{(_following ? 1 : 0)}|{(_headingUp ? 1 : 0)}|{Text.Replace("|", "/").Replace(" – " + Branding.ProductName, "")}");
    }

    protected override string? NewWindowState()
    {
        var c = _map.Center;
        return string.Create(Inv, $"v1|{c.Lat:0.######}|{c.Lon:0.######}|{_map.Zoom:0.##}|0|0|0|Map");
    }

    protected override void OnMapDataChanged()
    {
        ClearRoute();
        UpdateStatus();
        if (_panel is LayersPanel)
            ShowPanel(new LayersPanel(this));
    }

    private void ShowStartupMessages()
    {
        if (AppServices.StartupMessages.Count == 0 || AppServices.Windows.Windows.Count > 1)
            return;
        var text = string.Join("\n\n", AppServices.StartupMessages);
        AppServices.StartupMessages.Clear();
        MessageBox.Show(this, text, Branding.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ------------------------------------------------------------------ toolbar and overlay

    private Button ToolButton(string text, ButtonStyle style, Action click)
    {
        var b = Ui.Button(text, style, click);
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(10, 0, 10, 0);
        b.Height = Ui.TargetHeight;
        b.Margin = new Padding(3, 2, 3, 2);
        return b;
    }

    private static Button OverlayButton(string text, Action click)
    {
        var b = Ui.Button(text, ButtonStyle.Primary, click, FontRole.Large);
        b.Size = new Size(64, 64);
        b.Cursor = Cursors.Hand;
        b.TabStop = false;
        return b;
    }

    private void LayoutOverlay()
    {
        var size = (int)(64 * (UiMode.Touch ? 1.25 : 1) * DeviceDpi / 96f);
        var gap = size / 6;
        var x = _map.ClientSize.Width - size - gap * 2;
        var buttons = new[] { _zoomIn, _zoomOut, _follow, _heading };
        var total = buttons.Length * size + (buttons.Length - 1) * gap;
        var y = Math.Max(gap, (_map.ClientSize.Height - total) / 2);
        foreach (var b in buttons)
        {
            b.Bounds = new Rectangle(x, y, size, size);
            b.BringToFront();
            y += size + gap;
        }
    }

    private void ShowMoreMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Export map as PNG…", null, (_, _) => Snapshot.ExportPng(this, _map));
        menu.Items.Add("Export map as PDF…", null, (_, _) => Snapshot.ExportPdf(this, _map));
        menu.Items.Add("Print map…", null, (_, _) => Snapshot.Print(this, _map));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy map centre for dispatch", null, (_, _) => CopyText(DispatchText.Block(new GeoFix(_map.Center.Lat, _map.Center.Lon, null, null, DateTime.UtcNow, "Map centre"), "Map centre"), "Map centre copied"));
        menu.Items.Add("Position source…", null, (_, _) => { using var d = new SourceDialog(AppServices.Location); d.ShowDialog(this); });
        menu.Items.Add("Export breadcrumb trail (GPX)…", null, (_, _) => ExportTrail());
        menu.Items.Add("Clear breadcrumb trail", null, (_, _) =>
        {
            if (MessageBox.Show(this, "Clear the breadcrumb trail?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                AppServices.Breadcrumbs.Clear();
        });
        menu.Items.Add(new ToolStripSeparator());
        var theme = new ToolStripMenuItem("Theme");
        foreach (var t in Enum.GetValues<UiTheme>())
        {
            var value = t;
            theme.DropDownItems.Add(new ToolStripMenuItem(t + (t == UiTheme.Daylight ? " (Ctrl+D)" : t == UiTheme.Night ? " (Ctrl+N)" : ""), null, (_, _) => UiMode.SetTheme(value)) { Checked = UiMode.Theme == t });
        }
        menu.Items.Add(theme);
        menu.Items.Add(new ToolStripMenuItem("Touch Mode (Ctrl+T)", null, (_, _) => UiMode.SetTouch(!UiMode.Touch)) { Checked = UiMode.Touch });
        menu.Items.Add(new ToolStripMenuItem("Spoken directions", null, (_, _) => { _voice = !_voice; AppServices.Settings.Voice = _voice; AppServices.Settings.Save(); }) { Checked = _voice });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("APRS stations", null, (_, _) => ShowPanel(new AprsPanel(this)));
        menu.Items.Add("APRS messages", null, (_, _) => ShowPanel(new AprsMessagesPanel(this)));
        menu.Items.Add("APRS settings…", null, (_, _) => { using var d = new AprsSettingsDialog(); d.ShowDialog(this); });
        menu.Items.Add("Incidents & drawings", null, (_, _) => AppServices.Windows.Open(DrawingsWindow.ToolId));
        menu.Items.Add("Data & updates", null, (_, _) => AppServices.Windows.Open(DataWindow.ToolId));
        menu.Items.Add("About & data sources", null, (_, _) => AppServices.Windows.Open(AboutWindow.ToolId));
        UiMode.Apply(this);
        foreach (ToolStripItem item in menu.Items)
            item.Padding = UiMode.Touch ? new Padding(8, 10, 8, 10) : item.Padding;
        menu.Font = UiMode.GetFont(UiMode.Touch ? FontRole.Large : FontRole.Normal);
        menu.Show(Cursor.Position);
    }

    private void ExportTrail()
    {
        using var d = new SaveFileDialog { Filter = "GPX track (*.gpx)|*.gpx", FileName = $"breadcrumbs-{DateTime.Now:yyyyMMdd-HHmm}.gpx" };
        if (d.ShowDialog(this) != DialogResult.OK)
            return;
        var crumbs = AppServices.Breadcrumbs.Items;
        File.WriteAllText(d.FileName, DrawingFormats.ToGpx(Array.Empty<Drawing>(), new[]
        {
            ("Breadcrumbs", (IReadOnlyList<GeoPoint>)crumbs.Select(c => new GeoPoint(c.Lat, c.Lon)).ToList(), (IReadOnlyList<DateTime>?)crumbs.Select(c => c.Utc).ToList())
        }));
    }

    // ------------------------------------------------------------------ side panel

    public void ShowPanel(SidePanel panel)
    {
        ClosePanel(keepVisible: true);
        _panel = panel;
        _sideHost.Width = (int)(430 * UiMode.Scale * DeviceDpi / 96f);
        if (_sideHost.Width > ClientSize.Width * 0.6)
            _sideHost.Width = (int)(ClientSize.Width * 0.6);
        _sideHost.Controls.Add(panel);
        _sideHost.Visible = _splitter.Visible = true;
        UiMode.Apply(panel);
        LayoutOverlay();
    }

    public void ClosePanel() => ClosePanel(false);

    private void ClosePanel(bool keepVisible)
    {
        if (_panel != null)
        {
            _panel.OnClosing();
            _sideHost.Controls.Remove(_panel);
            _panel.Dispose();
            _panel = null;
        }
        if (!keepVisible)
        {
            _sideHost.Visible = _splitter.Visible = false;
            _map.Draft = null;
            _map.Invalidate();
        }
    }

    public SidePanel? CurrentPanel => _panel;

    // ------------------------------------------------------------------ map interaction

    /// <summary>When set (by the drawing tools), taps on the map go here instead of selecting things.</summary>
    public Action<GeoPoint>? TapHandler { get; set; }

    private void OnMapTapped(object? sender, MapPointEventArgs e)
    {
        if (TapHandler != null)
        {
            TapHandler(e.Point);
            return;
        }
        if (_map.HitAprs(e.Screen) is { } aprs)
        {
            SelectAprs(aprs, center: false);
            return;
        }
        if (_map.HitPoi(e.Screen) is { } poi)
        {
            SelectPoi(poi);
            return;
        }
        if (_map.HitDrawing(e.Screen) is { } drawing)
        {
            SelectDrawing(drawing);
            return;
        }
        if (_map.SelectedPoi != null || _map.SelectedDrawing != null || _map.SelectedAprs != null)
        {
            _map.SelectedPoi = null;
            _map.SelectedDrawing = null;
            _map.SelectedAprs = null;
            _map.Invalidate();
        }
    }

    /// <summary>Selects an APRS station and shows its details (optionally moving the map to it).</summary>
    public void SelectAprs(string key, bool center)
    {
        _map.SelectedAprs = key;
        _map.SelectedPoi = null;
        _map.SelectedDrawing = null;
        if (center && AppServices.Aprs.Tracker.Find(key)?.Point is { } p)
            CenterOn(p);
        _map.Invalidate();
        ShowPanel(new AprsStationPanel(this, key));
    }

    public void CenterOn(GeoPoint p)
    {
        SetFollowing(false);
        _map.Center = p;
    }

    public void SelectPoi(Poi poi)
    {
        _map.SelectedPoi = poi;
        _map.SelectedDrawing = null;
        _map.SelectedAprs = null;
        _map.Invalidate();
        ShowPanel(new PoiDetailsPanel(this, poi));
    }

    public void SelectDrawing(Drawing d)
    {
        _map.SelectedDrawing = d.Id;
        _map.SelectedPoi = null;
        _map.Invalidate();
        ShowPanel(new DrawingDetailsPanel(this, d));
    }

    private void ShowContextMenu(GeoPoint p, Point screen)
    {
        if (TapHandler != null)
            return;
        var menu = new ContextMenuStrip { Font = UiMode.GetFont(UiMode.Touch ? FontRole.Large : FontRole.Bold) };
        void Item(string text, Action a) => menu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => a()) { Padding = new Padding(10, 12, 10, 12) });
        Item("What's here?", () => ShowWhatsHere(p, null));
        Item("Directions to here", () => ShowPanel(new DirectionsPanel(this, null, p)));
        Item("Directions from here", () => ShowPanel(new DirectionsPanel(this, p, null)));
        Item("Nearest facilities from here…", () => ShowPanel(new NearestPanel(this, p, null)));
        Item("Drop incident pin here", () => DrawPanel.QuickAdd(this, DrawingKind.Incident, p));
        Item("Range rings here", () => DrawPanel.QuickAdd(this, DrawingKind.RangeRings, p));
        Item("Send to Field Toolkit", () => SendToToolkit(p, "Map point"));
        Item("Copy coordinates", () => CopyText(DispatchText.Block(new GeoFix(p.Lat, p.Lon, null, null, DateTime.UtcNow, "Map point"), "Map point"), "Location copied for dispatch"));
        UiMode.Apply(this);
        menu.Show(_map, screen);
    }

    public void ShowWhatsHere(GeoPoint p, string? label)
    {
        _map.Marker = p;
        _map.Invalidate();
        ShowPanel(new WhatsHerePanel(this, p, label));
    }

    public void ClearMarker()
    {
        _map.Marker = null;
        _map.Invalidate();
    }

    public void CenterOn(GeoPoint p, double? zoom = null)
    {
        SetFollowing(false);
        _map.SetView(p, zoom ?? Math.Max(_map.Zoom, 15));
    }

    private void RunSearch()
    {
        var q = _searchBox.Text.Trim();
        if (q.Length == 0)
        {
            _searchBox.Focus();
            return;
        }
        ShowPanel(new SearchPanel(this, q));
    }

    public void SendToToolkit(GeoPoint p, string name, string note = "")
    {
        try
        {
            Exchange.Send(Exchange.Inbox(Exchange.ToFieldToolkit), new ExchangeMessage { Name = name, Lat = p.Lat, Lon = p.Lon, Note = note, From = Branding.ProductName });
            Flash($"Sent \"{name}\" to Field Toolkit (Location → saved points).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Could not hand the point to Field Toolkit: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnPointReceived(object? sender, ExchangeMessage m)
    {
        // The most recently opened map window shows points sent from Field Toolkit.
        if (AppServices.Windows.Windows.OfType<MapWindow>().LastOrDefault() != this)
            return;
        WindowManager.BringForward(this);
        var p = new GeoPoint(m.Lat, m.Lon);
        CenterOn(p, 16);
        ShowWhatsHere(p, $"{m.Name} (from {m.From})");
    }

    // ------------------------------------------------------------------ GPS, follow, heading

    public GeoFix? Fix => AppServices.Location.Fix;

    public bool FixUsable => Fix != null && AppServices.Location.State == LocationState.Ready && DateTime.UtcNow - Fix.TimestampUtc < LocationService.StaleAfter * 4;

    public GeoPoint? MyPosition => Fix is { } f ? new GeoPoint(f.Latitude, f.Longitude) : null;

    public GeoPoint MyPositionOrCenter => FixUsable && MyPosition is { } p ? p : _map.Center;

    private void ToggleFollow()
    {
        if (Fix == null)
        {
            MessageBox.Show(this, "No GPS position yet.\n\n" + AppServices.Location.Status, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SetFollowing(true);
        if (MyPosition is { } p)
            _map.SetView(p, Math.Max(_map.Zoom, 15));
    }

    private void SetFollowing(bool on)
    {
        _following = on;
        Ui.SetSelected(_follow, on, ButtonStyle.Accent, ButtonStyle.Primary);
        _follow.Text = on ? "Me ✓" : "Me";
        if (!on && _headingUp)
        {
            _headingUp = false;
            UpdateHeadingButton();
        }
    }

    private void ToggleHeadingUp()
    {
        _headingUp = !_headingUp;
        if (_headingUp)
            SetFollowing(true);
        else
            _map.Bearing = 0;
        UpdateHeadingButton();
        OnLocationChanged(null, EventArgs.Empty);
    }

    private void UpdateHeadingButton()
    {
        _heading.Text = _headingUp ? "H↑" : "N↑";
        Ui.SetSelected(_heading, _headingUp, ButtonStyle.Accent, ButtonStyle.Primary);
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;
        var fix = Fix;
        _map.Fix = fix;
        _map.FixStale = fix != null && DateTime.UtcNow - fix.TimestampUtc > LocationService.StaleAfter;
        if (fix != null && _following)
        {
            var p = new GeoPoint(fix.Latitude, fix.Longitude);
            if (_headingUp && fix.CourseDegrees is { } course && (fix.SpeedMetersPerSecond ?? 0) > 2)
                _map.SetView(p, _map.Zoom, course);
            else
                _map.Center = p;
        }
        if (fix != null && _tracker != null)
            UpdateNavigation(fix);
        _map.Invalidate();
        UpdateStatus();
        _panel?.OnFix(fix!);
    }

    private void OnClock(object? sender, EventArgs e)
    {
        var fix = Fix;
        var stale = fix != null && DateTime.UtcNow - fix.TimestampUtc > LocationService.StaleAfter;
        if (stale != _map.FixStale)
        {
            _map.FixStale = stale;
            _map.Invalidate();
            UpdateStatus();
        }
    }

    // ------------------------------------------------------------------ routes and navigation

    public Route? Route => _map.Route;

    public void ShowRoute(Route route, IReadOnlyList<Route>? alternatives = null, bool fit = true)
    {
        _map.Route = route;
        _map.Alternatives = alternatives?.Where(a => a != route).ToList() ?? new List<Route>();
        if (fit)
        {
            SetFollowing(false);
            _map.ZoomToFit(GeoBounds.Of(route.Geometry).Expand(150));
        }
        _map.Invalidate();
    }

    public void ClearRoute()
    {
        StopNavigation();
        _map.Route = null;
        _map.Alternatives = Array.Empty<Route>();
        _map.Invalidate();
    }

    public bool Navigating => _tracker != null;

    public void StartNavigation(Route route)
    {
        ShowRoute(route, null, fit: false);
        _tracker = new NavigationTracker(route);
        _nav.Visible = true;
        SetFollowing(true);
        if (MyPosition is { } p)
            _map.SetView(p, Math.Max(_map.Zoom, 16));
        _nav.Update(_tracker, AppServices.Location.Fix == null ? "Waiting for GPS…" : null);
        if (_voice && route.Steps.Count > 0)
            AppServices.Speech.Say(route.Steps[0].Instruction);
    }

    public void StopNavigation()
    {
        _tracker = null;
        _nav.Visible = false;
        AppServices.Speech.Stop();
    }

    public void ToggleVoice()
    {
        _voice = !_voice;
        AppServices.Settings.Voice = _voice;
        AppServices.Settings.Save();
        if (!_voice)
            AppServices.Speech.Stop();
        if (_tracker != null)
            _nav.Update(_tracker, null);
    }

    public bool Voice => _voice;

    private void UpdateNavigation(GeoFix fix)
    {
        if (_tracker == null)
            return;
        var prompt = _tracker.Update(new GeoPoint(fix.Latitude, fix.Longitude), fix.AccuracyMeters, fix.SpeedMetersPerSecond);
        if (prompt != null && _voice)
            AppServices.Speech.Say(prompt);
        _nav.Update(_tracker, null);
        if (_tracker.OffRoute && !_rerouting)
            _ = RerouteAsync(fix);
    }

    private async Task RerouteAsync(GeoFix fix)
    {
        if (_tracker == null)
            return;
        _rerouting = true;
        _nav.Update(_tracker, "Off route – recalculating…");
        if (_voice)
            AppServices.Speech.Say("Recalculating.");
        try
        {
            var from = new GeoPoint(fix.Latitude, fix.Longitude);
            var to = _tracker.Route.End;
            var profile = _tracker.Route.Profile;
            var route = await AppServices.Routing.RunAsync(from, to, r => r.Route(from, to, profile));
            if (route != null && _tracker != null && !IsDisposed)
            {
                _tracker.Replace(route);
                _map.Route = route;
                _map.Invalidate();
                _nav.Update(_tracker, null);
            }
        }
        catch (Services.RoutingException ex)
        {
            _nav.Update(_tracker!, ex.Message);
        }
        finally
        {
            _rerouting = false;
        }
    }

    // ------------------------------------------------------------------ status bar

    private static Label StatusLabel() => new Label { AutoSize = true, ForeColor = Color.White, Margin = new Padding(4, 2, 14, 2), UseMnemonic = false }.Styled(FontRole.Bold);

    private void UpdateStatus()
    {
        var loc = AppServices.Location;
        var fix = Fix;
        var stale = fix != null && DateTime.UtcNow - fix.TimestampUtc > LocationService.StaleAfter;
        var (gpsText, gpsColor) = loc.State switch
        {
            LocationState.Ready when fix != null && !stale => ($"GPS ✓ {(fix.AccuracyMeters is { } a ? $"±{a:0} m" : "")}{(fix.Source.StartsWith("Entered") ? " (manual)" : "")}", Brand.Success),
            LocationState.Ready when stale => ($"GPS stale ({(DateTime.UtcNow - fix!.TimestampUtc).TotalSeconds:0} s)", Brand.Amber),
            LocationState.Error => ("GPS: " + ShortStatus(loc.Status), Brand.Danger),
            _ => ("GPS: waiting…", Brand.Amber)
        };
        _gpsLabel.Text = gpsText;
        UiMode.SetColors(_gpsLabel, gpsColor, gpsColor == Brand.Amber ? Brand.Navy : Color.White);
        var showGps = _following && fix != null;
        var p = showGps ? new GeoPoint(fix!.Latitude, fix.Longitude) : _map.Center;
        var prefix = showGps ? "" : "Centre ";
        _decLabel.Text = prefix + CoordinateFormat.Decimal(p.Lat, p.Lon);
        _dmsLabel.Text = DispatchText.Ascii(CoordinateFormat.Dms(p.Lat, p.Lon));
        _mgrsLabel.Text = "MGRS " + CoordinateFormat.Mgrs(p.Lat, p.Lon);
        _utmLabel.Text = "UTM " + CoordinateFormat.Utm(p.Lat, p.Lon);
        var data = AppServices.Data;
        _dataLabel.Text = !data.HasData ? "No map data" : data.IsSample ? "SAMPLE DATA" : $"Data {data.DataDate:yyyy-MM-dd}";
        _themeLabel.Text = $"Theme: {UiMode.ThemeName}";
        UpdateAprsStatus();
    }

    // ------------------------------------------------------------------ APRS

    private void OnAprsStatus(object? sender, EventArgs e) => UpdateAprsStatus();

    public void UpdateAprsStatus()
    {
        var aprs = AppServices.Aprs;
        var emergency = aprs.ActiveEmergencies.FirstOrDefault();
        _aprsLabel.Visible = aprs.Active || emergency != null;
        if (emergency != null)
        {
            _aprsLabel.Text = $"⚠ APRS EMERGENCY: {emergency}";
            UiMode.SetColors(_aprsLabel, Brand.Danger, Color.White);
        }
        else
        {
            _aprsLabel.Text = aprs.ShortStatus();
            var color = aprs.Messenger.Unread > 0 || aprs.AnyLinkFailed ? Brand.Amber : Brand.Navy;
            UiMode.SetColors(_aprsLabel, color, color == Brand.Amber ? Brand.Navy : Color.White);
        }
    }

    private bool FirstMapWindow => AppServices.Windows.Windows.OfType<MapWindow>().FirstOrDefault(w => !w.IsDisposed) == this;

    private void OnAprsMessage(object? sender, Core.Aprs.AprsMessage m)
    {
        UpdateAprsStatus();
        if (!FirstMapWindow)
            return;
        Flash($"✉ APRS message from {m.From}: {m.Text}");
        if (_voice)
            AppServices.Speech.Say($"A P R S message from {Phonetic.Spell(m.From)}.");
    }

    private void OnAprsEmergency(object? sender, Core.Aprs.AprsStation station)
    {
        UpdateAprsStatus();
        _map.Invalidate();
        // Speak once, from the first map window.
        if (_voice && FirstMapWindow)
            AppServices.Speech.Say($"A P R S emergency from {Phonetic.Spell(station.Key)}.");
    }

    private static string ShortStatus(string s) => s.Length > 40 ? s[..40] + "…" : s;

    protected override void OnUiModeChanged(object? sender, EventArgs e)
    {
        LayoutOverlay();
        UpdateStatus();
        _map.Invalidate();
    }

    // ------------------------------------------------------------------ helpers

    public void CopyText(string text, string confirmation)
    {
        try
        {
            Clipboard.SetText(text);
            Flash("✓ " + confirmation);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Try again.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Shows a short confirmation in the status bar.</summary>
    public void Flash(string message)
    {
        _themeLabel.Text = message;
        var t = new System.Windows.Forms.Timer { Interval = 4000 };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            if (!IsDisposed)
                UpdateStatus();
        };
        t.Start();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F))
        {
            _searchBox.Focus();
            _searchBox.SelectAll();
            return true;
        }
        if (keyData == Keys.Escape && _panel != null && !_searchBox.Focused)
        {
            ClosePanel();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

/// <summary>The large next-turn banner shown while navigating.</summary>
internal sealed class NavBanner : TableLayoutPanel
{
    private readonly Label _turn = new() { Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
    private readonly Label _distance = new() { Dock = DockStyle.Fill, ForeColor = Brand.Amber, TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false };
    private readonly Label _remaining = new() { Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
    private readonly Button _voice;

    public NavBanner(MapWindow map)
    {
        Dock = DockStyle.Top;
        Height = 120;
        ColumnCount = 4;
        RowCount = 2;
        BackColor = Brand.Navy;
        Padding = new Padding(8, 4, 8, 4);
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        _distance.Styled(FontRole.Display);
        _turn.Styled(FontRole.Large);
        _remaining.Styled(FontRole.Bold);
        Controls.Add(_distance, 0, 0);
        SetRowSpan(_distance, 2);
        Controls.Add(_turn, 1, 0);
        Controls.Add(_remaining, 1, 1);
        _voice = Ui.Button("Voice on", ButtonStyle.Secondary, () => map.ToggleVoice());
        var end = Ui.Button("End", ButtonStyle.Danger, () => map.ClearRoute());
        _voice.Dock = end.Dock = DockStyle.Fill;
        Controls.Add(_voice, 2, 0);
        SetRowSpan(_voice, 2);
        Controls.Add(end, 3, 0);
        SetRowSpan(end, 2);
        this.FixedSize();
        _map = map;
    }

    private readonly MapWindow _map;

    public void Update(NavigationTracker t, string? message)
    {
        var next = t.NextStep;
        _distance.Text = t.Arrived ? "✓" : Instructions.FormatDistance(t.DistanceToNextStep);
        _turn.Text = message ?? (t.Arrived ? "You have arrived" : next?.Instruction ?? "Continue");
        _remaining.Text = $"{Instructions.FormatDistance(t.RemainingMeters)} to go · about {Instructions.FormatDuration(t.RemainingSeconds)} · arrive {DateTime.Now.AddSeconds(t.RemainingSeconds):HH:mm}" +
                          (t.OffRoute ? " · OFF ROUTE" : "");
        _voice.Text = _map.Voice ? "Voice on" : "Voice off";
    }
}
