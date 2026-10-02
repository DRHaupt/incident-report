using FieldMap.Core.Drawings;
using FieldMap.Core.Routing;
using FieldMap.Services;
using Shared;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>Offline directions: vehicle profile, closures, alternatives, step list, copy/export and turn-by-turn.</summary>
public sealed class DirectionsPanel : SidePanel
{
    private GeoPoint? _from;
    private GeoPoint? _to;
    private string? _toLabel;
    private List<Route> _routes = new();
    private int _selected;
    private string? _error;
    private bool _busy;

    public DirectionsPanel(Views.MapWindow map, GeoPoint? from, GeoPoint? to, string? toLabel = null) : base(map, "Directions")
    {
        _from = from;
        _to = to;
        _toLabel = toLabel;
        Build();
        if (_to != null)
            _ = CalculateAsync();
    }

    private GeoPoint From => _from ?? Map.MyPositionOrCenter;

    private void Build()
    {
        Clear();
        AddText("From: " + (_from != null ? CoordinateFormat.Decimal(_from.Value.Lat, _from.Value.Lon) : Map.FixUsable ? "My position (GPS)" : "Map centre (no GPS fix)"), FontRole.Bold);
        AddButtons(Btn("Use my position", ButtonStyle.Secondary, () => { _from = null; Recalculate(); }),
                   Btn("Pick start on map", ButtonStyle.Secondary, () => Pick(p => _from = p)));
        AddText("To: " + (_to == null ? "not set – pick on the map, or use \"Directions to here\" from a search result" : _toLabel ?? CoordinateFormat.Decimal(_to.Value.Lat, _to.Value.Lon)), FontRole.Bold);
        AddButton("Pick destination on map", ButtonStyle.Secondary, () => Pick(p => { _to = p; _toLabel = null; }));

        var s = AppServices.Settings;
        AddButtons(Profile("Car"), Profile("Emergency"), Profile("Truck"), Profile("Walking"));
        if (s.Profile == "Truck")
            AddButton($"Truck: {s.TruckHeight:0.##} m high, {s.TruckWeight:0.#} t, {s.TruckLength:0.#} m long – change", ButtonStyle.Secondary, EditTruck);
        var closures = AppServices.Drawings.Items.Count(d => d.AffectsRouting);
        AddButton((s.AvoidClosures ? "✓ Avoiding" : "Not avoiding") + $" drawn road closures ({closures})", ButtonStyle.Secondary, () =>
        {
            s.AvoidClosures = !s.AvoidClosures;
            s.Save();
            Recalculate();
        });

        if (_busy)
            AddText("Calculating…", FontRole.Bold);
        if (_error != null)
            AddText(_error, FontRole.Bold, Brand.Danger);
        if (_routes.Count > 0)
        {
            var route = _routes[_selected];
            for (var i = 0; i < _routes.Count; i++)
            {
                var index = i;
                var r = _routes[i];
                var title = (i == 0 ? "Fastest" : $"Alternative {i}") + $": {Instructions.FormatDuration(r.DurationSeconds)}, {Instructions.FormatDistance(r.DistanceMeters)}";
                AddRow((i == _selected ? "▶ " : "") + title, i == _selected ? "Shown on the map" : "Tap to show this route", () => { _selected = index; ShowSelected(); Build(); },
                    i == _selected ? Brand.Amber : Brand.Muted);
            }
            foreach (var w in route.Warnings)
                AddText("⚠ " + w, FontRole.Bold, Brand.Danger);
            AddButton(Map.Navigating ? "Navigating – restart" : "Start navigation", ButtonStyle.Accent, () => Map.StartNavigation(route), FontRole.Heading);
            AddButtons(Btn("Copy route", ButtonStyle.Primary, () => Map.CopyText(Instructions.ToText(route, _from == null ? "My position" : null, _toLabel), "Route copied")),
                       Btn("Export GPX", ButtonStyle.Secondary, () => ExportGpx(route)),
                       Btn("Clear", ButtonStyle.Secondary, () => { Map.ClearRoute(); _routes.Clear(); Build(); }));
            AddText("Steps", FontRole.Heading);
            var n = 1;
            foreach (var step in route.Steps)
            {
                var st = step;
                AddRow($"{n++}. {step.Instruction}", step.Maneuver == Maneuver.Arrive ? "" : $"{Instructions.FormatDistance(step.DistanceMeters)} · {Instructions.FormatDuration(step.DurationSeconds)}",
                    () => Map.MapView.SetView(st.Location, Math.Max(Map.MapView.Zoom, 16)));
            }
        }
        Refit();
    }

