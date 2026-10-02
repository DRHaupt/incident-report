using FieldMap.Core.Build;
using FieldMap.Core.Layers;
using FieldMap.Core.Routing;
using FieldMap.Services;
using Shared;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>The N nearest facilities of a kind, sorted by drive time (with walking time for AEDs).</summary>
public sealed class NearestPanel : SidePanel
{
    private static readonly string[] QuickLayers =
    {
        LayerCatalog.Aed, LayerCatalog.Hospital, LayerCatalog.Fire, LayerCatalog.Police, LayerCatalog.Ambulance,
        LayerCatalog.Air, LayerCatalog.Pharmacy, LayerCatalog.Fuel, LayerCatalog.Water, LayerCatalog.Shelter
    };

    private readonly GeoPoint? _fixedOrigin;
    private string? _layer;
    private bool _edOnly = true;
    private int _count = 5;
    private CancellationTokenSource? _cancel;

    public NearestPanel(Views.MapWindow map, GeoPoint? origin, string? layer) : base(map, "Nearest")
    {
        _fixedOrigin = origin;
        _layer = layer;
        Build(null);
        if (_layer != null)
            _ = ComputeAsync();
    }

    private GeoPoint Origin => _fixedOrigin ?? Map.MyPositionOrCenter;

    private string OriginLabel => _fixedOrigin != null ? "the chosen point" : Map.FixUsable ? "my position (GPS)" : "the map centre (no GPS fix)";

    private void Build(Action? results)
    {
        Clear();
        AddText($"From {OriginLabel}", FontRole.Bold);
        // Layer buttons, two per row.
        for (var i = 0; i < QuickLayers.Length; i += 2)
        {
            var buttons = QuickLayers.Skip(i).Take(2).Select(id =>
            {
                var l = LayerCatalog.Find(id)!;
                var b = Btn(id == LayerCatalog.Hospital ? "Hospitals (ED)" : l.Title, id == _layer ? ButtonStyle.Primary : ButtonStyle.Secondary, () =>
                {
                    _layer = id;
                    _ = ComputeAsync();
                });
                return b;
            }).ToArray();
            AddButtons(buttons);
        }
        var profile = RoutingService.ProfileFromSettings(AppServices.Settings);
        AddButtons(ProfileButton("Car"), ProfileButton("Emergency"), ProfileButton("Truck"));
        if (_layer == LayerCatalog.Hospital)
            AddButton(_edOnly ? "✓ Emergency departments only" : "All hospitals (ED not required)", ButtonStyle.Secondary, () => { _edOnly = !_edOnly; _ = ComputeAsync(); });
        AddButton($"Show {(_count == 5 ? 10 : 5)} instead of {_count}", ButtonStyle.Secondary, () => { _count = _count == 5 ? 10 : 5; _ = ComputeAsync(); });
        if (_layer == LayerCatalog.Aed)
            AddText("⚠ " + LayerCatalog.AedWarning, FontRole.Bold, Brand.Danger);
        results?.Invoke();
        Refit();
        _ = profile;
    }

    private Button ProfileButton(string name)
    {
        var current = AppServices.Settings.Profile == name || (name == "Car" && AppServices.Settings.Profile is not ("Emergency" or "Truck"));
        return Btn(name == "Emergency" ? "Emergency" : name, current ? ButtonStyle.Primary : ButtonStyle.Secondary, () =>
        {
            AppServices.Settings.Profile = name;
            AppServices.Settings.Save();
            if (_layer != null)
                _ = ComputeAsync();
            else
                Build(null);
        });
    }

    private sealed record Row(Poi Poi, double Seconds, double Meters, double? WalkSeconds, double? WalkMeters, double Straight);

