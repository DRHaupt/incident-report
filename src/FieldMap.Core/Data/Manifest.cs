using System.Security.Cryptography;

namespace FieldMap.Core.Data;

/// <summary>Where a dataset came from, under which licence, and when it was downloaded.</summary>
public sealed class DataSourceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Licence { get; set; } = "";
    public string LicenceUrl { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>Attribution text required by the licence (shown on the map and on exports).</summary>
    public string Attribution { get; set; } = "";

    public DateTime? DownloadedUtc { get; set; }

    /// <summary>The date of the data itself when the publisher states it (e.g. extract timestamp), else the download date.</summary>
    public DateTime? DataDateUtc { get; set; }

    public string? Sha256 { get; set; }
    public long? SizeBytes { get; set; }
    public bool IsSample { get; set; }
}

/// <summary>Per-layer counts and which sources contributed (shown in the Layers panel and About → Data).</summary>
public sealed class LayerInfo
{
    public int Count { get; set; }
    public List<string> Sources { get; set; } = new();
}

public sealed class ManifestFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>manifest.json in every installed region version.</summary>
public sealed class RegionManifest
{
    public const string FileName = "manifest.json";
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public string RegionId { get; set; } = "";
    public string RegionName { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime BuiltUtc { get; set; }
    public string BuiltBy { get; set; } = "";
    public bool IsSample { get; set; }
    public double MinLat { get; set; }
    public double MinLon { get; set; }
    public double MaxLat { get; set; }
    public double MaxLon { get; set; }
    public List<DataSourceInfo> Sources { get; set; } = new();
    public Dictionary<string, LayerInfo> Layers { get; set; } = new();
    public Dictionary<string, long> Stats { get; set; } = new();
    public List<ManifestFile> Files { get; set; } = new();
    public int TileSchema { get; set; }
    public int GraphFormat { get; set; }
    public int SearchSchema { get; set; }

    /// <summary>Oldest data date among the sources (what the status bar shows as "data date").</summary>
    public DateTime? DataDate => Sources.Select(s => s.DataDateUtc ?? s.DownloadedUtc).Where(d => d != null).DefaultIfEmpty(null).Min();

    public static RegionManifest Load(string dir) =>
        Json.Deserialize<RegionManifest>(File.ReadAllText(Path.Combine(dir, FileName)));

    public void Save(string dir) => Json.WriteFileAtomic(Path.Combine(dir, FileName), Json.Serialize(this));

    public static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>Records size and SHA-256 of every data file in the folder (except the manifest itself).</summary>
    public void HashFiles(string dir)
    {
        Files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals(FileName, StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new ManifestFile
            {
                Path = System.IO.Path.GetRelativePath(dir, f).Replace('\\', '/'),
                Size = new FileInfo(f).Length,
                Sha256 = Sha256Of(f)
            }).ToList();
    }

    /// <summary>Problems found when checking the folder against the recorded files (empty = intact).</summary>
    public List<string> VerifyFiles(string dir, bool checkHashes = true)
    {
        var problems = new List<string>();
        foreach (var f in Files)
        {
            var path = System.IO.Path.Combine(dir, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                problems.Add($"{f.Path} is missing");
            else if (new FileInfo(path).Length != f.Size)
                problems.Add($"{f.Path} has the wrong size");
            else if (checkHashes && Sha256Of(path) != f.Sha256)
                problems.Add($"{f.Path} failed its checksum");
        }
        return problems;
    }
}