    private Button Profile(string name)
    {
        var current = AppServices.Settings.Profile == name || (name == "Car" && AppServices.Settings.Profile is not ("Emergency" or "Truck" or "Walking"));
        return Btn(name, current ? ButtonStyle.Primary : ButtonStyle.Secondary, () =>
        {
            AppServices.Settings.Profile = name;
            AppServices.Settings.Save();
            Recalculate();
        });
    }

    private void EditTruck()
    {
        var s = AppServices.Settings;
        var text = InputDialog.Ask(this, "Truck dimensions", "Height (m), weight (t), length (m):", $"{s.TruckHeight:0.##}, {s.TruckWeight:0.#}, {s.TruckLength:0.#}");
        if (text == null)
            return;
        var parts = text.Split(',', ';', ' ').Where(p => p.Length > 0).Select(p => double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
        if (parts.Length != 3 || parts.Any(double.IsNaN) || parts[0] is < 1.5 or > 6 || parts[1] is < 1 or > 140 || parts[2] is < 3 or > 50)
        {
            MessageBox.Show(this, "Enter three numbers, e.g. \"4.15, 39.5, 23\".", "Truck dimensions", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        (s.TruckHeight, s.TruckWeight, s.TruckLength) = (parts[0], parts[1], parts[2]);
        s.Save();
        Recalculate();
    }

    private void Pick(Action<GeoPoint> set)
    {
        Map.Flash("Tap the map to choose the point");
        Map.TapHandler = p =>
        {
            Map.TapHandler = null;
            set(p);
            Recalculate();
        };
    }

    private void Recalculate()
    {
        Build();
        if (_to != null)
            _ = CalculateAsync();
    }

    private async Task CalculateAsync()
    {
        if (_to is not { } to)
            return;
        _busy = true;
        _error = null;
        Build();
        var from = From;
        var profile = RoutingService.ProfileFromSettings(AppServices.Settings);
        try
        {
            var routes = await AppServices.Routing.RunAsync(from, to, r => r.RouteWithAlternatives(from, to, profile, 2), AppServices.Settings.AvoidClosures);
            if (IsDisposed)
                return;
            _routes = routes;
            _selected = 0;
            if (routes.Count == 0)
                _error = $"No route found for a {profile.Name.ToLowerInvariant()}. The destination may be unreachable (closed roads, gates, height or weight limits) or too far from a road.";
            else
                ShowSelected();
        }
        catch (RoutingException ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
        }
        if (!IsDisposed)
            Build();
    }

    private void ShowSelected()
    {
        if (_routes.Count == 0)
            return;
        Map.ShowRoute(_routes[_selected], _routes, fit: !Map.Navigating);
    }

    private void ExportGpx(Route route)
    {
        using var d = new SaveFileDialog { Filter = "GPX (*.gpx)|*.gpx", FileName = $"route-{DateTime.Now:yyyyMMdd-HHmm}.gpx" };
        if (d.ShowDialog(this) == DialogResult.OK)
            File.WriteAllText(d.FileName, DrawingFormats.RouteToGpx(route, _toLabel ?? "Route"));
    }

    public override void OnClosing()
    {
        if (Map.TapHandler != null)
            Map.TapHandler = null;
        if (!Map.Navigating)
            Map.ClearRoute();
    }
}
