using FieldMap.Core.Drawings;
using FieldMap.Core.Overlays;
using FieldMap.Views;
using Shared;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>A point, line or area from an imported event layer: its name, description and fields, with map actions.</summary>
public sealed class OverlayFeaturePanel : SidePanel
{
    public OverlayFeaturePanel(MapWindow map, OverlayLayer layer, OverlayFeature f) : base(map, f.Name.Length > 0 ? f.Name : "Event layer")
    {
        AddText(f.Name.Length > 0 ? f.Name : "(no name)", FontRole.Large);
        var kind = f.Geometry switch { OverlayGeometry.Point => "Point", OverlayGeometry.Line => "Line", _ => "Area" };
        AddText($"{kind} · {layer.Info.Name}{(f.Folder.Length > 0 ? " · " + f.Folder : "")}", FontRole.Bold, Brand.Muted);
        if (f.Description.Length > 0)
            AddText(f.Description);
        foreach (var (k, v) in f.Properties.Where(kv => !kv.Key.Equals("name", StringComparison.OrdinalIgnoreCase) && !kv.Key.Equals("description", StringComparison.OrdinalIgnoreCase)
                                                        && !IsStyleKey(kv.Key) && kv.Value.Length < 300).Take(30))
            AddText($"{k}: {v}");
        switch (f.Geometry)
        {
            case OverlayGeometry.Line:
                AddText($"Length {GeoMath.FormatDistance(Core.Geo.Geometry.Length(f.Parts[0]))}");
                break;
            case OverlayGeometry.Polygon:
                var area = Core.Geo.Geometry.Area(f.Parts[0]);
                AddText(area >= 1_000_000 ? $"Area {area / 1_000_000:0.00} km²" : $"Area {area:N0} m² ({area / 10_000:0.0} ha)");
                break;
        }
        var p = f.Anchor;
        AddText($"{CoordinateFormat.Decimal(p.Lat, p.Lon)} · MGRS {CoordinateFormat.Mgrs(p.Lat, p.Lon)}" + (f.Geometry == OverlayGeometry.Point ? "" : " (centre)"));
        if (map.MyPosition is { } me && map.FixUsable)
            AddText($"{GeoMath.FormatDistance(me.DistanceTo(p))} {GeoMath.FormatBearing(me.BearingTo(p))} from my position (straight line)", FontRole.Bold);

        AddButton("Directions here", ButtonStyle.Accent, () => Map.ShowPanel(new DirectionsPanel(Map, null, p, f.Name)));
        AddButtons(Btn("Copy for dispatch", ButtonStyle.Primary, () => Map.CopyText(DispatchText(layer, f), "Copied " + f.Name)),
                   Btn("What's here?", ButtonStyle.Secondary, () => Map.ShowWhatsHere(p, f.Name)));
        AddButtons(Btn("Add to incident drawings", ButtonStyle.Secondary, () => AddToDrawings(layer, f)),
                   Btn("Send to Field Toolkit", ButtonStyle.Secondary, () => Map.SendToToolkit(p, f.Name, layer.Info.Name)));
        AddButton("Event layers…", ButtonStyle.Secondary, () => Map.ShowPanel(new LayersPanel(Map)));
        AddText($"From {layer.Info.Name}, imported {layer.Info.ImportedUtc.ToLocalTime():yyyy-MM-dd HH:mm}. Map files from events are only as accurate as whoever drew them.", FontRole.Normal, Brand.Muted);
        Refit();
    }

    private static bool IsStyleKey(string k) => k is "stroke" or "stroke-width" or "stroke-opacity" or "fill" or "fill-opacity" or "marker-color" or "marker-size" or "marker-symbol";

    public static string DispatchText(OverlayLayer layer, OverlayFeature f)
    {
        var p = f.Anchor;
        var extra = new List<string> { $"From: {layer.Info.Name}{(f.Folder.Length > 0 ? " / " + f.Folder : "")}" };
        if (f.Description.Length > 0)
            extra.Add(f.Description.Replace('\n', ' '));
        return Shared.Core.Geo.DispatchText.Block(new GeoFix(p.Lat, p.Lon, null, null, DateTime.UtcNow, "Event layer"), f.Name.Length > 0 ? f.Name : "Event location", extra);
    }

    private void AddToDrawings(OverlayLayer layer, OverlayFeature f)
    {
        var d = new Drawing
        {
            Kind = f.Geometry switch { OverlayGeometry.Point => DrawingKind.Note, OverlayGeometry.Line => DrawingKind.Note, _ => DrawingKind.Perimeter },
            Shape = f.Geometry switch { OverlayGeometry.Point => DrawingShape.Point, OverlayGeometry.Line => DrawingShape.Line, _ => DrawingShape.Polygon },
            Label = f.Name,
            Notes = (f.Description + $"\nFrom {layer.Info.Name}").Trim(),
            Points = f.Parts[0].ToList(),
            CreatedBy = AppServices.Settings.UserName
        };
        AppServices.Drawings.Items.Add(d);
        AppServices.NotifyDrawingsChanged();
        Map.SelectDrawing(d);
        Map.Flash($"Added {f.Name} to the incident drawings");
    }
}
