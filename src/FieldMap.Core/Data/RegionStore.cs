using System.Globalization;
using FieldMap.Core.Geo;
using FieldMap.Core.Routing;
using FieldMap.Core.Search;
using FieldMap.Core.Tiles;

namespace FieldMap.Core.Data;

public sealed class ValidationReport
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Checks { get; } = new();

    /// <summary>Random entries with their source IDs, for spot-checking against the source data.</summary>
    public List<string> SpotChecks { get; } = new();

    public bool Ok => Errors.Count == 0;

    public override string ToString() =>
        string.Join(Environment.NewLine, Errors.Select(e => "ERROR: " + e).Concat(Warnings.Select(w => "Warning: " + w)).Concat(Checks.Select(c => "OK: " + c)));
}

/// <summary>Checks a built region version before it is published.</summary>
public static class RegionValidator
{
    public static ValidationReport Validate(string dir, RegionManifest? previous = null, bool checkHashes = true)
    {
        var report = new ValidationReport();
        RegionManifest manifest;
        try
        {
            manifest = RegionManifest.Load(dir);
        }
        catch (Exception ex)
        {
            report.Errors.Add("manifest.json is missing or unreadable: " + ex.Message);
            return report;
        }
        var fileProblems = manifest.VerifyFiles(dir, checkHashes);
        report.Errors.AddRange(fileProblems);
        if (fileProblems.Count == 0)
            report.Checks.Add($"{manifest.Files.Count} files present{(checkHashes ? " with matching SHA-256 checksums" : "")}");
        if (manifest.TileSchema != Build.TileSchema.Version || manifest.GraphFormat != RoadGraph.FormatVersion || manifest.SearchSchema != SearchIndexBuilder.SchemaVersion)
            report.Errors.Add("The data was built by a different version of Field Map (format mismatch). Rebuild or update the app.");

        // Tiles
        try
        {
            using var tiles = new MbTilesReader(Path.Combine(dir, RegionBuilder.TilesFile));
            var count = tiles.CountTiles();
            var center = new GeoPoint((manifest.MinLat + manifest.MaxLat) / 2, (manifest.MinLon + manifest.MaxLon) / 2);
            var (tx, ty) = WebMercator.Tile(center.Lat, center.Lon, 10);
            if (count == 0)
                report.Errors.Add("The tile file has no tiles.");
            else if (tiles.GetRaw(10, tx, ty) == null)
                report.Errors.Add("There is no map tile at the centre of the region.");
            else
                report.Checks.Add($"{count:N0} map tiles readable");
        }
        catch (Exception ex)
        {
            report.Errors.Add("Map tiles can't be read: " + ex.Message);
        }

        // Routing
        RoadGraph? graph = null;
        try
        {
            graph = RoadGraph.Load(Path.Combine(dir, RegionBuilder.GraphFile));
            if (graph.SegmentCount == 0)
                report.Errors.Add("The routing graph has no roads.");
            else if (TestRoute(graph) is { } msg)
                report.Checks.Add(msg);
            else
                report.Errors.Add("A test route could not be calculated.");
        }
        catch (Exception ex)
        {
            report.Errors.Add("Routing graph can't be read: " + ex.Message);
        }

        // Search
        try
        {
            using var search = new SearchEngine(Path.Combine(dir, RegionBuilder.SearchFile), graph);
            var places = search.LoadPlaces();
            var pois = search.LoadPois();
            var probe = places.OrderBy(p => p.Rank).ThenByDescending(p => p.Population).FirstOrDefault();
            if (probe != null)
            {
                var hit = search.Search(probe.Name, null, 5).Any(r => r.Name == probe.Name);
                if (hit)
                    report.Checks.Add($"Search finds \"{probe.Name}\"");
                else
                    report.Errors.Add($"Search does not find the place \"{probe.Name}\".");
            }
            report.Checks.Add($"{places.Count:N0} places and {pois.Count:N0} facilities indexed");
            var rnd = new Random(manifest.BuiltUtc.GetHashCode());
            foreach (var p in pois.Where(p => p.Name.Length > 0).OrderBy(_ => rnd.Next()).Take(20))
                report.SpotChecks.Add($"{p.KindLabel}: {p.Name} at {CoordinateFormat.Decimal(p.Lat, p.Lon)} – {p.Source}{(p.SourceId != null ? " " + p.SourceId : "")}");
        }
        catch (Exception ex)
        {
            report.Errors.Add("Search index can't be read: " + ex.Message);
        }

        // Compare with the version in use: a big drop means a broken or truncated download.
        if (previous != null && !previous.IsSample && !manifest.IsSample)
            foreach (var key in new[] { "routingSegments", "pois", "places" })
                if (previous.Stats.TryGetValue(key, out var before) && before > 100 && manifest.Stats.TryGetValue(key, out var now) && now < before * 0.7)
                    report.Errors.Add($"{key}: {now:N0} now vs {before:N0} in the current version (more than 30 % lost).");
        return report;
    }

