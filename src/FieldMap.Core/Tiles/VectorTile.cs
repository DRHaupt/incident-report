using System.IO.Compression;
using FieldMap.Core.Geo;
using FieldMap.Core.Osm;

namespace FieldMap.Core.Tiles;

public enum GeomType : byte
{
    Unknown = 0,
    Point = 1,
    Line = 2,
    Polygon = 3
}

/// <summary>A decoded vector-tile feature: geometry in tile units (0..Extent, y down) and its properties.</summary>
public sealed class VectorFeature
{
    public GeomType Type { get; init; }

    /// <summary>Points: one part per point. Lines: one part per line. Polygons: rings (outer clockwise, holes anticlockwise).</summary>
    public List<Vec[]> Parts { get; init; } = new();

    public Dictionary<string, object> Props { get; init; } = new();

    public string? Str(string key) => Props.TryGetValue(key, out var v) ? v as string ?? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    public long Int(string key, long fallback = 0) => Props.TryGetValue(key, out var v) ? v switch
    {
        long l => l,
        ulong u => (long)u,
        int i => i,
        double d => (long)d,
        bool b => b ? 1 : 0,
        string s when long.TryParse(s, out var p) => p,
        _ => fallback
    } : fallback;

    public bool Flag(string key) => Props.TryGetValue(key, out var v) && v is true or 1L or "yes";
}

public sealed class VectorLayer
{
    public string Name { get; init; } = "";
    public int Extent { get; init; } = 4096;
    public List<VectorFeature> Features { get; } = new();
}

/// <summary>A Mapbox Vector Tile (v2): encode to and decode from the protobuf format, optionally gzip'd as in MBTiles.</summary>
public sealed class VectorTile
{
    public List<VectorLayer> Layers { get; } = new();

    public VectorLayer? Layer(string name) => Layers.FirstOrDefault(l => l.Name == name);

    // ------------------------------------------------------------------ encode

    public byte[] Encode()
    {
        var tile = new ProtoWriter();
        foreach (var layer in Layers)
        {
            if (layer.Features.Count == 0)
                continue;
            var lw = new ProtoWriter();
            lw.UInt(15, 2);
            lw.String(1, layer.Name);
            var keys = new Dictionary<string, uint>();
            var values = new Dictionary<object, uint>();
            var valueList = new List<object>();
            foreach (var f in layer.Features)
            {
                var fw = new ProtoWriter();
                var tags = new List<uint>();
                foreach (var (k, v) in f.Props)
                {
                    if (!keys.TryGetValue(k, out var ki))
                        keys[k] = ki = (uint)keys.Count;
                    var norm = Normalise(v);
                    if (!values.TryGetValue(norm, out var vi))
                    {
                        values[norm] = vi = (uint)valueList.Count;
                        valueList.Add(norm);
                    }
                    tags.Add(ki);
                    tags.Add(vi);
                }
                if (tags.Count > 0)
                    fw.PackedUInt(2, tags);
                fw.UInt(3, (ulong)f.Type);
                fw.PackedUInt(4, EncodeGeometry(f));
                lw.Message(2, fw);
            }
            foreach (var k in keys.OrderBy(k => k.Value))
                lw.String(3, k.Key);
            foreach (var v in valueList)
            {
                var vw = new ProtoWriter();
                switch (v)
                {
                    case string s: vw.String(1, s); break;
                    case double d: vw.Double(3, d); break;
                    case long l when l < 0: vw.SInt(6, l); break;
                    case long l: vw.UInt(5, (ulong)l); break;
                    case bool b: vw.UInt(7, b ? 1UL : 0UL); break;
                }
                lw.Message(4, vw);
            }
            lw.UInt(5, (ulong)layer.Extent);
            tile.Message(3, lw);
        }
        return tile.ToArray();
    }

    private static object Normalise(object v) => v switch
    {
        int i => (long)i,
        uint u => (long)u,
        ulong u => (long)u,
        float f => (double)f,
        string or long or double or bool => v,
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };

    private static IEnumerable<uint> EncodeGeometry(VectorFeature f)
    {
        var cmds = new List<uint>();
        int cx = 0, cy = 0;
        uint Cmd(int id, int count) => (uint)((id & 7) | (count << 3));
        uint Z(int v) => (uint)((v << 1) ^ (v >> 31));
        if (f.Type == GeomType.Point)
        {
            cmds.Add(Cmd(1, f.Parts.Count));
            foreach (var p in f.Parts)
            {
                int x = (int)Math.Round(p[0].X), y = (int)Math.Round(p[0].Y);
                cmds.Add(Z(x - cx));
                cmds.Add(Z(y - cy));
                (cx, cy) = (x, y);
            }
            return cmds;
        }
        foreach (var part in f.Parts)
        {
            var pts = Dedupe(part, f.Type == GeomType.Polygon);
            var min = f.Type == GeomType.Polygon ? 3 : 2;
            if (pts.Count < min)
                continue;
            cmds.Add(Cmd(1, 1));
            cmds.Add(Z(pts[0].x - cx));
            cmds.Add(Z(pts[0].y - cy));
            (cx, cy) = pts[0];
            cmds.Add(Cmd(2, pts.Count - 1));
            for (var i = 1; i < pts.Count; i++)
            {
                cmds.Add(Z(pts[i].x - cx));
                cmds.Add(Z(pts[i].y - cy));
                (cx, cy) = pts[i];
            }
            if (f.Type == GeomType.Polygon)
                cmds.Add(Cmd(7, 1));
        }
        return cmds;
    }

