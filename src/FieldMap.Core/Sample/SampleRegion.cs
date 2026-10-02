using System.Globalization;
using System.Xml;
using FieldMap.Core.Geo;

namespace FieldMap.Core.Sample;

/// <summary>
/// Generates "Sample County": a small, entirely fictitious region (OSM XML) used until real data is installed and by the
/// tests. Every name is invented; it sits in empty wilderness in northern BC so it can't be confused with a real town,
/// and the app shows a red SAMPLE DATA banner whenever it is in use.
/// </summary>
public static class SampleRegion
{
    public const string RegionId = "sample";
    public const string Name = "Sample County (fictitious)";

    /// <summary>Town centre (south-west corner of the street grid is 560 m west and level with this point).</summary>
    public static readonly GeoPoint Origin = new(57.4000, -125.9000);

    public const double Block = 160;

    private static readonly string[] Streets = { "Alder Street", "Birch Street", "Cedar Street", "Douglas Street", "Main Street", "Fir Street", "Garnet Street", "Hemlock Street" };
    private static readonly string[] Avenues = { "1st Avenue", "2nd Avenue", "3rd Avenue", "4th Avenue", "5th Avenue", "6th Avenue", "7th Avenue", "8th Avenue" };

    /// <summary>Metres east/north of <see cref="Origin"/> → lat/lon.</summary>
    public static GeoPoint At(double east, double north) => Geometry.FromLocal(Origin, new Vec(east, north));

    /// <summary>x of street j (0–7), y of avenue i (0–7).</summary>
    public static double StreetX(int j) => -560 + j * Block;
    public static double AvenueY(int i) => i * Block;

    public static GeoPoint Intersection(int avenue, int street) => At(StreetX(street), AvenueY(avenue));

    public static GeoPoint HospitalPoint => At(StreetX(1) + 80, AvenueY(6) + 80);
    public static GeoPoint FireHallPoint => At(StreetX(4) + 30, AvenueY(3) + 20);

    /// <summary>A point on Mill Road east of the low bridge (car goes over the 3.5 m bridge, a truck must go round).</summary>
    public static GeoPoint MillRoadEast => At(4000, 600);

    /// <summary>End of the gated forest service road (emergency vehicles may pass the gate, cars may not).</summary>
    public static GeoPoint FsrEnd => At(-6000, 4200);

