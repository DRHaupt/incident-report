namespace FieldMap.Core.Data;

public static class SourceKinds
{
    /// <summary>OpenStreetMap extract (.osm.pbf, or .osm XML for the sample).</summary>
    public const string OsmPbf = "osm-pbf";
    /// <summary>Address points CSV (StatCan Open Database of Addresses layout), optionally zipped.</summary>
    public const string AddressCsv = "address-csv";
    /// <summary>Facility list CSV/GeoJSON merged into a POI layer using a column mapping.</summary>
    public const string PoiTable = "poi-table";
}

/// <summary>How columns of an official facility table map onto a POI layer.</summary>
public sealed class PoiMapping
{
    public string Layer { get; set; } = "";
    public string Kind { get; set; } = "";
    public string NameColumn { get; set; } = "name";
    public string LatColumn { get; set; } = "latitude";
    public string LonColumn { get; set; } = "longitude";
    public string? AddressColumn { get; set; }
    public string? IdColumn { get; set; }
    public string? VerifiedColumn { get; set; }

    /// <summary>Keep only rows whose <see cref="FilterColumn"/> is one of <see cref="FilterValues"/> (case-insensitive).</summary>
    public string? FilterColumn { get; set; }
    public List<string> FilterValues { get; set; } = new();

    /// <summary>Optional: kind from a column via a value map (e.g. "Emergency" → "ed").</summary>
    public string? KindColumn { get; set; }
    public Dictionary<string, string> KindMap { get; set; } = new();

    /// <summary>Merge with an existing POI of the same layer within this many metres instead of adding a new one.</summary>
    public double MergeRadiusMeters { get; set; } = 150;
}

public sealed class SourceDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = SourceKinds.OsmPbf;
    public string Url { get; set; } = "";
    public string? ChecksumUrl { get; set; }
    /// <summary>"md5" (Geofabrik .md5 files) or "sha256".</summary>
    public string? ChecksumType { get; set; }
    public string Licence { get; set; } = "";
    public string LicenceUrl { get; set; } = "";
    public string Attribution { get; set; } = "";
    public bool Required { get; set; }
    public bool Enabled { get; set; } = true;
    public long? ApproxBytes { get; set; }

    /// <summary>When the URL and terms were last checked, or "unverified".</summary>
    public string Verified { get; set; } = "unverified";

    /// <summary>For zip downloads: the file to use inside the archive (wildcards allowed, e.g. "*.csv").</summary>
    public string? ZipEntry { get; set; }

    public PoiMapping? Mapping { get; set; }

    public string FileName => Path.GetFileName(new Uri(Url).AbsolutePath) is { Length: > 0 } f ? f : Id;
}

public sealed class RegionDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<SourceDefinition> Sources { get; set; } = new();

    public long ApproxDownloadBytes => Sources.Where(s => s.Enabled).Sum(s => s.ApproxBytes ?? 0);
}

/// <summary>The installable regions and their sources (Content/regions.json, editable by the organisation).</summary>
public sealed class RegionCatalog
{
    public string Notes { get; set; } = "";
    public List<RegionDefinition> Regions { get; set; } = new();

    public RegionDefinition? Find(string id) => Regions.FirstOrDefault(r => r.Id == id);

    public static RegionCatalog Load(string path) => Json.Deserialize<RegionCatalog>(File.ReadAllText(path));

    public static RegionCatalog LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path))
                return Load(path);
        }
        catch (Exception)
        {
            // Fall through to the built-in list.
        }
        return new RegionCatalog
        {
            Regions =
            {
                new RegionDefinition
                {
                    Id = "bc", Name = "British Columbia",
                    Sources = { Osm("british-columbia", 1_200_000_000) }
                },
                new RegionDefinition
                {
                    Id = "ab", Name = "Alberta",
                    Sources = { Osm("alberta", 330_000_000) }
                }
            }
        };
    }

    private static SourceDefinition Osm(string slug, long bytes) => new()
    {
        Id = "osm",
        Name = "OpenStreetMap (Geofabrik extract)",
        Kind = SourceKinds.OsmPbf,
        Url = $"https://download.geofabrik.de/north-america/canada/{slug}-latest.osm.pbf",
        ChecksumUrl = $"https://download.geofabrik.de/north-america/canada/{slug}-latest.osm.pbf.md5",
        ChecksumType = "md5",
        Licence = "ODbL 1.0",
        LicenceUrl = "https://opendatacommons.org/licenses/odbl/1-0/",
        Attribution = "© OpenStreetMap contributors",
        Required = true,
        ApproxBytes = bytes,
        Verified = "2026-10"
    };
}
