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
        try
        {
            var near = Map.MyPositionOrCenter;
            results = await Task.Run(() => AppServices.Data.Search.Search(query, near, 25));
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
        if (results.Count == 0)
        {
            AddText($"Nothing found for \"{query}\".", FontRole.Bold);
            AddText("Try a street name, \"Main St & 5th Ave\", a facility (\"hospital\", \"AED\"), \"Hwy 97 km 120\", an address (\"150 Main St\") or coordinates in decimal, DMS or MGRS.");
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
