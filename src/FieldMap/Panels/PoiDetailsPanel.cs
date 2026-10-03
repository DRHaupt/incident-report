using FieldMap.Core.Build;
using FieldMap.Core.Layers;
using Shared;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>Details of a facility: type, address, hours, source and last-verified date (with the AED warning).</summary>
public sealed class PoiDetailsPanel : SidePanel
{
    public PoiDetailsPanel(Views.MapWindow map, Poi poi) : base(map, poi.KindLabel)
    {
        var layer = LayerCatalog.Find(poi.Layer);
        AddText(poi.DisplayName, FontRole.Large);
        AddText(poi.KindLabel + (layer != null && layer.Title != poi.KindLabel ? $" · {layer.Title}" : ""), FontRole.Bold, Brand.Muted);
        if (poi.Layer == LayerCatalog.Aed)
            AddText("⚠ " + LayerCatalog.AedWarning, FontRole.Bold, Brand.Danger);
        if (poi.Layer == LayerCatalog.Hospital && poi.Kind == "hospital")
            AddText("Emergency department not recorded in the data – confirm with dispatch.", FontRole.Bold, Brand.Danger);

        var p = poi.Props;
        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                AddText($"{label}: {value}");
        }
        Line("Address", p.GetValueOrDefault("address") ?? (p.TryGetValue("addr:housenumber", out var n) ? $"{n} {p.GetValueOrDefault("addr:street")}, {p.GetValueOrDefault("addr:city")}".TrimEnd(',', ' ') : null));
        Line("Where", p.GetValueOrDefault("defibrillator:location"));
        Line("AEDs at this site", p.GetValueOrDefault("aed_count"));
        if (p.GetValueOrDefault("location_method") is { Length: > 0 } method)
            AddText($"Position from the {method} – approximate; the list had no coordinates.", FontRole.Bold, Brand.Danger);
        Line("Access", p.GetValueOrDefault("access"));
        Line("Hours", p.GetValueOrDefault("opening_hours"));
        Line("Phone", p.GetValueOrDefault("phone") ?? p.GetValueOrDefault("contact:phone"));
        Line("Operator", p.GetValueOrDefault("operator"));
        Line("Indoor", p.GetValueOrDefault("indoor"));
        Line("Clearance", p.GetValueOrDefault("maxheight") is { } h ? h + " m" : null);
        Line("Weight limit", p.GetValueOrDefault("maxweight") is { } w ? w + " t" : null);
        Line("Length limit", p.GetValueOrDefault("maxlength"));
        Line("Reference", p.GetValueOrDefault("ref"));
        Line("Distance", p.GetValueOrDefault("distance") is { } km ? "km " + km : null);
        Line("Diesel", p.GetValueOrDefault("fuel:diesel"));
        Line("Hydrant type", p.GetValueOrDefault("fire_hydrant:type"));
        Line("Tank volume", p.GetValueOrDefault("water_tank:volume"));
        Line("Note", p.GetValueOrDefault("note") ?? p.GetValueOrDefault("description"));

        AddText("Source", FontRole.Heading);
        AddText($"{poi.Source}{(poi.SourceId != null ? $" ({poi.SourceId})" : "")}");
        AddText(poi.LastVerified != null ? $"Last verified: {poi.LastVerified}" : "Last verified: not recorded", FontRole.Bold,
            poi.LastVerified == null || (DateTime.TryParse(poi.LastVerified, out var d) && d < DateTime.Today.AddYears(-1)) ? Brand.Danger : Brand.Navy);
        AddText($"{CoordinateFormat.Decimal(poi.Lat, poi.Lon)} · MGRS {CoordinateFormat.Mgrs(poi.Lat, poi.Lon)}");
        if (map.MyPosition is { } me && map.FixUsable)
            AddText($"{GeoMath.FormatDistance(me.DistanceTo(poi.Point))} {GeoMath.FormatBearing(me.BearingTo(poi.Point))} from my position (straight line)", FontRole.Bold);

        AddButton("Directions here", ButtonStyle.Accent, () => Map.ShowPanel(new DirectionsPanel(Map, null, poi.Point, poi.DisplayName)));
        AddButtons(Btn("Copy for dispatch", ButtonStyle.Primary, () => Map.CopyText(DispatchText(poi), "Copied " + poi.DisplayName)),
                   Btn($"Nearest {(layer?.Title ?? "").ToLowerInvariant()}", ButtonStyle.Secondary, () => Map.ShowPanel(new NearestPanel(Map, Map.MyPositionOrCenter, poi.Layer))));
        AddButtons(Btn("What's here?", ButtonStyle.Secondary, () => Map.ShowWhatsHere(poi.Point, poi.DisplayName)),
                   Btn("Send to Field Toolkit", ButtonStyle.Secondary, () => Map.SendToToolkit(poi.Point, poi.DisplayName, poi.KindLabel)));
        Refit();
    }

    public static string DispatchText(Poi poi)
    {
        var extra = new List<string> { $"Type: {poi.KindLabel}", $"Data source: {poi.Source}{(poi.LastVerified != null ? ", verified " + poi.LastVerified : "")}" };
        if (poi.Layer == LayerCatalog.Aed)
            extra.Add("Note: AED locations may be out of date - confirm.");
        return Shared.Core.Geo.DispatchText.Block(new GeoFix(poi.Lat, poi.Lon, null, null, DateTime.UtcNow, "Field Map data"), poi.DisplayName, extra);
    }

    public override void OnClosing()
    {
        Map.MapView.SelectedPoi = null;
        Map.MapView.Invalidate();
    }
}
