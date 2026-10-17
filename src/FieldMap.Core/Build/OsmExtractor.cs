using FieldMap.Core.Geo;
using FieldMap.Core.Layers;
using FieldMap.Core.Osm;

namespace FieldMap.Core.Build;

/// <summary>
/// Reads an OSM extract in three streaming passes (relations → ways → nodes) and keeps only what Field Map needs: roads,
/// rail, water, selected land use, boundaries, places, POIs and addresses. Buildings and other detail are skipped, which
/// keeps memory manageable for the 1.2 GB British Columbia extract.
/// </summary>
public sealed class OsmExtractor
{
    public static readonly HashSet<string> CarHighways = new()
    {
        "motorway", "motorway_link", "trunk", "trunk_link", "primary", "primary_link", "secondary", "secondary_link",
        "tertiary", "tertiary_link", "unclassified", "residential", "living_street", "service", "road", "track", "busway"
    };

    public static readonly HashSet<string> WalkHighways = new() { "footway", "pedestrian", "path", "steps", "cycleway", "bridleway" };

    private readonly Dictionary<long, (OsmTags Tags, OsmMember[] Members)> _areaRelations = new();
    private readonly HashSet<long> _relationWays = new();
    private readonly Dictionary<long, OsmRestriction> _restrictions = new();

    private static readonly string[] RestrictionKinds =
    {
        "no_left_turn", "no_right_turn", "no_straight_on", "no_u_turn", "no_entry", "no_exit",
        "only_left_turn", "only_right_turn", "only_straight_on", "only_u_turn"
    };

