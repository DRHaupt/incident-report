using System.Text;
using FieldMap.Core.Geo;

namespace FieldMap.Core.Routing;

[Flags]
public enum SegmentFlags : uint
{
    None = 0,
    /// <summary>Only From→To may be travelled by vehicles.</summary>
    OnewayForward = 1 << 0,
    /// <summary>Only To→From may be travelled by vehicles.</summary>
    OnewayBackward = 1 << 1,
    /// <summary>No motor vehicles (access=no/private, motor_vehicle=no…).</summary>
    NoMotor = 1 << 2,
    /// <summary>Motor vehicles only with emergency=yes (emergency access road).</summary>
    EmergencyAccess = 1 << 3,
    NoHgv = 1 << 4,
    /// <summary>Footway, path, steps, cycleway: walking only.</summary>
    WalkOnly = 1 << 5,
    NoWalk = 1 << 6,
    Toll = 1 << 7,
    Ferry = 1 << 8,
    Bridge = 1 << 9,
    Tunnel = 1 << 10,
    Seasonal = 1 << 11,
    Unpaved = 1 << 12,
    Roundabout = 1 << 13,
    Link = 1 << 14,
    /// <summary>The segment's name contains "FSR" / "Forest Service": radio-controlled resource road.</summary>
    ForestService = 1 << 15
}

[Flags]
public enum NodeFlags : byte
{
    None = 0,
    /// <summary>Locked gate or barrier: no motor vehicles.</summary>
    BlocksMotor = 1,
    /// <summary>…but emergency vehicles may pass (emergency=yes on the barrier).</summary>
    EmergencyPasses = 2,
    /// <summary>An open gate or cattle guard: passable, mentioned in directions.</summary>
    Gate = 4,
    /// <summary>Blocks walking too (rare: fences mapped as barrier nodes).</summary>
    BlocksWalk = 8
}

public enum RoadClass : byte
{
    Motorway, Trunk, Primary, Secondary, Tertiary, Minor, Residential, Service, Track, Path, Ferry
}

/// <summary>
/// The routable road network of one region: graph nodes (junctions, ends, barriers) and segments (way pieces between
/// them) with their attributes and shape, in compressed-sparse-row arrays. Saved as one binary file (routing.graph).
/// </summary>
public sealed class RoadGraph
{
    private const uint Magic = 0x47524D46; // "FMRG"
    public const int FormatVersion = 1;

    // Nodes
    public int NodeCount { get; internal set; }
    internal int[] NodeLat = Array.Empty<int>();   // 1e-7 degrees
    internal int[] NodeLon = Array.Empty<int>();
    internal NodeFlags[] NodeFlag = Array.Empty<NodeFlags>();

    // Segments
    public int SegmentCount { get; internal set; }
    internal int[] SegFrom = Array.Empty<int>();
    internal int[] SegTo = Array.Empty<int>();
    internal float[] SegLength = Array.Empty<float>();      // metres
    internal byte[] SegSpeed = Array.Empty<byte>();         // km/h (maxspeed or class default)
    internal RoadClass[] SegClass = Array.Empty<RoadClass>();
    internal SegmentFlags[] SegFlags = Array.Empty<SegmentFlags>();
    internal ushort[] SegMaxHeight = Array.Empty<ushort>(); // centimetres, 0 = none
    internal ushort[] SegMaxWeight = Array.Empty<ushort>(); // 100 kg units, 0 = none
    internal ushort[] SegMaxLength = Array.Empty<ushort>(); // decimetres, 0 = none
    internal int[] SegName = Array.Empty<int>();
    internal int[] SegRef = Array.Empty<int>();
    internal int[] SegShapeStart = Array.Empty<int>();      // into ShapeLat/Lon; intermediate points only
    internal int[] ShapeLat = Array.Empty<int>();
    internal int[] ShapeLon = Array.Empty<int>();
    internal long[] SegWayId = Array.Empty<long>();

    // Adjacency: entries for node n are AdjEntry[AdjStart[n] .. AdjStart[n+1]); entry = segment*2 + direction (0 = From→To).
    internal int[] AdjStart = Array.Empty<int>();
    internal int[] AdjEntry = Array.Empty<int>();

