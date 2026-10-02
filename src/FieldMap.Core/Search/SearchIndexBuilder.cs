using System.Text.Json;
using FieldMap.Core.Build;
using FieldMap.Core.Geo;
using FieldMap.Core.Layers;
using FieldMap.Core.Routing;
using Microsoft.Data.Sqlite;

namespace FieldMap.Core.Search;

/// <summary>
/// Builds search.sqlite for a region: searchable entries (places, facilities, streets, exits) with an FTS5 trigram
/// index, addresses, intersections, km markers, all POIs, and R*Tree indexes for "What's here?".
/// </summary>
public static class SearchIndexBuilder
{
    public const int SchemaVersion = 1;

    internal const string Schema = @"
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE entries (id INTEGER PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL, norm TEXT NOT NULL, detail TEXT, lat REAL, lon REAL, layer TEXT, rank INTEGER, poi_id INTEGER);
CREATE VIRTUAL TABLE entries_fts USING fts5(norm, content='entries', content_rowid='id', tokenize='trigram');
CREATE TABLE addresses (id INTEGER PRIMARY KEY, number TEXT, unit TEXT, street TEXT, street_norm TEXT, city TEXT, lat REAL, lon REAL, source TEXT);
CREATE INDEX addresses_street ON addresses(street_norm, number);
CREATE VIRTUAL TABLE addresses_rtree USING rtree(id, min_lat, max_lat, min_lon, max_lon);
CREATE TABLE intersections (id INTEGER PRIMARY KEY, name1 TEXT, name2 TEXT, norm1 TEXT, norm2 TEXT, lat REAL, lon REAL, locality TEXT);
CREATE INDEX intersections_names ON intersections(norm1, norm2);
CREATE VIRTUAL TABLE intersections_rtree USING rtree(id, min_lat, max_lat, min_lon, max_lon);
CREATE TABLE km_markers (id INTEGER PRIMARY KEY, ref TEXT, km REAL, lat REAL, lon REAL, label TEXT);
CREATE INDEX km_ref ON km_markers(ref, km);
CREATE TABLE places (id INTEGER PRIMARY KEY, name TEXT, kind TEXT, rank INTEGER, population INTEGER, lat REAL, lon REAL);
CREATE TABLE pois (id INTEGER PRIMARY KEY, layer TEXT, kind TEXT, name TEXT, lat REAL, lon REAL, props TEXT, source TEXT, last_verified TEXT, source_id TEXT);
CREATE INDEX pois_layer ON pois(layer);
";