    private async Task ComputeAsync()
    {
        if (_layer == null)
            return;
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        var layer = LayerCatalog.Find(_layer)!;
        Title = "Nearest " + layer.Title.ToLowerInvariant();
        Build(() => AddText("Calculating drive times…", FontRole.Bold));
        var origin = Origin;
        var profile = RoutingService.ProfileFromSettings(AppServices.Settings);
        var count = _count;
        try
        {
            var candidates = AppServices.Data.Search.NearestPois(_layer, origin, 40, 250);
            if (_layer == LayerCatalog.Hospital && _edOnly)
            {
                var ed = candidates.Where(p => p.Kind == "ed").ToList();
                if (ed.Count > 0)
                    candidates = ed;
            }
            if (candidates.Count == 0)
            {
                Build(() => AddText($"No {layer.Title.ToLowerInvariant()} found in the installed map data near here.", FontRole.Bold));
                return;
            }
            var rows = await AppServices.Routing.RunAsync(origin, origin, r =>
            {
                var reach = r.NearestByTime(origin, candidates.Select(c => c.Point).ToList(), profile, count);
                var list = new List<Row>();
                foreach (var x in reach)
                {
                    var poi = candidates[x.Index];
                    double? walkS = null, walkM = null;
                    if (_layer == LayerCatalog.Aed && poi.Point.DistanceTo(origin) < 5000)
                    {
                        var walk = r.Route(origin, poi.Point, VehicleProfile.Walking);
                        walkS = walk?.DurationSeconds;
                        walkM = walk?.DistanceMeters;
                    }
                    list.Add(new Row(poi, x.DurationSeconds, x.DistanceMeters, walkS, walkM, poi.Point.DistanceTo(origin)));
                }
                return list;
            });
            if (cancel.IsCancellationRequested || IsDisposed)
                return;
            Build(() =>
            {
                AddText($"{rows.Count} nearest by {(profile.Kind == ProfileKind.Emergency ? "emergency " : "")}drive time ({profile.Name})", FontRole.Heading);
                var n = 1;
                foreach (var row in rows)
                {
                    var r = row;
                    var detail = $"{Instructions.FormatDuration(r.Seconds)} drive · {Instructions.FormatDistance(r.Meters)}" +
                                 (r.WalkSeconds is { } ws ? $" · walk {Instructions.FormatDuration(ws)}" : "") +
                                 $" · {Instructions.FormatDistance(r.Straight)} straight";
                    AddRow($"{n++}. {r.Poi.DisplayName}", detail, () => Map.ShowPanel(new DirectionsPanel(Map, _fixedOrigin, r.Poi.Point, r.Poi.DisplayName)),
                        Color.FromArgb(unchecked((int)layer.Color)));
                }
                if (rows.Count == 0)
                    AddText("None of them can be reached by road from here with this vehicle.", FontRole.Bold, Brand.Danger);
                if (rows.Count < count && candidates.Count > rows.Count)
                    AddText($"{candidates.Count - rows.Count} more are not reachable by road (or too far).", FontRole.Normal, Brand.Muted);
                AddButton("Copy list for dispatch", ButtonStyle.Primary, () => Map.CopyText(ListText(layer, rows, profile), "Nearest list copied"));
            });
        }
        catch (RoutingException ex)
        {
            Build(() => AddText(ex.Message, FontRole.Bold, Brand.Danger));
        }
    }

    private string ListText(LayerDef layer, List<Row> rows, VehicleProfile profile)
    {
        var o = Origin;
        var lines = new List<string> { $"NEAREST {layer.Title.ToUpperInvariant()} from {CoordinateFormat.Decimal(o.Lat, o.Lon)} (MGRS {CoordinateFormat.Mgrs(o.Lat, o.Lon)}) – {profile.Name}, {DateTime.Now:HH:mm}" };
        var n = 1;
        foreach (var r in rows)
            lines.Add($"{n++}. {r.Poi.DisplayName} – {Instructions.FormatDuration(r.Seconds)}, {Instructions.FormatDistance(r.Meters)}" +
                      (r.WalkSeconds is { } w ? $", walk {Instructions.FormatDuration(w)}" : "") + $" – {CoordinateFormat.Decimal(r.Poi.Lat, r.Poi.Lon)}");
        if (layer.Id == LayerCatalog.Aed)
            lines.Add("AED locations may be out of date - confirm.");
        lines.Add("Field Map offline estimate; verify with dispatch.");
        return string.Join(Environment.NewLine, lines);
    }

    public override void OnClosing() => _cancel?.Cancel();
}
