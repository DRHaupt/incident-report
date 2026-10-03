using FieldMap.Core.Aprs;
using FieldMap.Views;
using Shared;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>APRS: connection status, beacon, and the stations heard, nearest first.</summary>
public sealed class AprsPanel : SidePanel
{
    private int _shownVersion = -1;
    private DateTime _shownAt;

    public AprsPanel(MapWindow map) : base(map, "APRS stations")
    {
        Build();
        AppServices.ClockTick += OnTick;
        AppServices.Aprs.StatusChanged += OnStatus;
    }

    private void OnStatus(object? sender, EventArgs e) => _shownVersion = -1;

    private void OnTick(object? sender, EventArgs e)
    {
        // Refresh at most every 5 s so the list doesn't jump about under a finger.
        if (DateTime.UtcNow - _shownAt < TimeSpan.FromSeconds(5) || AppServices.Aprs.Tracker.Version == _shownVersion)
            return;
        Build();
    }

    private void Build()
    {
        _shownVersion = AppServices.Aprs.Tracker.Version;
        _shownAt = DateTime.UtcNow;
        var aprs = AppServices.Aprs;
        var s = aprs.Settings;
        var scroll = Stack.VerticalScroll.Value;
        Stack.SuspendLayout();
        Clear();
        if (!aprs.Active)
        {
            AddText("APRS is off. Turn on internet (APRS-IS) and/or a radio TNC in APRS settings.", FontRole.Bold, Brand.Danger);
        }
        else
        {
            if (aprs.Internet is { } inet)
                AddText($"Internet (APRS-IS): {inet.StatusText}", FontRole.Bold, LinkColor(inet.State));
            if (aprs.Radio is { } radio)
                AddText($"Radio ({aprs.RadioName}): {radio.StatusText}", FontRole.Bold, LinkColor(radio.State));
        }
        AddButtons(Btn("APRS settings…", ButtonStyle.Primary, () => { using var d = new AprsSettingsDialog(); d.ShowDialog(FindForm()); Build(); }),
                   Btn("Packet log", ButtonStyle.Secondary, () => AprsLogWindow.ShowSingle(FindForm())));
        var beacon = Btn("Send my position now", ButtonStyle.Accent, async () =>
        {
            var result = await AppServices.Aprs.BeaconAsync(manual: true);
            Map.Flash(result);
            Build();
        });
        AddButtons(beacon, Btn("Clear list", ButtonStyle.Secondary, () => { AppServices.Aprs.Tracker.Clear(); Build(); }));
        var problem = aprs.BeaconProblem();
        AddText(problem != null ? $"Beacon: {problem}"
            : s.BeaconEnabled ? $"Beacon on: every {s.BeaconMinutes} min ({s.BeaconMovingMinutes} min moving) as {s.Callsign.ToUpperInvariant()}"
            : $"Beacon off (sending is allowed as {s.Callsign.ToUpperInvariant()}).", FontRole.Normal, Brand.Muted);
        if (aprs.LastBeaconResult != null)
            AddText(aprs.LastBeaconResult, FontRole.Normal, Brand.Muted);
        AddToggle("Show APRS stations on the map", s.Show, on => { s.Show = on; AppServices.Settings.Save(); Map.MapView.Invalidate(); }, "");
        AddToggle("Show tracks", s.ShowTracks, on => { s.ShowTracks = on; AppServices.Settings.Save(); Map.MapView.Invalidate(); }, "");

        var from = Map.MyPositionOrCenter;
        var stations = aprs.VisibleStations()
            .OrderByDescending(x => x.Emergency)
            .ThenBy(x => x.Point is { } p ? p.DistanceTo(from) : double.MaxValue)
            .Take(150).ToList();
        AddText($"Heard in the last {s.ShowMinutes} min ({stations.Count}{(stations.Count == 150 ? "+" : "")}), nearest {(Map.FixUsable ? "to me" : "to the map centre")} first", FontRole.Heading);
        if (stations.Count == 0)
            AddText(aprs.Active ? "No stations yet." : "–", FontRole.Normal, Brand.Muted);
        foreach (var st in stations)
        {
            var station = st;
            var p = station.Point!.Value;
            var detail = $"{GeoMath.FormatDistance(from.DistanceTo(p))} {GeoMath.CompassPoint(from.BearingTo(p))} · {Ago(station.LastHeardUtc)} · {station.ViaText}" +
                         (station.Moving && station.SpeedKmh is { } v ? $" · {v:0} km/h" : "");
            var title = (station.Emergency ? "⚠ EMERGENCY · " : "") + station.Key + " · " + station.SymbolName;
            AddRow(title, detail, () => Map.SelectAprs(station.Key, center: true), station.Emergency ? Brand.Danger : Brand.Navy);
        }
        Stack.ResumeLayout();
        Refit();
        if (scroll > 0)
            Stack.VerticalScroll.Value = Math.Min(scroll, Stack.VerticalScroll.Maximum);
    }

