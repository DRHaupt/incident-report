using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FieldMap.Core.Build;
using FieldMap.Core.Geo;
using FieldMap.Core.Layers;
using FieldMap.Core.Routing;
using Microsoft.Data.Sqlite;

namespace FieldMap.Core.Search;

public enum ResultKind
{
    Coordinate,
    Place,
    Poi,
    Street,
    Address,
    Intersection,
    KmMarker,
    Exit,
    Category,
    /// <summary>A shop, restaurant, office, hotel… (<see cref="SearchResult.PoiId"/> identifies it for <see cref="SearchEngine.GetBusiness"/>).</summary>
    Business
}

public sealed record SearchResult(ResultKind Kind, string Name, string Detail, GeoPoint Point, double Score, string? Layer = null, long? PoiId = null, bool Approximate = false)
{
    public override string ToString() => $"{Name} – {Detail}";
}

public sealed class WhatsHere
{
    public GeoPoint Point { get; init; }
    public string? Address { get; init; }
    public double? AddressDistance { get; init; }
    public string? Intersection { get; init; }
    public double? IntersectionDistance { get; init; }
    public string? Road { get; init; }
    public double? RoadDistance { get; init; }
    public string? Locality { get; init; }

    /// <summary>Best one-line description: "near 150 Main Street, Sampleton" or "Main Street & 5th Avenue".</summary>
    public string Summary
    {
        get
        {
            string loc(string s) => Locality != null && !s.Contains(Locality) ? $"{s}, {Locality}" : s;
            if (Address != null && AddressDistance <= 40)
                return loc(Address);
            if (Intersection != null && IntersectionDistance <= 80)
                return loc(Intersection);
            if (Road != null && RoadDistance <= 60)
                return loc(Road) + (Address != null && AddressDistance <= 150 ? $" (near {Address})" : "");
            if (Address != null && AddressDistance <= 150)
                return loc("near " + Address);
            if (Road != null)
                return $"{Instructions.FormatDistance(RoadDistance ?? 0)} from {loc(Road)}";
            return Locality != null ? "near " + Locality : "No address nearby";
        }
    }
}

/// <summary>Searches one or more regions' search.sqlite files. Thread-safe (one connection per region, locked).</summary>
public sealed class SearchEngine : IDisposable
{
    private sealed class Region
    {
        public required SqliteConnection Db;
        public RoadGraph? Graph;
        public readonly object Lock = new();
    }

    private readonly List<Region> _regions = new();

    public SearchEngine()
    {
    }

    public SearchEngine(string searchDbPath, RoadGraph? graph = null) => AddRegion(searchDbPath, graph);