    public static void Build(MapData data, RoadGraph? graph, string path, IDictionary<string, string> meta, CancellationToken cancel = default)
    {
        if (File.Exists(path))
            File.Delete(path);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        db.Open();
        Exec(db, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
        Exec(db, Schema);
        using var tx = db.BeginTransaction();
        var localities = new LocalityIndex(data.Places);

        foreach (var (k, v) in meta.Append(new("schema_version", SchemaVersion.ToString())))
            Insert(db, tx, "INSERT INTO meta VALUES ($k,$v)", ("$k", k), ("$v", v));

        // Places
        var entry = Prepare(db, tx, "INSERT INTO entries (kind,name,norm,detail,lat,lon,layer,rank,poi_id) VALUES ($kind,$name,$norm,$detail,$lat,$lon,$layer,$rank,$poi)");
        void Entry(string kind, string name, string? detail, double lat, double lon, string? layer, int rank, long? poi, string? norm = null)
        {
            Set(entry, ("$kind", kind), ("$name", name), ("$norm", norm ?? TextNormalizer.Normalize(name)), ("$detail", detail), ("$lat", lat), ("$lon", lon), ("$layer", layer), ("$rank", rank), ("$poi", poi));
            entry.ExecuteNonQuery();
        }
        var place = Prepare(db, tx, "INSERT INTO places (name,kind,rank,population,lat,lon) VALUES ($n,$k,$r,$p,$lat,$lon)");
        foreach (var p in data.Places)
        {
            cancel.ThrowIfCancellationRequested();
            Set(place, ("$n", p.Name), ("$k", p.Kind), ("$r", p.Rank), ("$p", p.Population), ("$lat", p.Lat), ("$lon", p.Lon));
            place.ExecuteNonQuery();
            Entry("place", p.Name, PlaceLabel(p.Kind), p.Lat, p.Lon, null, 100 - p.Rank * 10, null);
        }

        // POIs (all layers) and the named ones as search entries.
        var poi = Prepare(db, tx, "INSERT INTO pois (id,layer,kind,name,lat,lon,props,source,last_verified,source_id) VALUES ($id,$l,$k,$n,$lat,$lon,$p,$s,$v,$sid)");
        foreach (var p in data.Pois)
        {
            cancel.ThrowIfCancellationRequested();
            Set(poi, ("$id", p.Id), ("$l", p.Layer), ("$k", p.Kind), ("$n", p.Name), ("$lat", p.Lat), ("$lon", p.Lon),
                ("$p", JsonSerializer.Serialize(p.Props)), ("$s", p.Source), ("$v", p.LastVerified), ("$sid", p.SourceId));
            poi.ExecuteNonQuery();
            if (p.Layer == LayerCatalog.Markers)
                continue; // searched through km_markers / exits below
            if (p.Name.Length > 0 && p.Layer is not (LayerCatalog.Water or LayerCatalog.Crossings))
            {
                var loc = localities.Nearest(p.Lat, p.Lon);
                Entry("poi", p.Name, p.KindLabel + (loc != null ? ", " + loc : ""), p.Lat, p.Lon, p.Layer, LayerRank(p.Layer), p.Id);
            }
        }

        // Streets: one entry per street name per ~5 km cluster, labelled with the locality.
        var streets = new Dictionary<(string Norm, int Cx, int Cy), (string Name, double Lat, double Lon, double Len)>();
        foreach (var w in data.Roads)
        {
            if (w.Tags["name"] is not { Length: > 0 } name || w.Points.Length < 2 || OsmExtractor.WalkHighways.Contains(w.Tags["highway"] ?? ""))
                continue;
            var mid = w.Points[w.Points.Length / 2];
            var key = (TextNormalizer.Normalize(name), (int)Math.Floor(mid.Lon / 0.07), (int)Math.Floor(mid.Lat / 0.045));
            var len = Geometry.Length(w.Points);
            if (!streets.TryGetValue(key, out var cur) || len > cur.Len)
                streets[key] = (name, mid.Lat, mid.Lon, len);
        }
        foreach (var ((norm, _, _), (name, lat, lon, _)) in streets)
            Entry("street", name, localities.Nearest(lat, lon), lat, lon, null, 40, null, norm);

        // Addresses (OSM and imported ODA), plus street entries for address-only streets.
        var addr = Prepare(db, tx, "INSERT INTO addresses (id,number,unit,street,street_norm,city,lat,lon,source) VALUES ($id,$n,$u,$s,$sn,$c,$lat,$lon,$src)");
        var addrRtree = Prepare(db, tx, "INSERT INTO addresses_rtree VALUES ($id,$lat,$lat,$lon,$lon)");
        var addressStreets = new Dictionary<(string, string), (string Name, double Lat, double Lon, int N)>();
        long addrId = 1;
        foreach (var a in data.Addresses)
        {
            cancel.ThrowIfCancellationRequested();
            var sn = TextNormalizer.Normalize(a.Street);
            var city = a.City.Length > 0 ? a.City : localities.Nearest(a.Lat, a.Lon) ?? "";
            Set(addr, ("$id", addrId), ("$n", a.Number.Trim().ToUpperInvariant()), ("$u", a.Unit), ("$s", a.Street), ("$sn", sn), ("$c", city), ("$lat", a.Lat), ("$lon", a.Lon), ("$src", a.Source));
            addr.ExecuteNonQuery();
            Set(addrRtree, ("$id", addrId), ("$lat", a.Lat), ("$lon", a.Lon));
            addrRtree.ExecuteNonQuery();
            addrId++;
            var k = (sn, city.ToLowerInvariant());
            addressStreets[k] = addressStreets.TryGetValue(k, out var s) ? (s.Name, s.Lat + a.Lat, s.Lon + a.Lon, s.N + 1) : (a.Street, a.Lat, a.Lon, 1);
        }
        var streetNorms = streets.Keys.Select(k => k.Norm).ToHashSet();
        foreach (var ((sn, _), (name, lat, lon, n)) in addressStreets)
            if (!streetNorms.Contains(sn))
                Entry("street", name, localities.Nearest(lat / n, lon / n), lat / n, lon / n, null, 35, null, sn);

        // Intersections from the routing graph: junctions of two differently named roads.
        if (graph != null)
            BuildIntersections(db, tx, graph, localities, cancel);

        // Km markers (with the highway number from the tag or the nearest numbered road) and exits.
        var km = Prepare(db, tx, "INSERT INTO km_markers (ref,km,lat,lon,label) VALUES ($r,$k,$lat,$lon,$l)");
        foreach (var p in data.Pois.Where(p => p.Layer == LayerCatalog.Markers))
        {
            var rf = p.Props.GetValueOrDefault("ref") ?? NearestRef(graph, p.Point) ?? "";
            if (p.Kind == "km" && double.TryParse(p.Props.GetValueOrDefault("distance")?.Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                var label = (rf.Length > 0 ? Instructions.RoadLabel("", rf) + " " : "") + $"km {value:0.#}";
                Set(km, ("$r", NormalizeRef(rf)), ("$k", value), ("$lat", p.Lat), ("$lon", p.Lon), ("$l", label));
                km.ExecuteNonQuery();
            }
            else if (p.Kind == "exit")
            {
                var exitRef = p.Props.GetValueOrDefault("ref") ?? "";
                var name = $"Exit {exitRef}".Trim() + (p.Name.Length > 0 ? $" ({p.Name})" : "");
                var hwy = NearestRef(graph, p.Point);
                Entry("exit", name, hwy != null ? Instructions.RoadLabel("", hwy) : "Exit", p.Lat, p.Lon, LayerCatalog.Markers, 55, p.Id,
                    TextNormalizer.Normalize($"exit {exitRef} {p.Name} {(hwy != null ? "highway " + hwy : "")}"));
            }
        }

        tx.Commit();
        Exec(db, "INSERT INTO entries_fts(entries_fts) VALUES('rebuild');");
        Exec(db, "ANALYZE;");
    }

    public static string NormalizeRef(string rf) => new string(rf.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string? NearestRef(RoadGraph? graph, GeoPoint p)
    {
        if (graph == null)
            return null;
        var best = (Dist: 80.0, Ref: (string?)null);
        foreach (var s in graph.Index.Query(new GeoBounds(p.Lat, p.Lon, p.Lat, p.Lon).Expand(80)))
        {
            var rf = graph.Ref(s);
            if (rf.Length == 0)
                continue;
            var d = Geometry.NearestOnLine(p, graph.Shape(s)).Distance;
            if (d < best.Dist)
                best = (d, rf.Split(';')[0]);
        }
        return best.Ref;
    }

    private static void BuildIntersections(SqliteConnection db, SqliteTransaction tx, RoadGraph g, LocalityIndex localities, CancellationToken cancel)
    {
        var ins = Prepare(db, tx, "INSERT INTO intersections (id,name1,name2,norm1,norm2,lat,lon,locality) VALUES ($id,$a,$b,$na,$nb,$lat,$lon,$loc)");
        var rtree = Prepare(db, tx, "INSERT INTO intersections_rtree VALUES ($id,$lat,$lat,$lon,$lon)");
        var placed = new Dictionary<(string, string), List<GeoPoint>>();
        long id = 1;
        for (var n = 0; n < g.NodeCount; n++)
        {
            if (n % 10000 == 0)
                cancel.ThrowIfCancellationRequested();
            var names = new HashSet<string>();
            for (var k = g.AdjStart[n]; k < g.AdjStart[n + 1]; k++)
            {
                var s = g.AdjEntry[k] >> 1;
                if ((g.SegFlags[s] & SegmentFlags.WalkOnly) != 0)
                    continue;
                var label = g.Name(s);
                if (label.Length == 0 && g.Ref(s).Length > 0)
                    label = Instructions.RoadLabel("", g.Ref(s));
                if (label.Length > 0)
                    names.Add(label);
            }
            if (names.Count < 2)
                continue;
            var p = g.Node(n);
            var list = names.OrderBy(x => x, StringComparer.Ordinal).ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var key = (list[i], list[j]);
                    if (!placed.TryGetValue(key, out var pts))
                        placed[key] = pts = new List<GeoPoint>();
                    if (pts.Any(q => q.DistanceTo(p) < 150))
                        continue; // same junction (dual carriageway, slip lanes)
                    pts.Add(p);
                    var loc = localities.Nearest(p.Lat, p.Lon);
                    foreach (var (a, b) in new[] { (list[i], list[j]), (list[j], list[i]) })
                    {
                        Set(ins, ("$id", id), ("$a", a), ("$b", b), ("$na", TextNormalizer.Normalize(a)), ("$nb", TextNormalizer.Normalize(b)), ("$lat", p.Lat), ("$lon", p.Lon), ("$loc", loc));
                        ins.ExecuteNonQuery();
                        Set(rtree, ("$id", id), ("$lat", p.Lat), ("$lon", p.Lon));
                        rtree.ExecuteNonQuery();
                        id++;
                    }
                }
        }
    }

    public static int LayerRank(string layer) => layer switch
    {
        LayerCatalog.Hospital => 75,
        LayerCatalog.Fire or LayerCatalog.Police or LayerCatalog.Ambulance => 70,
        LayerCatalog.Air or LayerCatalog.School => 60,
        _ => 50
    };

    private static string PlaceLabel(string kind) => kind switch
    {
        "city" => "City",
        "town" => "Town",
        "village" => "Village",
        "hamlet" => "Hamlet",
        "suburb" or "neighbourhood" => "Neighbourhood",
        _ => "Locality"
    };

    // ------------------------------------------------------------------ SQLite helpers

    private static void Exec(SqliteConnection db, string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static SqliteCommand Prepare(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        return c;
    }

    private static void Set(SqliteCommand c, params (string Name, object? Value)[] values)
    {
        if (c.Parameters.Count == 0)
            foreach (var (name, _) in values)
                c.Parameters.Add(new SqliteParameter(name, null));
        foreach (var (name, value) in values)
            c.Parameters[name].Value = value ?? DBNull.Value;
    }

    private static void Insert(SqliteConnection db, SqliteTransaction tx, string sql, params (string, object?)[] values)
    {
        using var c = Prepare(db, tx, sql);
        Set(c, values);
        c.ExecuteNonQuery();
    }
}

/// <summary>Finds the nearest town or village name for labelling ("Main Street, Sampleton").</summary>
internal sealed class LocalityIndex
{
    private readonly Dictionary<(int, int), List<Place>> _grid = new();
    private const double Cell = 0.25;

    public LocalityIndex(IEnumerable<Place> places)
    {
        foreach (var p in places.Where(p => p.Kind is "city" or "town" or "village" or "hamlet" or "suburb"))
        {
            var key = ((int)Math.Floor(p.Lat / Cell), (int)Math.Floor(p.Lon / Cell));
            if (!_grid.TryGetValue(key, out var list))
                _grid[key] = list = new List<Place>();
            list.Add(p);
        }
    }

    public string? Nearest(double lat, double lon, double maxKm = 30)
    {
        var cy = (int)Math.Floor(lat / Cell);
        var cx = (int)Math.Floor(lon / Cell);
        Place? best = null;
        var bestScore = double.MaxValue;
        for (var y = cy - 1; y <= cy + 1; y++)
            for (var x = cx - 1; x <= cx + 1; x++)
                if (_grid.TryGetValue((y, x), out var list))
                    foreach (var p in list)
                    {
                        var d = GeoMath.Distance(lat, lon, p.Lat, p.Lon) / 1000;
                        // Bigger places win over a slightly nearer hamlet.
                        var score = d * p.Rank switch { 1 => 0.5, 2 => 0.7, 3 => 0.85, _ => 1 };
                        if (d <= maxKm && score < bestScore)
                        {
                            best = p;
                            bestScore = score;
                        }
                    }
        return best?.Name;
    }
}