    /// <summary>Routes between two well-connected nodes; returns a description, or null on failure.</summary>
    private static string? TestRoute(RoadGraph g)
    {
        var router = new Router(g);
        var nodes = Enumerable.Range(0, g.NodeCount).Where(n => g.AdjStart[n + 1] - g.AdjStart[n] >= 3).Take(2000).ToList();
        if (nodes.Count < 2)
            nodes = Enumerable.Range(0, Math.Min(g.NodeCount, 50)).ToList();
        var rnd = new Random(42);
        for (var attempt = 0; attempt < 10 && nodes.Count >= 2; attempt++)
        {
            var a = g.Node(nodes[rnd.Next(nodes.Count)]);
            var b = g.Node(nodes[rnd.Next(nodes.Count)]);
            if (a.DistanceTo(b) < 50)
                continue;
            var route = router.Route(a, b, VehicleProfile.Car);
            if (route != null && route.DistanceMeters > 0)
                return $"Test route: {Instructions.FormatDistance(route.DistanceMeters)} in {route.Steps.Count} steps";
        }
        return null;
    }
}

/// <summary>
/// The user-chosen data folder: per-region versioned folders, an atomically replaced current.json pointer, the previous
/// version kept for rollback, staging for builds in progress and a download cache.
/// </summary>
public sealed class RegionStore
{
    public const string CurrentFile = "current.json";
    public const string PreviousFile = "previous.json";

    private sealed class Pointer
    {
        public string Version { get; set; } = "";
        public DateTime SetUtc { get; set; }
    }

    public RegionStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string RegionsDir => Path.Combine(Root, "regions");
    public string DownloadsDir => Path.Combine(Root, "downloads");
    public string StagingRoot => Path.Combine(Root, "staging");
    public string ImportsDir => Path.Combine(Root, "imports");
    public string CacheDir => Path.Combine(Root, "cache");

    public string RegionDir(string id) => Path.Combine(RegionsDir, id);
    public string VersionsDir(string id) => Path.Combine(RegionDir(id), "versions");
    public string VersionDir(string id, string version) => Path.Combine(VersionsDir(id), version);

    public IEnumerable<string> InstalledRegions() =>
        Directory.Exists(RegionsDir)
            ? Directory.GetDirectories(RegionsDir).Select(Path.GetFileName).OfType<string>().Where(id => ResolveCurrent(id).Version != null).OrderBy(x => x)
            : Enumerable.Empty<string>();

