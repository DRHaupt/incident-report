using System.Buffers.Binary;
using System.IO.Compression;

namespace FieldMap.Core.Osm;

/// <summary>
/// Streaming reader for OpenStreetMap PBF files (the format of the Geofabrik extracts). Supports zlib-compressed and raw
/// blobs, dense and plain nodes, ways and relations. Blocks a handler doesn't want are decompressed but not decoded.
/// </summary>
public sealed class OsmPbfReader
{
    private static readonly IReadOnlyList<long> NoRefs = Array.Empty<long>();

    public void Read(string path, IOsmHandler handler, CancellationToken cancel = default, IProgress<double>? progress = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        Read(stream, handler, cancel, progress);
    }

    public void Read(Stream stream, IOsmHandler handler, CancellationToken cancel = default, IProgress<double>? progress = null)
    {
        var lenBuf = new byte[4];
        var length = stream.CanSeek ? stream.Length : 0;
        var lastReport = 0L;
        var sawHeader = false;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (!ReadExactly(stream, lenBuf))
                break;
            var headerLen = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            if (headerLen is <= 0 or > 64 * 1024)
                throw new InvalidDataException("Not an OSM PBF file (bad blob header length).");
            var headerBytes = new byte[headerLen];
            if (!ReadExactly(stream, headerBytes))
                throw new InvalidDataException("Truncated PBF file.");
            string type = "";
            var dataSize = 0;
            var hr = new ProtoReader(headerBytes);
            while (hr.Next(out var f, out var w))
            {
                if (f == 1 && w == 2) type = hr.ReadString();
                else if (f == 3 && w == 0) dataSize = hr.ReadInt32();
                else hr.Skip(w);
            }
            if (dataSize is <= 0 or > 64 * 1024 * 1024)
                throw new InvalidDataException("Bad PBF blob size.");
            var blob = new byte[dataSize];
            if (!ReadExactly(stream, blob))
                throw new InvalidDataException("Truncated PBF file.");

            if (type == "OSMHeader")
            {
                CheckHeader(Decompress(blob));
                sawHeader = true;
            }
            else if (type == "OSMData")
            {
                if (!sawHeader)
                    throw new InvalidDataException("PBF file has no OSMHeader block.");
                DecodePrimitiveBlock(Decompress(blob), handler);
            }

            if (length > 0 && progress != null && stream.Position - lastReport > length / 200)
            {
                lastReport = stream.Position;
                progress.Report((double)stream.Position / length);
            }
        }
        progress?.Report(1);
    }

    private static bool ReadExactly(Stream s, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                if (read == 0)
                    return false;
                throw new InvalidDataException("Truncated PBF file.");
            }
            read += n;
        }
        return true;
    }

    private static byte[] Decompress(byte[] blob)
    {
        var r = new ProtoReader(blob);
        byte[]? raw = null;
        var rawSize = 0;
        (int Offset, int Length)? zlib = null;
        while (r.Next(out var f, out var w))
        {
            switch (f)
            {
                case 1 when w == 2: raw = r.ReadBytes(); break;
                case 2 when w == 0: rawSize = r.ReadInt32(); break;
                case 3 when w == 2: zlib = r.ReadBytesRange(); break;
                case 4 or 5 or 6 or 7: throw new NotSupportedException("This PBF uses LZMA/bzip2/LZ4/Zstd compression; only zlib is supported (Geofabrik uses zlib).");
                default: r.Skip(w); break;
            }
        }
        if (raw != null)
            return raw;
        if (zlib is not { } z)
            throw new InvalidDataException("Empty PBF blob.");
        var output = new byte[rawSize];
        using var zs = new ZLibStream(new MemoryStream(blob, z.Offset, z.Length), CompressionMode.Decompress);
        var read = 0;
        while (read < rawSize)
        {
            var n = zs.Read(output, read, rawSize - read);
            if (n == 0)
                throw new InvalidDataException("Corrupt zlib data in PBF blob.");
            read += n;
        }
        return output;
    }

    private static void CheckHeader(byte[] data)
    {
        var r = new ProtoReader(data);
        while (r.Next(out var f, out var w))
        {
            if (f == 4 && w == 2)
            {
                var feature = r.ReadString();
                if (feature is not ("OsmSchema-V0.6" or "DenseNodes" or "HistoricalInformation"))
                    throw new NotSupportedException($"PBF requires unsupported feature '{feature}'.");
            }
            else
            {
                r.Skip(w);
            }
        }
    }

    private void DecodePrimitiveBlock(byte[] data, IOsmHandler handler)
    {
        var r = new ProtoReader(data);
        var strings = new List<string>();
        var groups = new List<(int Offset, int Length)>();
        long granularity = 100, latOffset = 0, lonOffset = 0;
        while (r.Next(out var f, out var w))
        {
            switch (f)
            {
                case 1 when w == 2:
                {
                    var st = r.ReadMessage();
                    while (st.Next(out var sf, out var sw))
                    {
                        if (sf == 1 && sw == 2) strings.Add(st.ReadString());
                        else st.Skip(sw);
                    }
                    break;
                }
                case 2 when w == 2: groups.Add(r.ReadBytesRange()); break;
                case 17 when w == 0: granularity = r.ReadInt32(); break;
                case 19 when w == 0: latOffset = r.ReadInt64(); break;
                case 20 when w == 0: lonOffset = r.ReadInt64(); break;
                default: r.Skip(w); break;
            }
        }
        var block = new Block(strings, granularity, latOffset, lonOffset);
        foreach (var (offset, length) in groups)
        {
            var g = new ProtoReader(data, offset, length);
            while (g.Next(out var f, out var w))
            {
                switch (f)
                {
                    case 1 when w == 2 && handler.WantNodes: PlainNode(g.ReadMessage(), block, handler); break;
                    case 2 when w == 2 && handler.WantNodes: DenseNodes(g.ReadMessage(), block, handler); break;
                    case 3 when w == 2 && handler.WantWays: Way(g.ReadMessage(), block, handler); break;
                    case 4 when w == 2 && handler.WantRelations: Relation(g.ReadMessage(), block, handler); break;
                    default: g.Skip(w); break;
                }
            }
        }
    }

    private sealed record Block(List<string> Strings, long Granularity, long LatOffset, long LonOffset)
    {
        public double Lat(long v) => 1e-9 * (LatOffset + Granularity * v);
        public double Lon(long v) => 1e-9 * (LonOffset + Granularity * v);
    }

    private static List<uint> Packed(ref ProtoReader r)
    {
        var list = new List<uint>();
        var sub = r.ReadMessage();
        while (!sub.AtEnd)
            list.Add(sub.ReadUInt32());
        return list;
    }

    private static OsmTags Tags(List<uint> keys, List<uint> vals, Block b)
    {
        if (keys.Count == 0)
            return OsmTags.Empty;
        var tags = new KeyValuePair<string, string>[Math.Min(keys.Count, vals.Count)];
        for (var i = 0; i < tags.Length; i++)
            tags[i] = new(b.Strings[(int)keys[i]], b.Strings[(int)vals[i]]);
        return new OsmTags(tags);
    }

    private static void PlainNode(ProtoReader r, Block b, IOsmHandler h)
    {
        long id = 0, lat = 0, lon = 0;
        List<uint> keys = new(), vals = new();
        while (r.Next(out var f, out var w))
        {
            switch (f)
            {
                case 1: id = r.ReadSInt64(); break;
                case 2: keys = Packed(ref r); break;
                case 3: vals = Packed(ref r); break;
                case 8: lat = r.ReadSInt64(); break;
                case 9: lon = r.ReadSInt64(); break;
                default: r.Skip(w); break;
            }
        }
        h.Node(id, b.Lat(lat), b.Lon(lon), Tags(keys, vals, b));
    }

    private static void DenseNodes(ProtoReader r, Block b, IOsmHandler h)
    {
        var ids = new List<long>();
        var lats = new List<long>();
        var lons = new List<long>();
        var keyVals = new List<int>();
        while (r.Next(out var f, out var w))
        {
            switch (f)
            {
                case 1 when w == 2: ReadDeltas(ref r, ids); break;
                case 8 when w == 2: ReadDeltas(ref r, lats); break;
                case 9 when w == 2: ReadDeltas(ref r, lons); break;
                case 10 when w == 2:
                {
                    var sub = r.ReadMessage();
                    while (!sub.AtEnd)
                        keyVals.Add(sub.ReadInt32());
                    break;
                }
                default: r.Skip(w); break;
            }
        }
        var kv = 0;
        var tagBuffer = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < ids.Count; i++)
        {
            var tags = OsmTags.Empty;
            if (keyVals.Count > 0)
            {
                tagBuffer.Clear();
                while (kv < keyVals.Count && keyVals[kv] != 0)
                {
                    tagBuffer.Add(new(b.Strings[keyVals[kv]], b.Strings[keyVals[kv + 1]]));
                    kv += 2;
                }
                kv++; // the 0 delimiter
                if (tagBuffer.Count > 0)
                    tags = new OsmTags(tagBuffer.ToArray());
            }
            h.Node(ids[i], b.Lat(lats[i]), b.Lon(lons[i]), tags);
        }
    }

    private static void ReadDeltas(ref ProtoReader r, List<long> into)
    {
        var sub = r.ReadMessage();
        long value = 0;
        while (!sub.AtEnd)
        {
            value += sub.ReadSInt64();
            into.Add(value);
        }
    }

    private static void Way(ProtoReader r, Block b, IOsmHandler h)
    {
        long id = 0;
        List<uint> keys = new(), vals = new();
        var refs = new List<long>();
        while (r.Next(out var f, out var w))
        {
            switch (f)
            {
                case 1: id = r.ReadInt64(); break;
                case 2: keys = Packed(ref r); break;
                case 3: vals = Packed(ref r); break;
                case 8 when w == 2: ReadDeltas(ref r, refs); break;
                default: r.Skip(w); break;
            }
        }
        h.Way(id, refs.Count == 0 ? NoRefs : refs, Tags(keys, vals, b));
    }

    private static void Relation(ProtoReader r, Block b, IOsmHandler h)
    {
        long id = 0;
        List<uint> keys = new(), vals = new();
        var roles = new List<int>();
        var memIds = new List<long>();
        var types = new List<int>();
        while (r.Next(out var f, out var w))
        {
            switch (f)
            {
                case 1: id = r.ReadInt64(); break;
                case 2: keys = Packed(ref r); break;
                case 3: vals = Packed(ref r); break;
                case 8 when w == 2:
                {
                    var sub = r.ReadMessage();
                    while (!sub.AtEnd)
                        roles.Add(sub.ReadInt32());
                    break;
                }
                case 9 when w == 2: ReadDeltas(ref r, memIds); break;
                case 10 when w == 2:
                {
                    var sub = r.ReadMessage();
                    while (!sub.AtEnd)
                        types.Add(sub.ReadInt32());
                    break;
                }
                default: r.Skip(w); break;
            }
        }
        var members = new OsmMember[memIds.Count];
        for (var i = 0; i < members.Length; i++)
            members[i] = new OsmMember((OsmMemberType)(i < types.Count ? types[i] : 0), memIds[i], i < roles.Count ? b.Strings[roles[i]] : "");
        h.Relation(id, members, Tags(keys, vals, b));
    }
}