    private static List<(int x, int y)> Dedupe(Vec[] part, bool polygon)
    {
        var pts = new List<(int x, int y)>(part.Length);
        foreach (var p in part)
        {
            var q = ((int)Math.Round(p.X), (int)Math.Round(p.Y));
            if (pts.Count == 0 || pts[^1] != q)
                pts.Add(q);
        }
        if (polygon && pts.Count > 1 && pts[0] == pts[^1])
            pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    // ------------------------------------------------------------------ decode

    public static VectorTile Decode(byte[] data)
    {
        if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
            data = Gunzip(data);
        var tile = new VectorTile();
        var r = new ProtoReader(data);
        while (r.Next(out var f, out var w))
        {
            if (f == 3 && w == 2)
                tile.Layers.Add(DecodeLayer(r.ReadMessage()));
            else
                r.Skip(w);
        }
        return tile;
    }

    private static VectorLayer DecodeLayer(ProtoReader r)
    {
        var name = "";
        var extent = 4096;
        var keys = new List<string>();
        var values = new List<object>();
        var rawFeatures = new List<ProtoReader>();
        while (r.Next(out var f, out var w))
        {
            switch (f)
            {
                case 1 when w == 2: name = r.ReadString(); break;
                case 2 when w == 2: rawFeatures.Add(r.ReadMessage()); break;
                case 3 when w == 2: keys.Add(r.ReadString()); break;
                case 4 when w == 2: values.Add(DecodeValue(r.ReadMessage())); break;
                case 5 when w == 0: extent = r.ReadInt32(); break;
                default: r.Skip(w); break;
            }
        }
        var layer = new VectorLayer { Name = name, Extent = extent };
        foreach (var raw in rawFeatures)
        {
            var fr = raw;
            var type = GeomType.Unknown;
            var tags = new List<uint>();
            var geom = new List<uint>();
            while (fr.Next(out var f, out var w))
            {
                switch (f)
                {
                    case 2 when w == 2:
                    {
                        var s = fr.ReadMessage();
                        while (!s.AtEnd) tags.Add(s.ReadUInt32());
                        break;
                    }
                    case 3 when w == 0: type = (GeomType)fr.ReadInt32(); break;
                    case 4 when w == 2:
                    {
                        var s = fr.ReadMessage();
                        while (!s.AtEnd) geom.Add(s.ReadUInt32());
                        break;
                    }
                    default: fr.Skip(w); break;
                }
            }
            var feature = new VectorFeature { Type = type, Parts = DecodeGeometry(type, geom) };
            for (var i = 0; i + 1 < tags.Count; i += 2)
                if (tags[i] < keys.Count && tags[i + 1] < values.Count)
                    feature.Props[keys[(int)tags[i]]] = values[(int)tags[i + 1]];
            layer.Features.Add(feature);
        }
        return layer;
    }

    private static object DecodeValue(ProtoReader r)
    {
        object value = "";
        while (r.Next(out var f, out var w))
        {
            switch (f)
            {
                case 1: value = r.ReadString(); break;
                case 2: value = (double)r.ReadFloat(); break;
                case 3: value = r.ReadDouble(); break;
                case 4: value = r.ReadInt64(); break;
                case 5: value = (long)r.ReadVarint(); break;
                case 6: value = r.ReadSInt64(); break;
                case 7: value = r.ReadVarint() != 0; break;
                default: r.Skip(w); break;
            }
        }
        return value;
    }

    private static List<Vec[]> DecodeGeometry(GeomType type, List<uint> geom)
    {
        var parts = new List<Vec[]>();
        var current = new List<Vec>();
        int x = 0, y = 0, i = 0;
        while (i < geom.Count)
        {
            var cmd = geom[i++];
            var id = (int)(cmd & 7);
            var count = (int)(cmd >> 3);
            switch (id)
            {
                case 1:
                case 2:
                    for (var k = 0; k < count && i + 1 < geom.Count; k++)
                    {
                        x += UnZig(geom[i++]);
                        y += UnZig(geom[i++]);
                        if (id == 1)
                        {
                            if (current.Count > 0)
                                parts.Add(current.ToArray());
                            current = new List<Vec>();
                        }
                        current.Add(new Vec(x, y));
                    }
                    break;
                case 7:
                    if (current.Count > 0)
                        current.Add(current[0]);
                    break;
            }
        }
        if (current.Count > 0)
            parts.Add(current.ToArray());
        return parts;
    }

    private static int UnZig(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data);
        return ms.ToArray();
    }

    public static byte[] Gunzip(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }
}