    /// <summary>
    /// Reads a restriction relation. restriction=* applies to all motor vehicles, restriction:hgv to trucks,
    /// restriction:motorcar to cars (and emergency vehicles, which are cars); except=emergency/hgv/motorcar exempts
    /// those. Time-dependent (restriction:conditional) and bus/bicycle-only restrictions are not used.
    /// </summary>
    internal static OsmRestriction? ParseRestriction(long id, IReadOnlyList<OsmMember> members, OsmTags tags)
    {
        string? kind = null;
        var vehicles = RestrictionVehicles.None;
        if (tags["restriction"] is { } all)
        {
            kind = all;
            vehicles = RestrictionVehicles.All;
        }
        else if (tags["restriction:hgv"] is { } hgv)
        {
            kind = hgv;
            vehicles = RestrictionVehicles.Truck;
        }
        else if ((tags["restriction:motorcar"] ?? tags["restriction:motor_vehicle"]) is { } car)
        {
            kind = car;
            vehicles = tags["restriction:motor_vehicle"] != null ? RestrictionVehicles.All : RestrictionVehicles.Car | RestrictionVehicles.Emergency;
        }
        if (kind == null || !RestrictionKinds.Contains(kind))
            return null;
        foreach (var e in (tags["except"] ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            vehicles &= e switch
            {
                "emergency" => ~RestrictionVehicles.Emergency,
                "hgv" => ~RestrictionVehicles.Truck,
                "motorcar" => ~(RestrictionVehicles.Car | RestrictionVehicles.Emergency),
                "motor_vehicle" => RestrictionVehicles.None,
                _ => RestrictionVehicles.All
            };
        if (vehicles == RestrictionVehicles.None)
            return null;
        var from = members.Where(m => m.Type == OsmMemberType.Way && m.Role == "from").ToList();
        var to = members.Where(m => m.Type == OsmMemberType.Way && m.Role == "to").ToList();
        var viaNodes = members.Where(m => m.Type == OsmMemberType.Node && m.Role == "via").ToList();
        var viaWays = members.Where(m => m.Type == OsmMemberType.Way && m.Role == "via").Select(m => m.Id).ToArray();
        if (from.Count != 1 || to.Count != 1 || viaNodes.Count + (viaWays.Length > 0 ? 1 : 0) != 1 || viaNodes.Count > 1)
            return null;
        return new OsmRestriction
        {
            Id = id,
            From = from[0].Id,
            To = to[0].Id,
            ViaNode = viaNodes.Count == 1 ? viaNodes[0].Id : null,
            ViaWays = viaWays,
            Kind = kind,
            Vehicles = vehicles
        };
    }

    private readonly HashSet<long> _seenWays = new();
    private readonly HashSet<long> _seenTaggedNodes = new();
    private readonly HashSet<long> _boundaryWays = new();
    private readonly Dictionary<long, MapWay> _memberWays = new();
    private readonly List<long> _needed = new();
    private NodeStore _nodes = NodeStore.Empty;
    private readonly MapData _data = new();
    private readonly List<(MapWay Way, string Kind)> _pendingAreas = new();
    private readonly List<(MapWay Way, LayerDef Layer, string Kind)> _pendingWayPois = new();
    private readonly List<MapWay> _addressWays = new();
    private readonly List<(MapWay Way, string Kind)> _pendingWayBusinesses = new();

    public static MapData Extract(string path, IProgress<(string Stage, double Fraction)>? progress = null, CancellationToken cancel = default) =>
        Extract(new[] { path }, progress, cancel);

    /// <summary>
    /// Extracts one or more extracts into one map (e.g. BC and Alberta, so routes can cross the border). Elements present in
    /// several files (along the shared boundary) are kept once.
    /// </summary>
    public static MapData Extract(IReadOnlyList<string> paths, IProgress<(string Stage, double Fraction)>? progress = null, CancellationToken cancel = default)
    {
        var x = new OsmExtractor();
        void Pass(string stage, IOsmHandler h)
        {
            for (var i = 0; i < paths.Count; i++)
            {
                var label = paths.Count > 1 ? $"{stage} ({i + 1}/{paths.Count})" : stage;
                OsmFile.Read(paths[i], h, cancel, progress == null ? null : new Progress<double>(f => progress.Report((label, f))));
            }
        }
        Pass("Reading relations", new RelationPass(x));
        Pass("Reading ways", new WayPass(x));
        x._nodes = NodeStore.Build(x._needed);
        x._needed.Clear();
        x._needed.TrimExcess();
        Pass("Reading nodes", new NodePass(x));
        progress?.Report(("Assembling", 0));
        x.Assemble();
        progress?.Report(("Assembling", 1));
        return x._data;
    }

    // ------------------------------------------------------------------ classification

    internal static string? AreaKind(OsmTags t)
    {
        if (t.Is("natural", "water") || t.Is("waterway", "riverbank") || t.IsAny("landuse", "reservoir", "basin"))
            return "water";
        if (t.Is("natural", "glacier")) return "glacier";
        if (t.Is("natural", "wetland")) return "wetland";
        if (t.IsAny("leisure", "park", "nature_reserve") || t.IsAny("boundary", "national_park", "protected_area")) return "park";
        if (t.Is("landuse", "residential")) return "residential";
        if (t.IsAny("landuse", "commercial", "retail")) return "commercial";
        if (t.Is("landuse", "industrial")) return "industrial";
        if (t.Is("landuse", "military")) return "military";
        if (t.Is("aeroway", "aerodrome")) return "airport";
        if (t.Has("hazard")) return "hazard";
        return null;
    }

    private static bool IsRoad(OsmTags t) =>
        (t["highway"] is { } h && (CarHighways.Contains(h) || WalkHighways.Contains(h)) && !t.Is("area", "yes")) ||
        (t.Is("route", "ferry") && !t.Has("highway"));

    private static bool IsRail(OsmTags t) => t.IsAny("railway", "rail", "light_rail", "narrow_gauge") && !t.Has("abandoned");

    private static bool IsWaterway(OsmTags t) => t.IsAny("waterway", "river", "stream", "canal");

    private static bool IsClosed(IReadOnlyList<long> refs) => refs.Count >= 4 && refs[0] == refs[^1];

    // ------------------------------------------------------------------ passes

    private sealed class RelationPass : IOsmHandler
    {
        private readonly OsmExtractor _x;
        public RelationPass(OsmExtractor x) => _x = x;
        public bool WantNodes => false;
        public bool WantWays => false;
        public bool WantRelations => true;
        public void Node(long id, double lat, double lon, OsmTags tags) { }
        public void Way(long id, IReadOnlyList<long> nodeRefs, OsmTags tags) { }

        public void Relation(long id, IReadOnlyList<OsmMember> members, OsmTags tags)
        {
            var type = tags["type"];
            if (type == "restriction" || type?.StartsWith("restriction:", StringComparison.Ordinal) == true)
            {
                if (ParseRestriction(id, members, tags) is { } r)
                    _x._restrictions[id] = r;
                return;
            }
            if (type == "boundary" && tags.Is("boundary", "administrative") && tags.IsAny("admin_level", "2", "4"))
            {
                foreach (var m in members)
                    if (m.Type == OsmMemberType.Way)
                        _x._boundaryWays.Add(m.Id);
                return;
            }
            if (type is not ("multipolygon" or "boundary"))
                return;
            if (AreaKind(tags) == null && LayerCatalog.Classify(tags) == null)
                return;
            var ways = members.Where(m => m.Type == OsmMemberType.Way).ToArray();
            _x._areaRelations[id] = (tags, ways);
            foreach (var m in ways)
                _x._relationWays.Add(m.Id);
        }
    }

    private sealed class WayPass : IOsmHandler
    {
        private readonly OsmExtractor _x;
        public WayPass(OsmExtractor x) => _x = x;
        public bool WantNodes => false;
        public bool WantWays => true;
        public bool WantRelations => false;
        public void Node(long id, double lat, double lon, OsmTags tags) { }
        public void Relation(long id, IReadOnlyList<OsmMember> members, OsmTags tags) { }

        public void Way(long id, IReadOnlyList<long> refs, OsmTags tags)
        {
            if (refs.Count < 2 || !_x._seenWays.Add(id))
                return;
            MapWay? way = null;
            MapWay W() => way ??= new MapWay { Id = id, NodeIds = refs.ToArray(), Tags = tags };
            var keep = false;

            if (IsRoad(tags))
            {
                _x._data.Roads.Add(W());
                keep = true;
            }
            else if (IsRail(tags))
            {
                _x._data.Rail.Add(W());
                keep = true;
            }
            else if (IsWaterway(tags))
            {
                _x._data.Waterways.Add(W());
                keep = true;
            }
            else if (tags.Is("natural", "coastline"))
            {
                _x._data.Coastlines.Add(W());
                keep = true;
            }
            else if (IsClosed(refs) && AreaKind(tags) is { } kind)
            {
                _x._pendingAreas.Add((W(), kind));
                keep = true;
            }

            if (!IsRoad(tags) && LayerCatalog.Classify(tags) is { } poi)
            {
                _x._pendingWayPois.Add((W(), poi.Layer, poi.Kind));
                keep = true;
            }
            else if (!IsRoad(tags) && Search.BusinessKinds.Classify(tags) is { } business)
            {
                _x._pendingWayBusinesses.Add((W(), business));
                keep = true;
            }
            if (tags.Has("addr:housenumber") && tags.Has("addr:street"))
            {
                _x._addressWays.Add(W());
                keep = true;
            }
            if (_x._boundaryWays.Contains(id))
            {
                _x._data.Boundaries.Add(W());
                keep = true;
            }
            if (_x._relationWays.Contains(id))
            {
                _x._memberWays[id] = W();
                keep = true;
            }
            if (keep)
                _x._needed.AddRange(refs);
        }
    }

    private sealed class NodePass : IOsmHandler
    {
        private readonly OsmExtractor _x;
        public NodePass(OsmExtractor x) => _x = x;
        public bool WantNodes => true;
        public bool WantWays => false;
        public bool WantRelations => false;
        public void Way(long id, IReadOnlyList<long> nodeRefs, OsmTags tags) { }
        public void Relation(long id, IReadOnlyList<OsmMember> members, OsmTags tags) { }

        public void Node(long id, double lat, double lon, OsmTags tags)
        {
            _x._nodes.Set(id, lat, lon);
            if (tags.Count == 0 || !_x._seenTaggedNodes.Add(id))
                return;
            var d = _x._data;
            if (LayerCatalog.Classify(tags) is { } poi)
                d.Pois.Add(MakePoi(poi.Layer, poi.Kind, tags, lat, lon, "node/" + id));
            else if (Search.BusinessKinds.Classify(tags) is { } business)
                d.Businesses.Add(Search.BusinessKinds.Make(tags, business, lat, lon));
            if (tags["place"] is { } place && tags["name"] is { Length: > 0 } placeName &&
                place is "city" or "town" or "village" or "hamlet" or "suburb" or "neighbourhood" or "locality")
            {
                long.TryParse(tags["population"], out var pop);
                d.Places.Add(new Place { Name = placeName, Kind = place, Population = pop, Lat = lat, Lon = lon });
            }
            if (tags["addr:housenumber"] is { } number && tags["addr:street"] is { } street)
                d.Addresses.Add(new AddressPoint { Number = number, Street = street, City = tags["addr:city"] ?? "", Unit = tags["addr:unit"] ?? "", Lat = lat, Lon = lon });
            // Barriers on any kept way; the graph builder only looks up the ones on roads.
            if (tags.Has("barrier") && _x._nodes.Contains(id))
                d.Barriers[id] = tags;
        }
    }

    internal static Poi MakePoi(LayerDef layer, string kind, OsmTags tags, double lat, double lon, string sourceId)
    {
        var props = new Dictionary<string, string>();
        foreach (var key in LayerCatalog.KeptKeys)
            if (tags[key] is { } v && key != "name")
                props[key] = v;
        var verified = tags["check_date"] ?? tags["survey:date"];
        return new Poi
        {
            Layer = layer.Id,
            Kind = kind,
            Name = tags["name"] ?? (layer.Id == LayerCatalog.Markers && tags["distance"] is { } km ? "km " + km : ""),
            Lat = lat,
            Lon = lon,
            Props = props,
            Source = "OpenStreetMap",
            LastVerified = verified is { Length: >= 4 } ? verified : null,
            SourceId = sourceId
        };
    }

    // ------------------------------------------------------------------ assembly

    private GeoPoint[] Resolve(long[] refs)
    {
        var pts = new List<GeoPoint>(refs.Length);
        foreach (var r in refs)
            if (_nodes.TryGet(r, out var p))
                pts.Add(p);
        return pts.ToArray();
    }

    private void Assemble()
    {
        var d = _data;
        var bounds = GeoBounds.Empty;
        foreach (var list in new[] { d.Roads, d.Rail, d.Waterways, d.Coastlines, d.Boundaries })
        {
            foreach (var w in list)
            {
                w.Points = Resolve(w.NodeIds);
                foreach (var p in w.Points)
                    bounds = bounds.Include(p.Lat, p.Lon);
            }
            list.RemoveAll(w => w.Points.Length < 2);
        }
        foreach (var (way, kind) in _pendingAreas)
        {
            var ring = Resolve(way.NodeIds);
            if (ring.Length < 4)
                continue;
            AddArea(new MapArea { Id = way.Id, Kind = kind, Name = way.Tags["name"] }, way.Tags, ring, null);
        }
        d.Restrictions.AddRange(_restrictions.Values);
        foreach (var (id, (tags, members)) in _areaRelations)
        {
            var outers = AssembleRings(members.Where(m => m.Role != "inner").Select(m => _memberWays.GetValueOrDefault(m.Id)).OfType<MapWay>());
            var inners = AssembleRings(members.Where(m => m.Role == "inner").Select(m => _memberWays.GetValueOrDefault(m.Id)).OfType<MapWay>());
            if (outers.Count == 0)
                continue;
            if (AreaKind(tags) is { } kind)
            {
                var area = new MapArea { Id = -id, Kind = kind, Name = tags["name"] };
                foreach (var o in outers)
                    area.Outers.Add(o);
                area.Inners.AddRange(inners);
                area.AreaM2 = outers.Sum(o => Geometry.Area(o)) - inners.Sum(i => Geometry.Area(i));
                Classify(area);
            }
            if (LayerCatalog.Classify(tags) is { } poi)
            {
                var c = Geometry.Centroid(outers.OrderByDescending(o => o.Length).First());
                d.Pois.Add(MakePoi(poi.Layer, poi.Kind, tags, c.Lat, c.Lon, "relation/" + id));
            }
        }
        foreach (var (way, layer, kind) in _pendingWayPois)
        {
            var pts = Resolve(way.NodeIds);
            if (pts.Length == 0)
                continue;
            var c = IsClosed(way.NodeIds) ? Geometry.Centroid(pts) : pts[pts.Length / 2];
            d.Pois.Add(MakePoi(layer, kind, way.Tags, c.Lat, c.Lon, "way/" + way.Id));
        }
        foreach (var (way, kind) in _pendingWayBusinesses)
        {
            var pts = Resolve(way.NodeIds);
            if (pts.Length == 0)
                continue;
            var c = IsClosed(way.NodeIds) ? Geometry.Centroid(pts) : pts[pts.Length / 2];
            d.Businesses.Add(Search.BusinessKinds.Make(way.Tags, kind, c.Lat, c.Lon));
        }
        foreach (var way in _addressWays)
        {
            var pts = Resolve(way.NodeIds);
            if (pts.Length == 0)
                continue;
            var c = Geometry.Centroid(pts);
            d.Addresses.Add(new AddressPoint { Number = way.Tags["addr:housenumber"]!, Street = way.Tags["addr:street"]!, City = way.Tags["addr:city"] ?? "", Unit = way.Tags["addr:unit"] ?? "", Lat = c.Lat, Lon = c.Lon });
        }
        AddBridgesAndTunnels();
        foreach (var p in d.Pois)
            bounds = bounds.Include(p.Lat, p.Lon);
        d.Bounds = bounds;
        var nextId = 1L;
        foreach (var p in d.Pois)
            p.Id = nextId++;
        for (var i = 0; i < d.Businesses.Count; i++)
            d.Businesses[i].Id = i + 1;
        _memberWays.Clear();
        _nodes = NodeStore.Empty;
    }

    private void AddArea(MapArea area, OsmTags tags, GeoPoint[] ring, List<GeoPoint[]>? inners)
    {
        area.Outers.Add(ring);
        if (inners != null)
            area.Inners.AddRange(inners);
        area.AreaM2 = Geometry.Area(ring);
        Classify(area);
    }

    private void Classify(MapArea area)
    {
        switch (area.Kind)
        {
            case "water": _data.Water.Add(area); break;
            case "hazard": _data.Hazards.Add(area); break;
            default: _data.Landuse.Add(area); break;
        }
    }

    /// <summary>Joins member ways end to end into closed rings (ways may be reversed).</summary>
    internal List<GeoPoint[]> AssembleRings(IEnumerable<MapWay> ways)
    {
        var segments = ways.Where(w => w.NodeIds.Length >= 2).Select(w => w.NodeIds.ToList()).ToList();
        var rings = new List<GeoPoint[]>();
        while (segments.Count > 0)
        {
            var ring = segments[0];
            segments.RemoveAt(0);
            var progress = true;
            while (ring[0] != ring[^1] && progress)
            {
                progress = false;
                for (var i = 0; i < segments.Count; i++)
                {
                    var s = segments[i];
                    if (s[0] == ring[^1]) ring.AddRange(s.Skip(1));
                    else if (s[^1] == ring[^1]) ring.AddRange(Enumerable.Reverse(s).Skip(1));
                    else if (s[^1] == ring[0]) ring.InsertRange(0, s.Take(s.Count - 1));
                    else if (s[0] == ring[0]) ring.InsertRange(0, Enumerable.Reverse(s).Take(s.Count - 1));
                    else continue;
                    segments.RemoveAt(i);
                    progress = true;
                    break;
                }
            }
            if (ring[0] == ring[^1] && ring.Count >= 4)
            {
                var pts = Resolve(ring.ToArray());
                if (pts.Length >= 4)
                    rings.Add(pts);
            }
        }
        return rings;
    }

    private void AddBridgesAndTunnels()
    {
        var layer = LayerCatalog.Find(LayerCatalog.Crossings)!;
        foreach (var road in _data.Roads)
        {
            var t = road.Tags;
            if (road.Points.Length < 2 || !CarHighways.Contains(t["highway"] ?? "") || t.Is("highway", "track") && !t.Has("bridge"))
                continue;
            var kind = t["bridge"] is { } b && b != "no" ? "bridge" : t["tunnel"] is { } tu && tu != "no" ? "tunnel" : null;
            if (kind == null)
                continue;
            var mid = road.Points[road.Points.Length / 2];
            var poi = MakePoi(layer, kind, t, mid.Lat, mid.Lon, "way/" + road.Id);
            if (poi.Name.Length == 0)
                poi.Name = t["bridge:name"] ?? t["tunnel:name"] ?? "";
            _data.Pois.Add(poi);
        }
    }
}

/// <summary>Coordinates for a known set of node IDs: sorted ID array plus fixed-point lat/lon (1e-7°), 16 bytes per node.</summary>
internal sealed class NodeStore
{
    public static readonly NodeStore Empty = new(Array.Empty<long>());

    private readonly long[] _ids;
    private readonly int[] _lat;
    private readonly int[] _lon;
    private readonly bool[] _set;

    private NodeStore(long[] ids)
    {
        _ids = ids;
        _lat = new int[ids.Length];
        _lon = new int[ids.Length];
        _set = new bool[ids.Length];
    }

    public static NodeStore Build(List<long> needed)
    {
        var ids = needed.ToArray();
        Array.Sort(ids);
        var n = 0;
        for (var i = 0; i < ids.Length; i++)
            if (i == 0 || ids[i] != ids[i - 1])
                ids[n++] = ids[i];
        Array.Resize(ref ids, n);
        return new NodeStore(ids);
    }

    public int Count => _ids.Length;

    public void Set(long id, double lat, double lon)
    {
        var i = Array.BinarySearch(_ids, id);
        if (i < 0)
            return;
        _lat[i] = (int)Math.Round(lat * 1e7);
        _lon[i] = (int)Math.Round(lon * 1e7);
        _set[i] = true;
    }

    public bool Contains(long id) => Array.BinarySearch(_ids, id) >= 0;

    public bool TryGet(long id, out GeoPoint p)
    {
        var i = Array.BinarySearch(_ids, id);
        if (i < 0 || !_set[i])
        {
            p = default;
            return false;
        }
        p = new GeoPoint(_lat[i] / 1e7, _lon[i] / 1e7);
        return true;
    }
}