    internal string[] Strings = { "" };

    private SpatialIndex? _index;

    public GeoPoint Node(int n) => new(NodeLat[n] / 1e7, NodeLon[n] / 1e7);

    public string Name(int segment) => Strings[SegName[segment]];
    public string Ref(int segment) => Strings[SegRef[segment]];
    public float Length(int segment) => SegLength[segment];
    public RoadClass Class(int segment) => SegClass[segment];
    public SegmentFlags Flags(int segment) => SegFlags[segment];
    public double? MaxHeightMeters(int s) => SegMaxHeight[s] == 0 ? null : SegMaxHeight[s] / 100.0;
    public double? MaxWeightTonnes(int s) => SegMaxWeight[s] == 0 ? null : SegMaxWeight[s] / 10.0;
    public double? MaxLengthMeters(int s) => SegMaxLength[s] == 0 ? null : SegMaxLength[s] / 10.0;
    public int SpeedKmh(int s) => SegSpeed[s];
    public long WayId(int s) => SegWayId[s];
    public int From(int s) => SegFrom[s];
    public int To(int s) => SegTo[s];

    /// <summary>Segment points from start to end in the given direction (including both graph nodes).</summary>
    public List<GeoPoint> Shape(int segment, bool forward = true)
    {
        var pts = new List<GeoPoint> { Node(SegFrom[segment]) };
        for (var i = SegShapeStart[segment]; i < SegShapeStart[segment + 1]; i++)
            pts.Add(new GeoPoint(ShapeLat[i] / 1e7, ShapeLon[i] / 1e7));
        pts.Add(Node(SegTo[segment]));
        if (!forward)
            pts.Reverse();
        return pts;
    }

    public GeoBounds Bounds
    {
        get
        {
            var b = GeoBounds.Empty;
            for (var i = 0; i < NodeCount; i++)
                b = b.Include(NodeLat[i] / 1e7, NodeLon[i] / 1e7);
            return b;
        }
    }

    internal SpatialIndex Index => _index ??= new SpatialIndex(this);