    public void AddRegion(string searchDbPath, RoadGraph? graph)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = searchDbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        _regions.Add(new Region { Db = db, Graph = graph });
    }

    /// <summary>Gives a region its routing graph once loaded (used for road names in "What's here?").</summary>
    public void AttachGraph(int regionIndex, RoadGraph graph)
    {
        if (regionIndex >= 0 && regionIndex < _regions.Count)
            lock (_regions[regionIndex].Lock)
                _regions[regionIndex].Graph = graph;
    }

    public void Dispose()
    {
        foreach (var r in _regions)
            r.Db.Dispose();
        _regions.Clear();
    }

    // ------------------------------------------------------------------ public API

    /// <summary>Results best first. <paramref name="near"/> (map centre or GPS) breaks ties towards closer results.</summary>
    public List<SearchResult> Search(string query, GeoPoint? near = null, int limit = 20)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(query))
            return results;
        query = query.Trim();

        if (QueryParser.TryCoordinates(query, out var coord, out var format))
        {
            results.Add(new SearchResult(ResultKind.Coordinate, CoordinateFormat.Decimal(coord.Lat, coord.Lon), $"Coordinates ({format})", coord, 1));
            return results;
        }
        if (QueryParser.TryHighwayKm(query, out var hwy, out var km))
            results.AddRange(FindKm(hwy, km));
        if (QueryParser.TryIntersection(query, out var a, out var b))
            results.AddRange(FindIntersection(a, b, near));
        var norm = TextNormalizer.Normalize(query);
        if (QueryParser.TryCategory(norm, out var layer))
            results.Add(new SearchResult(ResultKind.Category, "Nearest " + layer.Title.ToLowerInvariant(), "Category – list by drive time", near ?? default, 0.99, layer.Id));
        if (QueryParser.TryAddress(query, out var number, out var street))
            results.AddRange(FindAddress(number, street, near));
        results.AddRange(FindEntries(norm, near));
        results.AddRange(FindBusinessesOfKind(norm, near));

        return results
            .GroupBy(r => (r.Kind, r.Name, Math.Round(r.Point.Lat, 3), Math.Round(r.Point.Lon, 3)))
            .Select(g => g.OrderByDescending(r => r.Score).First())
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
    }

    /// <summary>POIs of a layer nearest to a point (straight line), for categories and nearest-N candidates.</summary>
    public List<Poi> NearestPois(string layer, GeoPoint p, int count, double maxKm = 200)
    {
        var all = new List<Poi>();
        for (var ri = 0; ri < _regions.Count; ri++)
            lock (_regions[ri].Lock)
            {
                using var c = _regions[ri].Db.CreateCommand();
                // A bounding box first (cheap), widened until enough are found.
                foreach (var radius in new[] { 10.0, 40, maxKm })
                {
                    var box = new GeoBounds(p.Lat, p.Lon, p.Lat, p.Lon).Expand(radius * 1000);
                    c.CommandText = "SELECT id,layer,kind,name,lat,lon,props,source,last_verified,source_id FROM pois WHERE layer=$l AND lat BETWEEN $a AND $b AND lon BETWEEN $c AND $d";
                    c.Parameters.Clear();
                    c.Parameters.AddWithValue("$l", layer);
                    c.Parameters.AddWithValue("$a", box.MinLat);
                    c.Parameters.AddWithValue("$b", box.MaxLat);
                    c.Parameters.AddWithValue("$c", box.MinLon);
                    c.Parameters.AddWithValue("$d", box.MaxLon);
                    var found = ReadPois(c, ri);
                    if (found.Count >= count || radius >= maxKm)
                    {
                        all.AddRange(found);
                        break;
                    }
                }
            }
        return all.OrderBy(x => x.Point.DistanceTo(p)).Take(count).ToList();
    }

    public List<Poi> LoadPois()
    {
        var all = new List<Poi>();
        for (var ri = 0; ri < _regions.Count; ri++)
            lock (_regions[ri].Lock)
            {
                using var c = _regions[ri].Db.CreateCommand();
                c.CommandText = "SELECT id,layer,kind,name,lat,lon,props,source,last_verified,source_id FROM pois";
                all.AddRange(ReadPois(c, ri));
            }
        return all;
    }

    public List<Place> LoadPlaces()
    {
        var all = new List<Place>();
        foreach (var region in _regions)
            lock (region.Lock)
            {
                using var c = region.Db.CreateCommand();
                c.CommandText = "SELECT name,kind,population,lat,lon FROM places";
                using var r = c.ExecuteReader();
                while (r.Read())
                    all.Add(new Place { Name = r.GetString(0), Kind = r.GetString(1), Population = r.GetInt64(2), Lat = r.GetDouble(3), Lon = r.GetDouble(4) });
            }
        return all;
    }

    public string? Meta(string key)
    {
        foreach (var region in _regions)
            lock (region.Lock)
            {
                using var c = region.Db.CreateCommand();
                c.CommandText = "SELECT value FROM meta WHERE key=$k";
                c.Parameters.AddWithValue("$k", key);
                if (c.ExecuteScalar() is string v)
                    return v;
            }
        return null;
    }

    /// <summary>Reads POI rows; ids get the region's key (see <see cref="RegionKey"/>) so they're unique across regions.</summary>
    internal static List<Poi> ReadPois(SqliteCommand c, int region)
    {
        var list = new List<Poi>();
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new Poi
            {
                Id = RegionKey(region, r.GetInt64(0)),
                Layer = r.GetString(1),
                Kind = r.GetString(2),
                Name = r.GetString(3),
                Lat = r.GetDouble(4),
                Lon = r.GetDouble(5),
                Props = r.IsDBNull(6) ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(6)) ?? new(),
                Source = r.IsDBNull(7) ? "" : r.GetString(7),
                LastVerified = r.IsDBNull(8) ? null : r.GetString(8),
                SourceId = r.IsDBNull(9) ? null : r.GetString(9)
            });
        return list;
    }

    // ------------------------------------------------------------------ "What's here?"

    public WhatsHere Reverse(GeoPoint p)
    {
        string? address = null, inter = null, road = null, locality = null;
        double? addressD = null, interD = null, roadD = null;
        foreach (var region in _regions)
            lock (region.Lock)
            {
                var box = new GeoBounds(p.Lat, p.Lon, p.Lat, p.Lon).Expand(300);
                using (var c = region.Db.CreateCommand())
                {
                    c.CommandText = "SELECT a.number, a.unit, a.street, a.city, a.lat, a.lon FROM addresses_rtree r JOIN addresses a ON a.id=r.id " +
                                    "WHERE r.min_lat<=$b AND r.max_lat>=$a AND r.min_lon<=$d AND r.max_lon>=$c";
                    Box(c, box);
                    using var rd = c.ExecuteReader();
                    while (rd.Read())
                    {
                        var d = p.DistanceTo(new GeoPoint(rd.GetDouble(4), rd.GetDouble(5)));
                        if (addressD == null || d < addressD)
                        {
                            addressD = d;
                            var unit = rd.IsDBNull(1) ? "" : rd.GetString(1);
                            address = (unit.Length > 0 ? unit + "-" : "") + $"{rd.GetString(0)} {rd.GetString(2)}" + (rd.IsDBNull(3) || rd.GetString(3).Length == 0 ? "" : ", " + rd.GetString(3));
                        }
                    }
                }
                var box2 = new GeoBounds(p.Lat, p.Lon, p.Lat, p.Lon).Expand(800);
                using (var c = region.Db.CreateCommand())
                {
                    c.CommandText = "SELECT i.name1, i.name2, i.lat, i.lon FROM intersections_rtree r JOIN intersections i ON i.id=r.id " +
                                    "WHERE r.min_lat<=$b AND r.max_lat>=$a AND r.min_lon<=$d AND r.max_lon>=$c";
                    Box(c, box2);
                    using var rd = c.ExecuteReader();
                    while (rd.Read())
                    {
                        var d = p.DistanceTo(new GeoPoint(rd.GetDouble(2), rd.GetDouble(3)));
                        if (interD == null || d < interD - 0.01)
                        {
                            interD = d;
                            inter = $"{rd.GetString(0)} & {rd.GetString(1)}";
                        }
                    }
                }
                if (region.Graph is { } g)
                {
                    var segments = g.Index.Query(new GeoBounds(p.Lat, p.Lon, p.Lat, p.Lon).Expand(2000)).ToList();
                    if (segments.Count == 0)
                        segments = g.Index.Query(new GeoBounds(p.Lat, p.Lon, p.Lat, p.Lon).Expand(15000)).ToList();
                    foreach (var s in segments)
                    {
                        if ((g.SegFlags[s] & SegmentFlags.WalkOnly) != 0)
                            continue;
                        var label = Instructions.RoadLabel(g.Name(s), g.Ref(s));
                        if (label.Length == 0)
                            continue;
                        var d = Geometry.NearestOnLine(p, g.Shape(s)).Distance;
                        if (roadD == null || d < roadD)
                        {
                            roadD = d;
                            road = label;
                        }
                    }
                }
                using (var c = region.Db.CreateCommand())
                {
                    c.CommandText = "SELECT name, kind, lat, lon FROM places WHERE kind IN ('city','town','village','hamlet','suburb') AND lat BETWEEN $a AND $b AND lon BETWEEN $c AND $d";
                    Box(c, new GeoBounds(p.Lat, p.Lon, p.Lat, p.Lon).Expand(30000));
                    using var rd = c.ExecuteReader();
                    var best = double.MaxValue;
                    while (rd.Read())
                    {
                        var d = p.DistanceTo(new GeoPoint(rd.GetDouble(2), rd.GetDouble(3))) * rd.GetString(1) switch { "city" => 0.5, "town" => 0.7, "village" => 0.85, _ => 1 };
                        if (d < best)
                        {
                            best = d;
                            locality = rd.GetString(0);
                        }
                    }
                }
            }
        return new WhatsHere
        {
            Point = p, Address = address, AddressDistance = addressD, Intersection = inter, IntersectionDistance = interD,
            Road = road, RoadDistance = roadD, Locality = locality
        };
    }

    private static void Box(SqliteCommand c, GeoBounds b)
    {
        c.Parameters.AddWithValue("$a", b.MinLat);
        c.Parameters.AddWithValue("$b", b.MaxLat);
        c.Parameters.AddWithValue("$c", b.MinLon);
        c.Parameters.AddWithValue("$d", b.MaxLon);
    }

    // ------------------------------------------------------------------ lookups

    private static double ProximityPenalty(GeoPoint? near, GeoPoint p)
    {
        if (near is not { } n || n == default)
            return 0;
        var km = n.DistanceTo(p) / 1000;
        return Math.Min(0.15, Math.Log10(1 + km) * 0.05);
    }

    private IEnumerable<SearchResult> FindEntries(string norm, GeoPoint? near)
    {
        var results = new List<SearchResult>();
        if (norm.Length == 0)
            return results;
        var trigrams = TextNormalizer.Trigrams(norm).ToList();
        foreach (var region in _regions)
            lock (region.Lock)
            {
                using var c = region.Db.CreateCommand();
                if (trigrams.Count == 0)
                {
                    c.CommandText = "SELECT kind,name,norm,detail,lat,lon,layer,rank,poi_id FROM entries WHERE norm LIKE $q ORDER BY rank DESC LIMIT 200";
                    c.Parameters.AddWithValue("$q", norm + "%");
                }
                else
                {
                    var match = string.Join(" OR ", trigrams.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
                    c.CommandText = "SELECT e.kind,e.name,e.norm,e.detail,e.lat,e.lon,e.layer,e.rank,e.poi_id FROM entries_fts f JOIN entries e ON e.id=f.rowid " +
                                    "WHERE entries_fts MATCH $m ORDER BY f.rank LIMIT 400";
                    c.Parameters.AddWithValue("$m", match);
                }
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var name = r.GetString(1);
                    var sim = TextNormalizer.Similarity(norm, r.GetString(2));
                    if (sim < 0.55)
                        continue;
                    var p = new GeoPoint(r.GetDouble(4), r.GetDouble(5));
                    var rank = r.GetInt32(7);
                    var kind = r.GetString(0) switch
                    {
                        "place" => ResultKind.Place,
                        "poi" => ResultKind.Poi,
                        "exit" => ResultKind.Exit,
                        "business" => ResultKind.Business,
                        _ => ResultKind.Street
                    };
                    var score = sim * 0.85 + rank / 1000.0 - ProximityPenalty(near, p);
                    long? id = r.IsDBNull(8) ? null : r.GetInt64(8);
                    if (kind is (ResultKind.Business or ResultKind.Poi) && id is { } rawId)
                        id = RegionKey(_regions.IndexOf(region), rawId);
                    results.Add(new SearchResult(kind, name, r.IsDBNull(3) ? "" : r.GetString(3), p, score, r.IsDBNull(6) ? null : r.GetString(6), id));
                }
            }
        return results;
    }

    // POI and business ids are numbered per region (each starts at 1), so with BC and Alberta both loaded the same id
    // exists twice. Ids handed out carry region * 1e9 + id; the first region's ids are unchanged.
    private const long RegionStride = 1_000_000_000L;

    private static long RegionKey(int region, long id) => region * RegionStride + id;

    /// <summary>A business found by search (id from <see cref="SearchResult.PoiId"/>), or null.</summary>
    public Business? GetBusiness(long key)
    {
        var ri = (int)(key / RegionStride);
        if (ri < 0 || ri >= _regions.Count)
            return null;
        var region = _regions[ri];
        lock (region.Lock)
        {
            try
            {
                using var c = region.Db.CreateCommand();
                c.CommandText = "SELECT name,kind,lat,lon,address,phone,hours,website FROM businesses WHERE id=$id";
                c.Parameters.AddWithValue("$id", key % RegionStride);
                using var r = c.ExecuteReader();
                if (!r.Read())
                    return null;
                return new Business
                {
                    Id = key, Name = r.GetString(0), Kind = r.GetString(1), Lat = r.GetDouble(2), Lon = r.GetDouble(3),
                    Address = r.IsDBNull(4) ? "" : r.GetString(4), Phone = r.IsDBNull(5) ? "" : r.GetString(5),
                    Hours = r.IsDBNull(6) ? "" : r.GetString(6), Website = r.IsDBNull(7) ? "" : r.GetString(7)
                };
            }
            catch (SqliteException)
            {
                return null; // map data built before businesses were added
            }
        }
    }

    /// <summary>
    /// "restaurant", "hotels", "grocery": the nearest businesses of that type (straight line), when the query names a
    /// business type.
    /// </summary>
    private IEnumerable<SearchResult> FindBusinessesOfKind(string norm, GeoPoint? near)
    {
        var results = new List<SearchResult>();
        if (norm.Length < 3 || norm.Split(' ').Length > 3)
            return results;
        var forms = new HashSet<string> { norm };
        if (norm.EndsWith("ies", StringComparison.Ordinal)) forms.Add(norm[..^3] + "y");
        if (norm.EndsWith("es", StringComparison.Ordinal)) forms.Add(norm[..^2]);
        if (norm.EndsWith('s')) forms.Add(norm[..^1]);
        var found = new List<(Business B, int Region)>();
        for (var ri = 0; ri < _regions.Count; ri++)
        {
            var region = _regions[ri];
            lock (region.Lock)
            {
                try
                {
                    using var c = region.Db.CreateCommand();
                    var clauses = new List<string>();
                    var i = 0;
                    foreach (var f in forms)
                    {
                        clauses.Add($"kind_norm = $f{i} OR kind_norm LIKE $s{i} OR kind_norm LIKE $e{i}");
                        c.Parameters.AddWithValue("$f" + i, f);
                        c.Parameters.AddWithValue("$s" + i, f + " %");
                        c.Parameters.AddWithValue("$e" + i, "% " + f);
                        i++;
                    }
                    var order = "";
                    if (near is { } n && n != default)
                    {
                        // Parameters, not text: a negative longitude written into the SQL would read as "--" (a comment).
                        order = "ORDER BY (lat-$nlat)*(lat-$nlat) + 0.4*(lon-$nlon)*(lon-$nlon)";
                        c.Parameters.AddWithValue("$nlat", n.Lat);
                        c.Parameters.AddWithValue("$nlon", n.Lon);
                    }
                    c.CommandText = $"SELECT id,name,kind,lat,lon,address FROM businesses WHERE {string.Join(" OR ", clauses)} {order} LIMIT 15";
                    using var r = c.ExecuteReader();
                    while (r.Read())
                        found.Add((new Business { Id = r.GetInt64(0), Name = r.GetString(1), Kind = r.GetString(2), Lat = r.GetDouble(3), Lon = r.GetDouble(4), Address = r.IsDBNull(5) ? "" : r.GetString(5) }, ri));
                }
                catch (SqliteException)
                {
                    // Older map data without businesses.
                }
            }
        }
        foreach (var (b, ri) in found)
        {
            // Below a matching name, above loose matches; nearer first.
            var score = 0.8 - ProximityPenalty(near, b.Point);
            results.Add(new SearchResult(ResultKind.Business, b.Name, b.Kind + (b.Address.Length > 0 ? ", " + b.Address : ""), b.Point, score, null, RegionKey(ri, b.Id)));
        }
        return results;
    }

    /// <summary>Street entries matching a (fuzzy) street name: their normalised names, best first.</summary>
    private List<string> StreetNorms(Region region, string streetQuery)
    {
        var norm = TextNormalizer.Normalize(streetQuery);
        var trigrams = TextNormalizer.Trigrams(norm).ToList();
        var found = new Dictionary<string, double>();
        using var c = region.Db.CreateCommand();
        if (trigrams.Count == 0)
        {
            c.CommandText = "SELECT norm FROM entries WHERE kind='street' AND norm LIKE $q LIMIT 100";
            c.Parameters.AddWithValue("$q", norm + "%");
        }
        else
        {
            c.CommandText = "SELECT e.norm FROM entries_fts f JOIN entries e ON e.id=f.rowid WHERE entries_fts MATCH $m AND e.kind='street' ORDER BY f.rank LIMIT 300";
            c.Parameters.AddWithValue("$m", string.Join(" OR ", trigrams.Select(t => "\"" + t + "\"")));
        }
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var n = r.GetString(0);
            var sim = StreetSimilarity(norm, n);
            if (sim >= 0.7 && (!found.TryGetValue(n, out var cur) || sim > cur))
                found[n] = sim;
        }
        return found.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(12).ToList();
    }

    /// <summary>Like <see cref="TextNormalizer.Similarity"/> but a missing street type ("Main" for "Main Street") isn't penalised.</summary>
    private static double StreetSimilarity(string query, string name)
    {
        var types = new HashSet<string> { "street", "avenue", "road", "drive", "highway", "boulevard", "crescent", "place", "court", "lane", "way", "trail", "parkway" };
        var q = query.Split(' ').Where(t => !types.Contains(t)).ToArray();
        var n = name.Split(' ').Where(t => !types.Contains(t)).ToArray();
        if (q.Length == 0 || n.Length == 0)
            return TextNormalizer.Similarity(query, name);
        var core = TextNormalizer.Similarity(string.Join(' ', q), string.Join(' ', n));
        // Exact type agreement (or none given) is a small bonus.
        var qt = query.Split(' ').Where(types.Contains).ToHashSet();
        var nt = name.Split(' ').Where(types.Contains).ToHashSet();
        return core + (qt.Count == 0 || qt.SetEquals(nt) ? 0.02 : -0.05);
    }

    private IEnumerable<SearchResult> FindIntersection(string a, string b, GeoPoint? near)
    {
        var results = new List<SearchResult>();
        foreach (var region in _regions)
            lock (region.Lock)
            {
                var na = StreetNorms(region, a);
                var nb = StreetNorms(region, b);
                if (na.Count == 0 || nb.Count == 0)
                    continue;
                using var c = region.Db.CreateCommand();
                var pa = string.Join(",", na.Select((_, i) => "$a" + i));
                var pb = string.Join(",", nb.Select((_, i) => "$b" + i));
                c.CommandText = $"SELECT name1,name2,norm1,norm2,lat,lon,locality FROM intersections WHERE norm1 IN ({pa}) AND norm2 IN ({pb})";
                for (var i = 0; i < na.Count; i++) c.Parameters.AddWithValue("$a" + i, na[i]);
                for (var i = 0; i < nb.Count; i++) c.Parameters.AddWithValue("$b" + i, nb[i]);
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var p = new GeoPoint(r.GetDouble(4), r.GetDouble(5));
                    var sim = (StreetSimilarity(TextNormalizer.Normalize(a), r.GetString(2)) + StreetSimilarity(TextNormalizer.Normalize(b), r.GetString(3))) / 2;
                    results.Add(new SearchResult(ResultKind.Intersection, $"{r.GetString(0)} & {r.GetString(1)}",
                        "Intersection" + (r.IsDBNull(6) ? "" : ", " + r.GetString(6)), p, 0.9 + sim * 0.1 - ProximityPenalty(near, p)));
                }
            }
        return results;
    }

    private IEnumerable<SearchResult> FindAddress(string number, string street, GeoPoint? near)
    {
        var results = new List<SearchResult>();
        number = number.ToUpperInvariant();
        foreach (var region in _regions)
            lock (region.Lock)
            {
                var norms = StreetNorms(region, street);
                if (norms.Count == 0)
                    continue;
                using var c = region.Db.CreateCommand();
                var ps = string.Join(",", norms.Select((_, i) => "$s" + i));
                c.CommandText = $"SELECT number,unit,street,street_norm,city,lat,lon FROM addresses WHERE street_norm IN ({ps})";
                for (var i = 0; i < norms.Count; i++) c.Parameters.AddWithValue("$s" + i, norms[i]);
                var rows = new List<(string Num, string Unit, string Street, string Norm, string City, GeoPoint P)>();
                using (var r = c.ExecuteReader())
                    while (r.Read())
                        rows.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4), new GeoPoint(r.GetDouble(5), r.GetDouble(6))));
                var qnorm = TextNormalizer.Normalize(street);
                foreach (var group in rows.GroupBy(x => (x.Norm, x.City)))
                {
                    var sim = StreetSimilarity(qnorm, group.Key.Norm);
                    var exact = group.Where(x => x.Num == number).ToList();
                    if (exact.Count > 0)
                    {
                        foreach (var x in exact.Take(3))
                            results.Add(new SearchResult(ResultKind.Address, (x.Unit.Length > 0 ? x.Unit + "-" : "") + $"{x.Num} {x.Street}", "Address" + (x.City.Length > 0 ? ", " + x.City : ""), x.P,
                                0.93 + sim * 0.06 - ProximityPenalty(near, x.P)));
                        continue;
                    }
                    // No exact number: interpolate between the nearest numbers on the same side of the street.
                    if (!int.TryParse(Regex.Match(number, @"^\d+").Value, out var n))
                        continue;
                    var numbered = group.Select(x => (N: int.TryParse(Regex.Match(x.Num, @"^\d+").Value, out var v) ? v : -1, x.P, x.Street))
                        .Where(x => x.N >= 0).OrderBy(x => x.N).ToList();
                    var sameSide = numbered.Where(x => x.N % 2 == n % 2).ToList();
                    var pool = sameSide.Count >= 2 ? sameSide : numbered;
                    var lower = pool.LastOrDefault(x => x.N < n);
                    var upper = pool.FirstOrDefault(x => x.N > n);
                    if (lower.Street == null || upper.Street == null || upper.N - lower.N > 400)
                        continue;
                    var p = Geometry.Lerp(lower.P, upper.P, (double)(n - lower.N) / (upper.N - lower.N));
                    results.Add(new SearchResult(ResultKind.Address, $"{number} {lower.Street}",
                        $"Approximate (between {lower.N} and {upper.N})" + (group.Key.City.Length > 0 ? ", " + group.Key.City : ""), p, 0.85 + sim * 0.05 - ProximityPenalty(near, p), Approximate: true));
                }
            }
        return results;
    }

    private IEnumerable<SearchResult> FindKm(string hwy, double km)
    {
        var results = new List<SearchResult>();
        var rf = SearchIndexBuilder.NormalizeRef(hwy);
        foreach (var region in _regions)
            lock (region.Lock)
            {
                using var c = region.Db.CreateCommand();
                c.CommandText = "SELECT km,lat,lon,label FROM km_markers WHERE ref=$r ORDER BY km";
                c.Parameters.AddWithValue("$r", rf);
                var markers = new List<(double Km, GeoPoint P, string Label)>();
                using (var r = c.ExecuteReader())
                    while (r.Read())
                        markers.Add((r.GetDouble(0), new GeoPoint(r.GetDouble(1), r.GetDouble(2)), r.GetString(3)));
                if (markers.Count == 0)
                    continue;
                var exact = markers.FirstOrDefault(m => Math.Abs(m.Km - km) < 0.05);
                if (exact.Label != null)
                {
                    results.Add(new SearchResult(ResultKind.KmMarker, exact.Label, "Highway km marker", exact.P, 1));
                    continue;
                }
                var lower = markers.LastOrDefault(m => m.Km < km);
                var upper = markers.FirstOrDefault(m => m.Km > km);
                if (lower.Label != null && upper.Label != null && upper.Km - lower.Km <= 10)
                {
                    var p = Geometry.Lerp(lower.P, upper.P, (km - lower.Km) / (upper.Km - lower.Km));
                    results.Add(new SearchResult(ResultKind.KmMarker, $"{Instructions.RoadLabel("", hwy)} km {km:0.#}",
                        $"Approximate – between markers km {lower.Km:0.#} and km {upper.Km:0.#}", p, 0.97, Approximate: true));
                }
                else
                {
                    var nearest = markers.OrderBy(m => Math.Abs(m.Km - km)).First();
                    results.Add(new SearchResult(ResultKind.KmMarker, nearest.Label, $"Nearest marker to km {km:0.#} on this highway", nearest.P, 0.8, Approximate: true));
                }
            }
        return results;
    }
}