    public static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        Write(stream);
    }

    public static void Write(Stream stream)
    {
        var b = new Builder();
        b.Build();
        b.Save(stream);
    }

    private sealed class Builder
    {
        private readonly List<(long Id, GeoPoint P, (string, string)[] Tags)> _nodes = new();
        private readonly List<(long Id, long[] Refs, (string, string)[] Tags)> _ways = new();
        private readonly List<(long Id, (string Type, long Ref, string Role)[] Members, (string, string)[] Tags)> _relations = new();
        private long _next = 1;

        private long Node(GeoPoint p, params (string, string)[] tags)
        {
            var id = _next++;
            _nodes.Add((id, p, tags));
            return id;
        }

        private long N(double east, double north, params (string, string)[] tags) => Node(At(east, north), tags);

        private long Way(long[] refs, params (string, string)[] tags)
        {
            var id = _next++;
            _ways.Add((id, refs, tags));
            return id;
        }

        private long Ring(IEnumerable<(double E, double N)> pts, params (string, string)[] tags)
        {
            var ids = pts.Select(p => N(p.E, p.N)).ToList();
            ids.Add(ids[0]);
            return Way(ids.ToArray(), tags);
        }

        private static IEnumerable<(double, double)> Circle(double e, double n, double r, int segments = 24) =>
            Enumerable.Range(0, segments).Select(i => (e + r * Math.Cos(2 * Math.PI * i / segments), n + r * Math.Sin(2 * Math.PI * i / segments)));

        public void Build()
        {
            // Street grid: shared intersection nodes so the road graph is connected.
            var grid = new long[8, 8];
            for (var i = 0; i < 8; i++)
                for (var j = 0; j < 8; j++)
                    grid[i, j] = N(StreetX(j), AvenueY(i));
            for (var i = 0; i < 8; i++)
            {
                var tags = new List<(string, string)> { ("highway", i is 0 or 4 ? "secondary" : "residential"), ("name", Avenues[i]), ("maxspeed", "50") };
                if (i == 2)
                    tags.Add(("oneway", "yes")); // 3rd Avenue: one way eastbound
                Way(Enumerable.Range(0, 8).Select(j => grid[i, j]).ToArray(), tags.ToArray());
            }
            for (var j = 0; j < 8; j++)
                Way(Enumerable.Range(0, 8).Select(i => grid[i, j]).ToArray(), ("highway", j == 4 ? "tertiary" : "residential"), ("name", Streets[j]), ("maxspeed", "50"));

            // Highway 901 south of town, with an exit to Main Street and a bridge over Sample River.
            var hwyX = new double[] { -6000, -4000, -2000, -560, StreetX(4), 1120, 2400 };
            var hwyNodes = hwyX.Select(x => N(x, -400)).ToList();
            var bridgeStart = hwyNodes[^1];
            var bridgeEnd = N(2600, -400);
            var east = new[] { bridgeEnd, N(4000, -400), N(6000, -400) };
            var hwyTags = new (string, string)[] { ("highway", "trunk"), ("ref", "901"), ("name", "Highway 901"), ("maxspeed", "90") };
            Way(hwyNodes.ToArray(), hwyTags);
            Way(new[] { bridgeStart, bridgeEnd }, hwyTags.Concat(new[] { ("bridge", "yes"), ("layer", "1"), ("maxheight", "4.6"), ("maxweight", "40"), ("bridge:name", "Sample River Bridge") }).ToArray());
            Way(east, hwyTags);
            var hwyMain = hwyNodes[4];
            var hwyMill = east[1];
            var hwyFsr = hwyNodes[0];

            // Km markers every kilometre and the exit.
            for (var km = 0; km <= 12; km++)
                N(-6000 + km * 1000, -412, ("highway", "milestone"), ("distance", (100 + km).ToString(CultureInfo.InvariantCulture)), ("ref", "901"));
            N(StreetX(4) + 30, -410, ("highway", "motorway_junction"), ("ref", "106"), ("name", "Sampleton"));

            // Rail line between town and highway; Main Street crosses it at a level crossing.
            var crossing = N(StreetX(4), -200, ("railway", "level_crossing"));
            Way(new[] { N(-6000, -200), N(-2000, -200), crossing, N(2000, -200), N(6000, -200) }, ("railway", "rail"), ("name", "Sample Valley Railway"));
            Way(new[] { grid[0, 4], crossing, hwyMain }, ("highway", "tertiary"), ("name", "Main Street"), ("maxspeed", "50"));

            // Mill Road: north-east corner over a low bridge (3.5 m) to the east, then down to the highway.
            var millBridgeW = N(2400, 1100);
            var millBridgeE = N(2600, 1100);
            var millEast = N(4000, 600);
            Way(new[] { grid[7, 7], N(1500, 1120), millBridgeW }, ("highway", "tertiary"), ("name", "Mill Road"), ("maxspeed", "60"));
            Way(new[] { millBridgeW, millBridgeE }, ("highway", "tertiary"), ("name", "Mill Road"), ("bridge", "yes"), ("maxheight", "3.5"), ("maxweight", "10"), ("bridge:name", "Old Mill Bridge"), ("maxspeed", "60"));
            Way(new[] { millBridgeE, millEast, hwyMill }, ("highway", "tertiary"), ("name", "Mill Road"), ("maxspeed", "60"));

            // Forest service road with an emergency-access gate, seasonal, through an avalanche path.
            var gate = N(-6000, -100, ("barrier", "gate"), ("access", "no"), ("emergency", "yes"), ("note", "Locked gate – key with fire department (SAMPLE)"));
            Way(new[] { hwyFsr, gate, N(-6000, 1500), N(-5800, 3000), N(-6000, 4200) }, ("highway", "unclassified"), ("name", "Sample Creek FSR"), ("surface", "gravel"), ("seasonal", "yes"), ("maxspeed", "40"));
            Ring(new[] { (-6400.0, 2600.0), (-5400.0, 2600.0), (-5400.0, 3400.0), (-6400.0, 3400.0) }, ("hazard", "avalanche"), ("name", "Sample Creek avalanche path"));
            N(-5900, 3800, ("hazard", "rockfall"), ("name", "Rockfall – Sample Creek bluffs"));
            N(-6000, 4250, ("amenity", "shelter"), ("shelter_type", "basic_hut"), ("name", "Sample Creek Cabin (SAMPLE)"));

            // Water, park and town area.
            Way(Enumerable.Range(0, 7).Select(k => N(2500 + (k % 2 == 0 ? 0 : 60), -3000 + k * 1000)).ToArray(), ("waterway", "river"), ("name", "Sample River"));
            Ring(Circle(-2500, 1500, 600), ("natural", "water"), ("name", "Fictional Lake"));
            Ring(new[] { (StreetX(1), AvenueY(5)), (StreetX(2), AvenueY(5)), (StreetX(2), AvenueY(6)), (StreetX(1), AvenueY(6)) }, ("leisure", "park"), ("name", "Sample Park"));
            Ring(new[] { (-600.0, -40.0), (600.0, -40.0), (600.0, 1160.0), (-600.0, 1160.0) }, ("landuse", "residential"));

            // A multipolygon relation built from two open ways (tests ring assembly), with an island (inner ring).
            var m1 = N(1200, 2000);
            var m2 = N(2000, 2000);
            var w1 = Way(new[] { m1, N(1200, 2600), N(2000, 2600), m2 });
            var w2 = Way(new[] { m1, N(1600, 1800), m2 });
            var island = Ring(Circle(1600, 2250, 120, 8));
            var rid = _next++;
            _relations.Add((rid, new[] { ("way", w1, "outer"), ("way", w2, "outer"), ("way", island, "inner") },
                new[] { ("type", "multipolygon"), ("natural", "wetland"), ("name", "Sample Marsh") }));

            // Places.
            N(0, 560, ("place", "town"), ("name", "Sampleton"), ("population", "4200"));
            N(-6000, 4000, ("place", "hamlet"), ("name", "North Sample"));
            N(-2500, 2200, ("place", "locality"), ("name", "Example Lake"));

            // Facilities (all fictitious, marked SAMPLE).
            var h = HospitalPoint;
            Node(h, ("amenity", "hospital"), ("emergency", "yes"), ("name", "Sampleton General Hospital (SAMPLE)"), ("addr:housenumber", "650"), ("addr:street", "Birch Street"), ("phone", "+1-555-0100"));
            N(StreetX(1) + 120, AvenueY(6) + 120, ("aeroway", "helipad"), ("name", "Sampleton General Helipad (SAMPLE)"));
            N(StreetX(7) + 60, AvenueY(2) + 60, ("amenity", "hospital"), ("emergency", "no"), ("name", "Sample Valley Clinic Hospital (SAMPLE)"));
            Ring(new[] { (StreetX(5) + 10, AvenueY(1) + 10), (StreetX(6) - 10, AvenueY(1) + 10), (StreetX(6) - 10, AvenueY(2) - 10), (StreetX(5) + 10, AvenueY(2) - 10) },
                ("amenity", "school"), ("name", "Sampleton Elementary School (SAMPLE)"));
            N(StreetX(2) + 80, AvenueY(3) + 80, ("amenity", "school"), ("name", "Sampleton Secondary School (SAMPLE)"));
            var f = FireHallPoint;
            Node(f, ("amenity", "fire_station"), ("name", "Sampleton Fire Hall No. 1 (SAMPLE)"));
            N(StreetX(3) + 40, AvenueY(3) + 30, ("amenity", "police"), ("name", "Sampleton Police Office (SAMPLE)"));
            N(StreetX(5) + 30, AvenueY(4) + 30, ("emergency", "ambulance_station"), ("name", "Sampleton Ambulance Station (SAMPLE)"));
            N(-3000, -900, ("aeroway", "aerodrome"), ("name", "Sampleton Airstrip (SAMPLE)"), ("icao", "ZZZZ"));
            N(StreetX(4) + 40, AvenueY(5) - 30, ("amenity", "pharmacy"), ("name", "Sample Drugs (SAMPLE)"));
            N(StreetX(4) + 50, -360, ("amenity", "fuel"), ("name", "Highway Fuel (SAMPLE)"), ("fuel:diesel", "yes"), ("hgv", "yes"));
            N(StreetX(6) + 40, AvenueY(0) + 30, ("amenity", "fuel"), ("name", "Town Gas (SAMPLE)"));
            N(-4000, -440, ("highway", "rest_area"), ("name", "Sample Valley Rest Area (SAMPLE)"));
            N(-4010, -450, ("amenity", "toilets"));
            N(5000, -430, ("parking", "layby"), ("amenity", "parking"), ("name", "Brake check (SAMPLE)"));
            N(StreetX(4) - 30, AvenueY(4) + 40, ("amenity", "social_facility"), ("social_facility", "shelter"), ("name", "Sampleton Community Hall – reception centre (SAMPLE)"));
            N(StreetX(3) + 80, AvenueY(7) + 40, ("emergency", "assembly_point"), ("name", "Sample Park muster point (SAMPLE)"));

            // AEDs, some with a recorded check date.
            Node(new GeoPoint(h.Lat + 0.0001, h.Lon), ("emergency", "defibrillator"), ("name", "Hospital main entrance AED"), ("defibrillator:location", "Inside main entrance, left wall"), ("check_date", "2026-03-14"), ("access", "yes"), ("opening_hours", "24/7"));
            N(StreetX(5) + 60, AvenueY(1) + 40, ("emergency", "defibrillator"), ("name", "Elementary school AED"), ("defibrillator:location", "Gym lobby"), ("check_date", "2025-09-02"), ("opening_hours", "Mo-Fr 08:00-16:00"));
            N(StreetX(4) - 20, AvenueY(4) + 30, ("emergency", "defibrillator"), ("defibrillator:location", "Community hall kitchen"), ("access", "customers"));
            N(-4000, -455, ("emergency", "defibrillator"), ("name", "Rest area AED"), ("check_date", "2024-06-20"), ("indoor", "no"));

            // Hydrants along Main Street and 1st Avenue, a water tank by the FSR.
            for (var i = 0; i < 8; i++)
                N(StreetX(4) + 12, AvenueY(i) + 70, ("emergency", "fire_hydrant"), ("fire_hydrant:type", "pillar"), ("ref", $"H-{100 + i}"));
            for (var j = 0; j < 8; j++)
                N(StreetX(j) + 70, AvenueY(0) - 12, ("emergency", "fire_hydrant"), ("fire_hydrant:type", "pillar"), ("ref", $"H-{200 + j}"));
            N(-5980, 200, ("emergency", "water_tank"), ("water_tank:volume", "45000"), ("name", "FSR water tank (SAMPLE)"));

            // Address points: Main Street (100–790) and 1st Avenue (10–150).
            for (var i = 0; i < 8; i++)
                for (var k = 0; k < 8; k++)
                {
                    var number = (i + 1) * 100 + k * 10;
                    var side = k % 2 == 0 ? -18 : 18;
                    N(StreetX(4) + side, AvenueY(i) + 15 + k * 18, ("addr:housenumber", number.ToString(CultureInfo.InvariantCulture)), ("addr:street", "Main Street"), ("addr:city", "Sampleton"));
                }
            for (var j = 0; j < 8; j++)
                N(StreetX(j) + 40, AvenueY(0) + 18, ("addr:housenumber", (10 + j * 20).ToString(CultureInfo.InvariantCulture)), ("addr:street", "1st Avenue"), ("addr:city", "Sampleton"));
        }

        public void Save(Stream stream)
        {
            var inv = CultureInfo.InvariantCulture;
            using var w = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true });
            w.WriteStartDocument();
            w.WriteStartElement("osm");
            w.WriteAttributeString("version", "0.6");
            w.WriteAttributeString("generator", "Field Map SampleRegion – FICTITIOUS SAMPLE DATA");
            w.WriteComment(" Sample County: entirely fictitious names and features for testing Field Map. Not real data. ");
            void Tags((string, string)[] tags)
            {
                foreach (var (k, v) in tags)
                {
                    w.WriteStartElement("tag");
                    w.WriteAttributeString("k", k);
                    w.WriteAttributeString("v", v);
                    w.WriteEndElement();
                }
            }
            foreach (var (id, p, tags) in _nodes)
            {
                w.WriteStartElement("node");
                w.WriteAttributeString("id", id.ToString(inv));
                w.WriteAttributeString("lat", p.Lat.ToString("0.0000000", inv));
                w.WriteAttributeString("lon", p.Lon.ToString("0.0000000", inv));
                Tags(tags);
                w.WriteEndElement();
            }
            foreach (var (id, refs, tags) in _ways)
            {
                w.WriteStartElement("way");
                w.WriteAttributeString("id", id.ToString(inv));
                foreach (var r in refs)
                {
                    w.WriteStartElement("nd");
                    w.WriteAttributeString("ref", r.ToString(inv));
                    w.WriteEndElement();
                }
                Tags(tags);
                w.WriteEndElement();
            }
            foreach (var (id, members, tags) in _relations)
            {
                w.WriteStartElement("relation");
                w.WriteAttributeString("id", id.ToString(inv));
                foreach (var (type, r, role) in members)
                {
                    w.WriteStartElement("member");
                    w.WriteAttributeString("type", type);
                    w.WriteAttributeString("ref", r.ToString(inv));
                    w.WriteAttributeString("role", role);
                    w.WriteEndElement();
                }
                Tags(tags);
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndDocument();
        }
    }
}
