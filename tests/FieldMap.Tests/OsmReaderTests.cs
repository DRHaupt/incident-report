using FieldMap.Core.Build;
using FieldMap.Core.Osm;

namespace FieldMap.Tests;

public class OsmReaderTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    private sealed class Counter : IOsmHandler
    {
        public bool WantNodes => true;
        public bool WantWays => true;
        public bool WantRelations => true;
        public readonly Dictionary<long, (double Lat, double Lon, string Tags)> Nodes = new();
        public readonly Dictionary<long, (long[] Refs, string Tags)> Ways = new();
        public readonly Dictionary<long, (OsmMember[] Members, string Tags)> Relations = new();
        public void Node(long id, double lat, double lon, OsmTags tags) => Nodes[id] = (lat, lon, tags.ToString());
        public void Way(long id, IReadOnlyList<long> refs, OsmTags tags) => Ways[id] = (refs.ToArray(), tags.ToString());
        public void Relation(long id, IReadOnlyList<OsmMember> members, OsmTags tags) => Relations[id] = (members.ToArray(), tags.ToString());
    }

    [Fact]
    public void Pbf_written_by_libosmium_matches_the_xml()
    {
        var xml = new Counter();
        var pbf = new Counter();
        OsmFile.Read(Data("sample-county.osm"), xml);
        OsmFile.Read(Data("sample-county.osm.pbf"), pbf);
        Assert.Equal(xml.Nodes.Count, pbf.Nodes.Count);
        Assert.Equal(xml.Ways.Count, pbf.Ways.Count);
        Assert.Single(pbf.Relations);
        foreach (var (id, n) in xml.Nodes)
        {
            var p = pbf.Nodes[id];
            Assert.True(Math.Abs(n.Lat - p.Lat) < 1e-7 && Math.Abs(n.Lon - p.Lon) < 1e-7, $"node {id}");
            Assert.Equal(n.Tags, p.Tags);
        }
        foreach (var (id, w) in xml.Ways)
        {
            Assert.Equal(w.Refs, pbf.Ways[id].Refs);
            Assert.Equal(w.Tags, pbf.Ways[id].Tags);
        }
        var (members, tags) = pbf.Relations.Values.Single();
        Assert.Equal(3, members.Length);
        Assert.Contains("inner", members.Select(m => m.Role));
        Assert.Contains("type=multipolygon", tags);
    }

    [Fact]
    public void Extracting_pbf_and_xml_gives_the_same_map_data()
    {
        var a = OsmExtractor.Extract(Data("sample-county.osm"));
        var b = OsmExtractor.Extract(Data("sample-county.osm.pbf"));
        Assert.Equal(a.Summary, b.Summary);
        Assert.Equal(a.Pois.Select(p => p.Name).OrderBy(x => x), b.Pois.Select(p => p.Name).OrderBy(x => x));
        Assert.Equal(a.Barriers.Count, b.Barriers.Count);
    }

    [Fact]
    public void Overlapping_extracts_are_merged_without_duplicates()
    {
        // The same area twice (as BC and Alberta overlap along the border): every element is kept once.
        var once = OsmExtractor.Extract(Data("sample-county.osm"));
        var twice = OsmExtractor.Extract(new[] { Data("sample-county.osm"), Data("sample-county.osm.pbf") });
        Assert.Equal(once.Summary, twice.Summary);
    }

    [Fact]
    public void Truncated_pbf_is_reported()
    {
        var bytes = File.ReadAllBytes(Data("sample-county.osm.pbf"));
        var path = Path.Combine(Path.GetTempPath(), "truncated-" + Guid.NewGuid().ToString("N")[..6] + ".osm.pbf");
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        try
        {
            Assert.Throws<InvalidDataException>(() => OsmFile.Read(path, new Counter()));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