/// <summary>Recognises the special query forms: coordinates, highway + km, intersections, addresses, categories.</summary>
public static class QueryParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool TryCoordinates(string q, out GeoPoint p, out string format)
    {
        p = default;
        format = "";
        if (TryUtm(q, out p))
        {
            format = "UTM";
            return true;
        }
        // Only treat it as coordinates when it is mostly numbers (so "5th Ave" isn't parsed).
        if (!Regex.IsMatch(q, @"\d") || Regex.Matches(q, "[A-Za-z]").Count > 6 && !Mgrs.LooksLikeMgrs(q))
            return false;
        if (CoordinateFormat.TryParse(q, out var lat, out var lon))
        {
            p = new GeoPoint(lat, lon);
            format = Mgrs.LooksLikeMgrs(q) ? "MGRS" : Regex.IsMatch(q, "[°'′\"″]|\\d \\d") ? "DMS/DDM" : "decimal degrees";
            return true;
        }
        return false;
    }

    /// <summary>"10U 500000 5600000", "10 U 500000E 5600000N".</summary>
    public static bool TryUtm(string q, out GeoPoint p)
    {
        p = default;
        var m = Regex.Match(q.Trim(), @"^(\d{1,2})\s*([C-HJ-NP-X])\s+(\d{6}(?:\.\d+)?)\s*m?\s*E?\s*[, ]\s*(\d{7}(?:\.\d+)?)\s*m?\s*N?$", RegexOptions.IgnoreCase);
        if (!m.Success)
            return false;
        var zone = int.Parse(m.Groups[1].Value, Inv);
        var band = char.ToUpperInvariant(m.Groups[2].Value[0]);
        if (zone is < 1 or > 60)
            return false;
        var (lat, lon) = Utm.ToLatLon(zone, band >= 'N', double.Parse(m.Groups[3].Value, Inv), double.Parse(m.Groups[4].Value, Inv));
        p = new GeoPoint(lat, lon);
        return true;
    }

    /// <summary>"Hwy 97 km 120", "highway 1 km 55.5", "97 km 120", "km 120 hwy 97".</summary>
    public static bool TryHighwayKm(string q, out string hwy, out double km)
    {
        hwy = "";
        km = 0;
        var s = q.Trim().ToLowerInvariant();
        var m = Regex.Match(s, @"^(?:hwy|highway|route|rte|hy)?\s*\.?\s*(\d{1,3}[a-z]?)\s*(?:,|at)?\s*(?:km|kilometre|kilometer|k)\s*\.?\s*(\d+(?:\.\d+)?)$");
        if (!m.Success)
            m = Regex.Match(s, @"^(?:km|kilometre|kilometer)\s*(\d+(?:\.\d+)?)\s*(?:on\s+)?(?:hwy|highway|route)\s*(\d{1,3}[a-z]?)$");
        if (!m.Success)
            return false;
        var swapped = s.StartsWith("km") || s.StartsWith("kilo");
        hwy = (swapped ? m.Groups[2] : m.Groups[1]).Value.ToUpperInvariant();
        km = double.Parse((swapped ? m.Groups[1] : m.Groups[2]).Value, Inv);
        return true;
    }

    /// <summary>"Main St & 5th Ave", "Main and 5th", "Main / 5th", "Main @ 5th", "Main at 5th".</summary>
    public static bool TryIntersection(string q, out string a, out string b)
    {
        a = b = "";
        var parts = Regex.Split(q, @"\s*(?:&|@|/|\band\b|\bat\b)\s*", RegexOptions.IgnoreCase).Where(p => p.Trim().Length > 0).ToArray();
        // "150 Main St and …" is an address, not an intersection.
        if (parts.Length != 2 || Regex.IsMatch(parts[0], @"^\s*\d+\s+[A-Za-z]"))
            return false;
        a = parts[0].Trim();
        b = parts[1].Trim();
        return true;
    }

    /// <summary>"150 Main St", "150A Main Street, Sampleton", "#3-150 Main".</summary>
    public static bool TryAddress(string q, out string number, out string street)
    {
        number = street = "";
        var m = Regex.Match(q.Trim(), @"^(?:#?\w+\s*-\s*)?(\d{1,6}[A-Za-z]?)\s+(.+?)(?:,.*)?$");
        if (!m.Success || Regex.IsMatch(m.Groups[2].Value, @"^(st|nd|rd|th)\b", RegexOptions.IgnoreCase))
            return false;
        number = m.Groups[1].Value;
        street = m.Groups[2].Value;
        return true;
    }

    /// <summary>A category word on its own ("aed", "nearest hospital", "fire hall") → its layer.</summary>
    public static bool TryCategory(string normQuery, out LayerDef layer)
    {
        layer = null!;
        var q = Regex.Replace(normQuery, @"^(nearest|closest|find|nearby)\s+", "");
        foreach (var l in LayerCatalog.All)
            if (l.SearchWords.Any(w => TextNormalizer.Normalize(w) == q))
            {
                layer = l;
                return true;
            }
        return false;
    }
}
