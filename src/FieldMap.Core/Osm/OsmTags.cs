namespace FieldMap.Core.Osm;

/// <summary>The key=value tags of an OpenStreetMap element.</summary>
public sealed class OsmTags
{
    public static readonly OsmTags Empty = new(Array.Empty<KeyValuePair<string, string>>());

    private readonly KeyValuePair<string, string>[] _tags;

    public OsmTags(KeyValuePair<string, string>[] tags) => _tags = tags;

    public OsmTags(IDictionary<string, string> tags) => _tags = tags.ToArray();

    public int Count => _tags.Length;

    public IReadOnlyList<KeyValuePair<string, string>> All => _tags;

    public string? this[string key]
    {
        get
        {
            foreach (var t in _tags)
                if (t.Key == key)
                    return t.Value;
            return null;
        }
    }

    public bool Has(string key) => this[key] != null;

    public bool Is(string key, string value) => this[key] == value;

    public bool IsAny(string key, params string[] values) => this[key] is { } v && values.Contains(v);

    /// <summary>"yes", "true" and "1" count as yes.</summary>
    public bool IsYes(string key) => this[key] is "yes" or "true" or "1";

    public Dictionary<string, string> ToDictionary() => _tags.ToDictionary(t => t.Key, t => t.Value);

    public override string ToString() => string.Join(";", _tags.Select(t => $"{t.Key}={t.Value}"));
}

public enum OsmMemberType : byte
{
    Node,
    Way,
    Relation
}

public readonly record struct OsmMember(OsmMemberType Type, long Id, string Role);

/// <summary>
/// Receives OpenStreetMap elements from a reader. Files list nodes first, then ways, then relations, so a handler can be
/// run over the file several times (e.g. relations, then ways, then nodes) to collect only what it needs.
/// </summary>
public interface IOsmHandler
{
    bool WantNodes { get; }
    bool WantWays { get; }
    bool WantRelations { get; }

    void Node(long id, double lat, double lon, OsmTags tags);
    void Way(long id, IReadOnlyList<long> nodeRefs, OsmTags tags);
    void Relation(long id, IReadOnlyList<OsmMember> members, OsmTags tags);
}

/// <summary>Reads .osm.pbf or .osm (XML) files, chosen by extension.</summary>
public static class OsmFile
{
    public static void Read(string path, IOsmHandler handler, CancellationToken cancel = default, IProgress<double>? progress = null)
    {
        if (path.EndsWith(".pbf", StringComparison.OrdinalIgnoreCase))
            new OsmPbfReader().Read(path, handler, cancel, progress);
        else
            OsmXmlReader.Read(path, handler, cancel, progress);
    }
}
