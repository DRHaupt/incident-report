using FieldMap.Core.Layers;
using FieldMap.Core.Search;
using Shared;
using Shared.UI;

namespace FieldMap.Panels;

/// <summary>Search results: addresses, places, intersections, facilities, km markers, coordinates and categories.</summary>
public sealed class SearchPanel : SidePanel
{
    public SearchPanel(Views.MapWindow map, string query) : base(map, "Search") => _ = RunAsync(query);

    private async Task RunAsync(string query)
    {
        AddText($"Searching for \"{query}\"…", FontRole.Bold);
        List<SearchResult> results;
        var near = Map.MyPositionOrCenter;
        // Imported event files first: at an event that's usually what people look for ("medic 2", "gate C").
        var events = Core.Overlays.OverlaySearch.Search(AppServices.Overlays.Layers, query, near, 30);
        try
        {
            results = AppServices.Data.HasData ? await Task.Run(() => AppServices.Data.Search.Search(query, near, 25)) : new List<SearchResult>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Clear();
            AddText("Search is not available: " + ex.Message, FontRole.Bold, Brand.Danger);
            return;
        }
        if (IsDisposed)
            return;
        Clear();
        Title = $"Search: {query}";
        if (events.Count > 0)
        {
            AddText($"In event files ({events.Count}{(events.Count == 30 ? "+" : "")})", FontRole.Heading);
            foreach (var m in events.Take(15))
            {
                var match = m;
                var kind = m.Feature.Geometry switch { Core.Overlays.OverlayGeometry.Point => "Point", Core.Overlays.OverlayGeometry.Line => "Line", _ => "Area" };
                AddRow(m.Feature.Name.Length > 0 ? m.Feature.Name : $"({kind.ToLowerInvariant()})",
                    $"{kind} · {m.Layer.Info.Name}{(m.Feature.Folder.Length > 0 ? " › " + m.Feature.Folder : "")}" +
                    (m.MatchedIn != "name" ? $" · found in {m.MatchedIn}" : "") + " · " + GeoMath.FormatDistance(m.Feature.Anchor.DistanceTo(near)),
                    () => Map.ShowOverlayFeature(match.Layer, match.Feature), Brand.Amber);
            }
            if (events.Count > 15)
                AddButton($"All matches in event files…", ButtonStyle.Secondary, () => Map.ShowPanel(new EventItemsPanel(Map, null, query)));
            if (results.Count > 0)
                AddText("On the map", FontRole.Heading);
        }
        if (results.Count == 0 && events.Count == 0)
        {
            AddText($"Nothing found for \"{query}\".", FontRole.Bold);
            AddText("Try a street name, \"Main St & 5th Ave\", a facility (\"hospital\", \"AED\"), a business or type (\"Tim Hortons\", \"restaurant\"), \"Hwy 97 km 120\", an address (\"150 Main St\"), coordinates in decimal, DMS or MGRS, or anything in an imported event file.");
            Refit();
            return;
        }
        foreach (var r in results)
        {
            var result = r;
            var accent = r.Kind switch
            {
                ResultKind.Category => Brand.Amber,
                ResultKind.Poi when r.Layer is { } l && LayerCatalog.Find(l) is { } def => Color.FromArgb(unchecked((int)def.Color)),
                ResultKind.Coordinate => Brand.Success,
                ResultKind.Business => Color.FromArgb(0x6D, 0x4C, 0x41),
                _ => Brand.Navy
            };
            var detail = r.Detail + (r.Approximate ? " (approximate)" : "");
            if (r.Kind != ResultKind.Category && r.Kind != ResultKind.Coordinate)
                detail += " · " + GeoMath.FormatDistance(r.Point.DistanceTo(Map.MyPositionOrCenter));
            AddRow(r.Name, detail, () => Open(result), accent);
        }
        Refit();
    }

    private void Open(SearchResult r)
    {
        switch (r.Kind)
        {
            case ResultKind.Category when r.Layer != null:
                Map.ShowPanel(new NearestPanel(Map, Map.MyPositionOrCenter, r.Layer));
                return;
            case ResultKind.Business when r.PoiId is { } key && AppServices.Data.Search.GetBusiness(key) is { } business:
                Map.CenterOn(business.Point, 17);
                Map.MapView.Marker = business.Point;
                Map.MapView.Invalidate();
                Map.ShowPanel(new BusinessPanel(Map, business));
                return;
            case ResultKind.Poi when r.PoiId is { } id && AppServices.Data.FindPoi(id) is { } poi:
                Map.CenterOn(poi.Point, 16.5);
                Map.SelectPoi(poi);
                return;
            default:
                var zoom = r.Kind switch { ResultKind.Place => 13.5, ResultKind.Street => 15.5, _ => 17 };
                Map.CenterOn(r.Point, zoom);
                Map.ShowWhatsHere(r.Point, r.Name + (r.Approximate ? " (approximate)" : ""));
                return;
        }
    }
}