    private static Color LinkColor(AprsLinkState s) => s switch
    {
        AprsLinkState.Connected => Brand.Success,
        AprsLinkState.Failed => Brand.Danger,
        _ => Brand.Navy
    };

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalSeconds < 60 ? $"{Math.Max(0, d.TotalSeconds):0} s ago" : d.TotalMinutes < 120 ? $"{d.TotalMinutes:0} min ago" : $"{d.TotalHours:0.#} h ago";
    }

    public override void OnClosing()
    {
        AppServices.ClockTick -= OnTick;
        AppServices.Aprs.StatusChanged -= OnStatus;
    }
}

/// <summary>One APRS station or object: what it is, where, when heard, its status and comment, with map actions.</summary>
public sealed class AprsStationPanel : SidePanel
{
    private readonly string _key;
    private int _shownVersion = -1;

    public AprsStationPanel(MapWindow map, string key) : base(map, key)
    {
        _key = key;
        Build();
        AppServices.ClockTick += OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (AppServices.Aprs.Tracker.Version != _shownVersion)
            Build();
    }

    private void Build()
    {
        _shownVersion = AppServices.Aprs.Tracker.Version;
        var s = AppServices.Aprs.Tracker.Find(_key);
        Stack.SuspendLayout();
        Clear();
        if (s == null || s.Point is not { } p)
        {
            AddText($"{_key} is no longer in the list (not heard recently, or the object was deleted).", FontRole.Bold, Brand.Danger);
            Stack.ResumeLayout();
            Refit();
            return;
        }
        if (s.Emergency)
        {
            AddText($"⚠ EMERGENCY reported by {s.Source}. Confirm and pass to dispatch.", FontRole.Large, Brand.Danger);
            AddButton("Acknowledge alert", ButtonStyle.Secondary, () => { AppServices.Aprs.AcknowledgeEmergency(_key); Map.UpdateAprsStatus(); });
        }
        AddText(s.Key, FontRole.Large);
        AddText(s.SymbolName + (s.IsObject ? $" · object from {s.Source}" : ""), FontRole.Bold, Brand.Muted);
        if (s.MicEMessage != null && !s.Emergency)
            AddText($"Status: {s.MicEMessage}", FontRole.Bold);
        if (!string.IsNullOrWhiteSpace(s.Status))
            AddText($"\"{s.Status}\"", FontRole.Bold);
        if (s.Comment.Length > 0)
            AddText(s.Comment);
        if (s.Weather != null)
            AddText("Weather: " + s.Weather, FontRole.Bold);

        AddText($"Heard {AprsPanel.Ago(s.LastHeardUtc)} ({s.LastHeardUtc.ToLocalTime():HH:mm:ss}) by {s.ViaText}" +
                (s.PositionUtc is { } pt && pt != s.LastHeardUtc ? $" · position from {AprsPanel.Ago(pt)}" : ""));
        if (s.SpeedKmh is { } v && s.Moving)
            AddText($"Moving {v:0} km/h{(s.CourseDegrees is { } c ? " heading " + GeoMath.FormatBearing(c) : "")}");
        if (s.AltitudeMeters is { } alt)
            AddText($"Altitude {alt:0} m");
        AddText($"{CoordinateFormat.Decimal(p.Lat, p.Lon)} · MGRS {CoordinateFormat.Mgrs(p.Lat, p.Lon)}");
        if (s.Ambiguity > 0)
            AddText($"Position deliberately imprecise by the sender (±{AmbiguityText(s.Ambiguity)}).", FontRole.Bold, Brand.Danger);
        if (Map.MyPosition is { } me && Map.FixUsable)
            AddText($"{GeoMath.FormatDistance(me.DistanceTo(p))} {GeoMath.FormatBearing(me.BearingTo(p))} from my position (straight line)", FontRole.Bold);

        AddButton("Directions here", ButtonStyle.Accent, () => Map.ShowPanel(new DirectionsPanel(Map, null, p, s.Key)));
        AddButtons(Btn("Copy for dispatch", ButtonStyle.Primary, () => Map.CopyText(DispatchText(s), "Copied " + s.Key)),
                   Btn("Centre map", ButtonStyle.Secondary, () => Map.CenterOn(p)));
        AddButtons(Btn("What's here?", ButtonStyle.Secondary, () => Map.ShowWhatsHere(p, s.Key)),
                   Btn("Send to Field Toolkit", ButtonStyle.Secondary, () => Map.SendToToolkit(p, s.Key, "APRS " + s.SymbolName)));
        AddButton("All APRS stations", ButtonStyle.Secondary, () => Map.ShowPanel(new AprsPanel(Map)));

        AddText("Packet", FontRole.Heading);
        AddText($"Path: {(s.Path.Length > 0 ? s.Path : "direct")} · {s.Packets} packets · track {s.Track.Count} points", FontRole.Normal, Brand.Muted);
        var raw = Ui.Text(s.LastRaw, FontRole.Normal, Brand.Muted, new Padding(0, 3, 0, 3));
        raw.Font = new Font(FontFamily.GenericMonospace, raw.Font.Size);
        Stack.Controls.Add(raw);
        AddText("APRS is amateur radio: positions are sent by the stations themselves and may be old or wrong.", FontRole.Normal, Brand.Muted);
        Stack.ResumeLayout();
        Refit();
    }

