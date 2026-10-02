using FieldMap.Core.Build;

namespace FieldMap.Map;

/// <summary>Grid index of POIs (≈5 km cells) for drawing the visible ones quickly.</summary>
public sealed class PoiIndex
{
    private const double Cell = 0.05;
    private readonly Dictionary<(int, int), List<Poi>> _cells = new();

    public PoiIndex(IEnumerable<Poi> pois)
    {
        foreach (var p in pois)
        {
            var key = ((int)Math.Floor(p.Lat / Cell), (int)Math.Floor(p.Lon / Cell));
            if (!_cells.TryGetValue(key, out var list))
                _cells[key] = list = new List<Poi>();
            list.Add(p);
        }
    }

    public IEnumerable<Poi> Query(GeoBounds b)
    {
        var y0 = (int)Math.Floor(b.MinLat / Cell);
        var y1 = (int)Math.Floor(b.MaxLat / Cell);
        var x0 = (int)Math.Floor(b.MinLon / Cell);
        var x1 = (int)Math.Floor(b.MaxLon / Cell);
        if ((long)(y1 - y0 + 1) * (x1 - x0 + 1) > 40000)
            yield break; // zoomed far out: too many cells to scan (POIs aren't drawn at that scale anyway)
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if (_cells.TryGetValue((y, x), out var list))
                    foreach (var p in list)
                        if (b.Contains(p.Lat, p.Lon))
                            yield return p;
    }
}