    private string? ReadPointer(string id, string file)
    {
        try
        {
            var path = Path.Combine(RegionDir(id), file);
            return File.Exists(path) ? Json.Deserialize<Pointer>(File.ReadAllText(path)).Version : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void WritePointer(string id, string file, string version) =>
        Json.WriteFileAtomic(Path.Combine(RegionDir(id), file), Json.Serialize(new Pointer { Version = version, SetUtc = DateTime.UtcNow }));

    public string? CurrentVersion(string id) => ReadPointer(id, CurrentFile);
    public string? PreviousVersion(string id) => ReadPointer(id, PreviousFile);

    /// <summary>A version folder is usable when its manifest loads and all listed files exist with the right size.</summary>
    public bool IsValid(string id, string? version)
    {
        if (string.IsNullOrEmpty(version))
            return false;
        var dir = VersionDir(id, version);
        try
        {
            return Directory.Exists(dir) && RegionManifest.Load(dir).VerifyFiles(dir, checkHashes: false).Count == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The version to use: current.json if valid, else previous.json, else the newest valid version folder. The message
    /// explains any fallback so the user knows the map isn't the latest.
    /// </summary>
    public (string? Version, string? Message) ResolveCurrent(string id)
    {
        var current = CurrentVersion(id);
        if (IsValid(id, current))
            return (current, null);
        var previous = PreviousVersion(id);
        if (IsValid(id, previous))
            return (previous, $"The current map data ({current ?? "none"}) is damaged or missing; using the previous version {previous}.");
        if (Directory.Exists(VersionsDir(id)))
            foreach (var v in Directory.GetDirectories(VersionsDir(id)).Select(Path.GetFileName).OfType<string>().OrderByDescending(x => x, StringComparer.Ordinal))
                if (IsValid(id, v))
                    return (v, current == null ? null : $"The current and previous map data are damaged; using version {v}.");
        return (null, current == null ? null : "No usable map data is installed for this region.");
    }

    public RegionManifest? CurrentManifest(string id) =>
        ResolveCurrent(id).Version is { } v ? RegionManifest.Load(VersionDir(id, v)) : null;

    public string? CurrentDir(string id) => ResolveCurrent(id).Version is { } v ? VersionDir(id, v) : null;

    /// <summary>A new empty staging folder for a build or package install (same volume as the versions, so publish is a rename).</summary>
    public string NewStagingDir(string id)
    {
        var dir = Path.Combine(StagingRoot, $"{id}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Deletes staging folders left behind by interrupted builds.</summary>
    public void CleanStaging()
    {
        if (!Directory.Exists(StagingRoot))
            return;
        foreach (var d in Directory.GetDirectories(StagingRoot))
            TryDelete(d);
    }

    public string NewVersionName(string id, DateTime builtUtc)
    {
        var date = builtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        for (var n = 1; ; n++)
        {
            var name = $"{date}_{n}";
            if (!Directory.Exists(VersionDir(id, name)))
                return name;
        }
    }

    /// <summary>
    /// Publishes a validated staging folder: moves it into versions\ (a rename), then atomically replaces current.json; the
    /// old current becomes previous.json. Versions other than current and previous are deleted. A failure before the final
    /// pointer write leaves the old version current.
    /// </summary>
    public string Publish(string id, string stagingDir, Action? beforePointerSwap = null)
    {
        var manifest = RegionManifest.Load(stagingDir);
        var version = NewVersionName(id, manifest.BuiltUtc);
        manifest.Version = version;
        manifest.Save(stagingDir);
        Directory.CreateDirectory(VersionsDir(id));
        var target = VersionDir(id, version);
        Directory.Move(stagingDir, target);
        beforePointerSwap?.Invoke();
        var old = ResolveCurrent(id).Version;
        if (old != null && old != version)
            WritePointer(id, PreviousFile, old);
        WritePointer(id, CurrentFile, version);
        Prune(id);
        return version;
    }

    /// <summary>Swaps current and previous. Returns the version now in use, or null when there is nothing to roll back to.</summary>
    public string? Rollback(string id)
    {
        var current = ResolveCurrent(id).Version;
        var previous = PreviousVersion(id);
        if (!IsValid(id, previous) || previous == current)
            return null;
        if (current != null)
            WritePointer(id, PreviousFile, current);
        WritePointer(id, CurrentFile, previous!);
        return previous;
    }

    /// <summary>Removes versions that are neither current nor previous (files still open are retried next time).</summary>
    public void Prune(string id)
    {
        if (!Directory.Exists(VersionsDir(id)))
            return;
        var keep = new HashSet<string?> { CurrentVersion(id), PreviousVersion(id) };
        foreach (var d in Directory.GetDirectories(VersionsDir(id)))
            if (!keep.Contains(Path.GetFileName(d)))
                TryDelete(d);
    }

    public void RemoveRegion(string id) => TryDelete(RegionDir(id));

    public long SizeOnDisk(string id) =>
        Directory.Exists(RegionDir(id)) ? Directory.GetFiles(RegionDir(id), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