    private static string AmbiguityText(int level) => level switch { 1 => "about 20 m", 2 => "about 200 m", 3 => "about 2 km", _ => "about 20 km" };

    public static string DispatchText(AprsStation s)
    {
        var extra = new List<string> { $"APRS: {s.SymbolName}{(s.IsObject ? $" (object from {s.Source})" : "")}", $"Heard: {s.LastHeardUtc.ToLocalTime():yyyy-MM-dd HH:mm} via {s.ViaText}" };
        if (s.Emergency)
            extra.Insert(0, "EMERGENCY reported");
        if (s.MicEMessage != null && !s.Emergency)
            extra.Add("Status: " + s.MicEMessage);
        if (!string.IsNullOrWhiteSpace(s.Status))
            extra.Add("Status text: " + s.Status);
        if (s.Comment.Length > 0)
            extra.Add("Comment: " + s.Comment);
        if (s.Moving && s.SpeedKmh is { } v)
            extra.Add($"Moving {v:0} km/h" + (s.CourseDegrees is { } c ? $" heading {c:000}" : ""));
        return Shared.Core.Geo.DispatchText.Block(new GeoFix(s.Lat!.Value, s.Lon!.Value, null, s.AltitudeMeters, s.PositionUtc ?? s.LastHeardUtc, "APRS"), s.Key, extra);
    }

    public override void OnClosing()
    {
        AppServices.ClockTick -= OnTick;
        Map.MapView.SelectedAprs = null;
        Map.MapView.Invalidate();
    }
}