    // ------------------------------------------------------------------ file format

    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new BinaryWriter(fs, Encoding.UTF8);
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(NodeCount);
        w.Write(SegmentCount);
        w.Write(ShapeLat.Length);
        w.Write(AdjEntry.Length);
        w.Write(Strings.Length);
        foreach (var s in Strings)
            w.Write(s);
        Write(w, NodeLat); Write(w, NodeLon); w.Write(Array.ConvertAll(NodeFlag, f => (byte)f));
        Write(w, SegFrom); Write(w, SegTo);
        foreach (var l in SegLength) w.Write(l);
        w.Write(SegSpeed);
        w.Write(Array.ConvertAll(SegClass, c => (byte)c));
        foreach (var f in SegFlags) w.Write((uint)f);
        foreach (var v in SegMaxHeight) w.Write(v);
        foreach (var v in SegMaxWeight) w.Write(v);
        foreach (var v in SegMaxLength) w.Write(v);
        Write(w, SegName); Write(w, SegRef); Write(w, SegShapeStart);
        Write(w, ShapeLat); Write(w, ShapeLon);
        foreach (var v in SegWayId) w.Write(v);
        Write(w, AdjStart); Write(w, AdjEntry);
        w.Write(Magic);
    }

    private static void Write(BinaryWriter w, int[] a)
    {
        var bytes = new byte[a.Length * 4];
        Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
        w.Write(bytes);
    }

    private static int[] ReadInts(BinaryReader r, int n)
    {
        var bytes = r.ReadBytes(n * 4);
        if (bytes.Length != n * 4)
            throw new InvalidDataException("Routing graph file is truncated.");
        var a = new int[n];
        Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length);
        return a;
    }

    public static RoadGraph Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a Field Map routing graph.");
        var version = r.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Routing graph format {version} is not supported (expected {FormatVersion}). Update the map data.");
        var g = new RoadGraph();
        var nodes = r.ReadInt32();
        var segs = r.ReadInt32();
        var shapes = r.ReadInt32();
        var adj = r.ReadInt32();
        var strings = r.ReadInt32();
        g.NodeCount = nodes;
        g.SegmentCount = segs;
        g.Strings = new string[strings];
        for (var i = 0; i < strings; i++)
            g.Strings[i] = r.ReadString();
        g.NodeLat = ReadInts(r, nodes); g.NodeLon = ReadInts(r, nodes);
        g.NodeFlag = Array.ConvertAll(r.ReadBytes(nodes), b => (NodeFlags)b);
        g.SegFrom = ReadInts(r, segs); g.SegTo = ReadInts(r, segs);
        g.SegLength = new float[segs];
        for (var i = 0; i < segs; i++) g.SegLength[i] = r.ReadSingle();
        g.SegSpeed = r.ReadBytes(segs);
        g.SegClass = Array.ConvertAll(r.ReadBytes(segs), b => (RoadClass)b);
        g.SegFlags = new SegmentFlags[segs];
        for (var i = 0; i < segs; i++) g.SegFlags[i] = (SegmentFlags)r.ReadUInt32();
        g.SegMaxHeight = new ushort[segs];
        for (var i = 0; i < segs; i++) g.SegMaxHeight[i] = r.ReadUInt16();
        g.SegMaxWeight = new ushort[segs];
        for (var i = 0; i < segs; i++) g.SegMaxWeight[i] = r.ReadUInt16();
        g.SegMaxLength = new ushort[segs];
        for (var i = 0; i < segs; i++) g.SegMaxLength[i] = r.ReadUInt16();
        g.SegName = ReadInts(r, segs); g.SegRef = ReadInts(r, segs); g.SegShapeStart = ReadInts(r, segs + 1);
        g.ShapeLat = ReadInts(r, shapes); g.ShapeLon = ReadInts(r, shapes);
        g.SegWayId = new long[segs];
        for (var i = 0; i < segs; i++) g.SegWayId[i] = r.ReadInt64();
        g.AdjStart = ReadInts(r, nodes + 1); g.AdjEntry = ReadInts(r, adj);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("Routing graph file is corrupt (bad trailer).");
        return g;
    }
}

/// <summary>Grid index (≈1 km cells) of segments for snapping a position to the nearest road.</summary>
internal sealed class SpatialIndex
{
    private const double Cell = 0.01;
    private readonly long[] _keys;
    private readonly int[] _segments;
    private readonly RoadGraph _g;

    public SpatialIndex(RoadGraph g)
    {
        _g = g;
        var entries = new List<(long Key, int Seg)>(g.SegmentCount + g.SegmentCount / 4);
        for (var s = 0; s < g.SegmentCount; s++)
        {
            var b = GeoBounds.Of(g.Shape(s));
            for (var y = CellOf(b.MinLat); y <= CellOf(b.MaxLat); y++)
                for (var x = CellOf(b.MinLon); x <= CellOf(b.MaxLon); x++)
                    entries.Add((Key(y, x), s));
        }
        entries.Sort((a, b) => a.Key.CompareTo(b.Key));
        _keys = entries.Select(e => e.Key).ToArray();
        _segments = entries.Select(e => e.Seg).ToArray();
    }

    private static int CellOf(double deg) => (int)Math.Floor(deg / Cell);

    private static long Key(int y, int x) => ((long)y << 32) | (uint)x;

    /// <summary>Segments in cells overlapping the bounds.</summary>
    public IEnumerable<int> Query(GeoBounds b)
    {
        var seen = new HashSet<int>();
        for (var y = CellOf(b.MinLat); y <= CellOf(b.MaxLat); y++)
            for (var x = CellOf(b.MinLon); x <= CellOf(b.MaxLon); x++)
            {
                var key = Key(y, x);
                var i = Array.BinarySearch(_keys, key);
                if (i < 0)
                    continue;
                while (i > 0 && _keys[i - 1] == key)
                    i--;
                for (; i < _keys.Length && _keys[i] == key; i++)
                    if (seen.Add(_segments[i]))
                        yield return _segments[i];
            }
    }
}
