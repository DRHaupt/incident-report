using FieldMap.Core.Drawings;
using FieldMap.Core.Search;
using Shared;
using Shared.Core.Radio;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>"What's here?": nearest address or intersection, coordinates in every format, radio read-out, copy for dispatch.</summary>
public sealed class WhatsHerePanel : SidePanel
{
    private readonly GeoPoint _point;
    private readonly string? _label;
    private WhatsHere? _here;

    public WhatsHerePanel(Views.MapWindow map, GeoPoint point, string? label) : base(map, "What's here?")
    {
        _point = point;
        _label = label;
        Build();
        _ = LookupAsync();
    }

    private async Task LookupAsync()
    {
        try
        {
            var here = await Task.Run(() => AppServices.Data.Search.Reverse(_point));
            if (IsDisposed)
                return;
            _here = here;
            Build();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // No search data: coordinates still work.
        }
    }

    private void Build()
    {
        Clear();
        var p = _point;
        if (_label != null)
            AddText(_label, FontRole.Heading);
        AddText(_here?.Summary ?? (AppServices.Data.HasData ? "Looking up the nearest address…" : "No map data installed"), FontRole.Large);
        if (_here?.Intersection != null && !(_here.Summary.Contains(_here.Intersection)))
            AddText($"Nearest intersection: {_here.Intersection} ({GeoMath.FormatDistance(_here.IntersectionDistance ?? 0)})");
        if (_here?.Address != null && !_here.Summary.Contains(_here.Address))
            AddText($"Nearest address: {_here.Address} ({GeoMath.FormatDistance(_here.AddressDistance ?? 0)})");
        if (Map.MyPosition is { } me && Map.FixUsable)
            AddText($"From my position: {GeoMath.FormatDistance(me.DistanceTo(p))}, bearing {GeoMath.FormatBearing(me.BearingTo(p))} true", FontRole.Bold);

        AddButton("Copy for dispatch", ButtonStyle.Primary, () => Copy(DispatchBlock(), "Location copied – paste it into the dispatch system or message."), FontRole.Heading);

        foreach (var (name, value) in new[]
                 {
                     ("Lat/Long", CoordinateFormat.Decimal(p.Lat, p.Lon)),
                     ("DMS", CoordinateFormat.Dms(p.Lat, p.Lon)),
                     ("Deg/min", CoordinateFormat.Ddm(p.Lat, p.Lon)),
                     ("MGRS", CoordinateFormat.Mgrs(p.Lat, p.Lon)),
                     ("UTM", CoordinateFormat.Utm(p.Lat, p.Lon))
                 })
        {
            var v = value;
            AddRow(v, name + " – tap to copy", () => Copy(DispatchText.Ascii(v), "Copied " + v));
        }

        var mgrs = AppServices.Settings.ReadoutFormat == "mgrs";
        AddText("Radio read-out", FontRole.Heading);
        AddText(mgrs ? CoordinateFormat.SpokenMgrs(CoordinateFormat.Mgrs(p.Lat, p.Lon)) : CoordinateFormat.SpokenDecimal(p.Lat, p.Lon), FontRole.Large);
        AddButton(mgrs ? "Read lat/long instead" : "Read grid reference instead", ButtonStyle.Secondary, () =>
        {
            AppServices.Settings.ReadoutFormat = mgrs ? "decimal" : "mgrs";
            AppServices.Settings.Save();
            Build();
        });

        AddButtons(Btn("Directions to here", ButtonStyle.Accent, () => Map.ShowPanel(new DirectionsPanel(Map, null, p))),
                   Btn("Nearest from here", ButtonStyle.Secondary, () => Map.ShowPanel(new NearestPanel(Map, p, null))));
        AddButtons(Btn("Drop incident pin", ButtonStyle.Danger, () => DrawPanel.QuickAdd(Map, DrawingKind.Incident, p, _label)),
                   Btn("Save as note", ButtonStyle.Secondary, () => DrawPanel.QuickAdd(Map, DrawingKind.Note, p, _label ?? _here?.Summary)));
        AddButtons(Btn("Send to Field Toolkit", ButtonStyle.Secondary, () => Map.SendToToolkit(p, _label ?? _here?.Summary ?? "Map point")),
                   Btn("Directions from here", ButtonStyle.Secondary, () => Map.ShowPanel(new DirectionsPanel(Map, p, null))));
        Refit();
    }

    /// <summary>The Field Toolkit Location block, plus the nearest address/intersection.</summary>
    private string DispatchBlock()
    {
        var extra = new List<string>();
        if (_here != null)
            extra.Add("Near: " + DispatchText.Ascii(_here.Summary));
        if (Map.MyPosition is { } me && Map.FixUsable)
            extra.Add($"From current position: {GeoMath.FormatDistance(me.DistanceTo(_point))}, bearing {GeoMath.FormatBearing(me.BearingTo(_point))} true");
        return DispatchText.Block(new GeoFix(_point.Lat, _point.Lon, null, null, DateTime.UtcNow, "Point on map (Field Map)"), _label, extra);
    }

    private void Copy(string text, string confirmation) => Map.CopyText(text, confirmation);

    public override void OnClosing() => Map.ClearMarker();
}
