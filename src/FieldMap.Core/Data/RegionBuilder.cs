using System.Globalization;
using FieldMap.Core.Build;
using FieldMap.Core.Layers;
using FieldMap.Core.Routing;
using FieldMap.Core.Search;
using FieldMap.Core.Tiles;

namespace FieldMap.Core.Data;

/// <summary>A local file for one of a region's sources, with its provenance.</summary>
public sealed record SourceFile(SourceDefinition Definition, string Path, DataSourceInfo Info);

public sealed class BuildRequest
{
    public required string RegionId { get; init; }
    public required string RegionName { get; init; }
    public required string OutputDir { get; init; }
    public required List<SourceFile> Sources { get; init; }

    /// <summary>Organisation AED registries and similar imports re-applied on every build: (file, source name).</summary>
    public List<(string Path, string SourceName)> AedImports { get; init; } = new();

    public bool IsSample { get; init; }
    public string BuiltBy { get; init; } = "Field Map";
}

/// <summary>Builds a complete region version (tiles, routing graph, search index, manifest) from local source files.</summary>
public static class RegionBuilder
{
    public const string TilesFile = "tiles.mbtiles";
    public const string GraphFile = "routing.graph";
    public const string SearchFile = "search.sqlite";

    public static RegionManifest Build(BuildRequest req, IProgress<(string Stage, double Fraction)>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(req.OutputDir);
        var osmFiles = req.Sources.Where(s => s.Definition.Kind == SourceKinds.OsmPbf).ToList();
        if (osmFiles.Count == 0)
            throw new InvalidOperationException("An OpenStreetMap extract is required.");
        var osm = osmFiles[0];

        void Report(string stage, double f) => progress?.Report((stage, f));

        // 1. OpenStreetMap
        var data = OsmExtractor.Extract(osmFiles.Select(f => f.Path).ToList(), new Immediate<(string Stage, double Fraction)>(p => Report(p.Stage, p.Fraction)), cancel);
        var layerSources = new Dictionary<string, HashSet<string>>();
        foreach (var p in data.Pois)
            foreach (var f in osmFiles)
                Source(layerSources, p.Layer, f.Info.Name);

        // 2. Address points
        foreach (var s in req.Sources.Where(s => s.Definition.Kind == SourceKinds.AddressCsv))
        {
            Report($"Importing addresses ({s.Definition.Name})", 0);
            AddressImport.Import(s.Path, data, s.Info.Name, s.Definition.ZipEntry, data.Bounds.IsEmpty ? null : data.Bounds.Expand(5000), cancel);
        }

        // 3. Official facility tables and AED registries
        foreach (var s in req.Sources.Where(s => s.Definition.Kind == SourceKinds.PoiTable && s.Definition.Mapping != null))
        {
            Report($"Merging {s.Definition.Name}", 0);
            var rows = PoiImport.Read(s.Path, s.Definition.Mapping!, s.Info.Name, s.Definition.ZipEntry)
                .Where(p => data.Bounds.IsEmpty || data.Bounds.Expand(5000).Contains(p.Lat, p.Lon)).ToList();
            PoiImport.Merge(data.Pois, rows, s.Definition.Mapping!.MergeRadiusMeters);
            Source(layerSources, s.Definition.Mapping!.Layer, s.Info.Name);
        }
        foreach (var (path, name) in req.AedImports)
        {
            Report($"Merging AEDs from {Path.GetFileName(path)}", 0);
            // Registries list a few sites outside the province; keep the ones on this region's map.
            var aeds = PoiImport.ReadAeds(path, name).Where(a => data.Bounds.IsEmpty || data.Bounds.Expand(5000).Contains(a.Lat, a.Lon)).ToList();
            PoiImport.Merge(data.Pois, aeds, PoiImport.AedMapping.MergeRadiusMeters);
            foreach (var src in aeds.Select(a => a.Source).Distinct())
                Source(layerSources, LayerCatalog.Aed, src);
        }

        // 4. Routing graph (before tiles: the tile builder releases road geometry)
        Report("Building routing graph", 0);
        var graph = GraphBuilder.Build(data, cancel);
        graph.Save(Path.Combine(req.OutputDir, GraphFile));
        Report("Building routing graph", 1);

        var built = DateTime.UtcNow;
        var meta = new Dictionary<string, string>
        {
            ["region"] = req.RegionId,
            ["name"] = req.RegionName,
            ["built"] = built.ToString("o", CultureInfo.InvariantCulture),
            ["is_sample"] = req.IsSample ? "1" : "0",
            ["attribution"] = string.Join(" · ", req.Sources.Select(s => s.Info.Attribution).Where(a => a.Length > 0).Distinct())
        };

        // 5. Search index
        Report("Building search index", 0);
        SearchIndexBuilder.Build(data, graph, Path.Combine(req.OutputDir, SearchFile), meta, cancel,
            new Immediate<double>(f => Report("Building search index", f)));
        Report("Building search index", 1);

        var stats = new Dictionary<string, long>
        {
            ["roads"] = data.Roads.Count,
            ["routingNodes"] = graph.NodeCount,
            ["routingSegments"] = graph.SegmentCount,
            ["turnRestrictions"] = graph.TurnRestrictionCount,
            ["turnRestrictionsNotUsed"] = graph.UnresolvedRestrictions,
            ["pois"] = data.Pois.Count,
            ["addresses"] = data.Addresses.Count,
            ["places"] = data.Places.Count,
            ["waterAreas"] = data.Water.Count
        };
        var bounds = data.Bounds;
        var layerCounts = data.Pois.GroupBy(p => p.Layer).ToDictionary(g => g.Key, g => g.Count());

        // 6. Vector tiles
        var tiles = TileBuilder.BuildMbTiles(data, Path.Combine(req.OutputDir, TilesFile), meta,
            new Immediate<double>(f => Report("Building map tiles", f)), cancel);
        stats["tiles"] = tiles;

        // 7. Manifest
        var manifest = new RegionManifest
        {
            RegionId = req.RegionId,
            RegionName = req.RegionName,
            BuiltUtc = built,
            BuiltBy = req.BuiltBy,
            IsSample = req.IsSample,
            MinLat = bounds.MinLat,
            MinLon = bounds.MinLon,
            MaxLat = bounds.MaxLat,
            MaxLon = bounds.MaxLon,
            Sources = req.Sources.Select(s => s.Info).ToList(),
            Stats = stats,
            TileSchema = TileSchema.Version,
            GraphFormat = RoadGraph.FormatVersion,
            SearchSchema = SearchIndexBuilder.SchemaVersion
        };
        foreach (var layer in LayerCatalog.All)
            manifest.Layers[layer.Id] = new LayerInfo
            {
                Count = layerCounts.GetValueOrDefault(layer.Id),
                Sources = layerSources.TryGetValue(layer.Id, out var srcs) ? srcs.OrderBy(x => x).ToList() : osmFiles.Select(f => f.Info.Name).ToList()
            };
        manifest.Layers["basemap"] = new LayerInfo { Count = (int)tiles, Sources = osmFiles.Select(f => f.Info.Name).ToList() };
        manifest.Layers["addresses"] = new LayerInfo
        {
            Count = data.Addresses.Count,
            Sources = data.Addresses.Select(a => a.Source).Distinct().OrderBy(x => x).ToList()
        };
        Report("Writing checksums", 0);
        manifest.HashFiles(req.OutputDir);
        manifest.Save(req.OutputDir);
        Report("Done", 1);
        return manifest;
    }

    private static void Source(Dictionary<string, HashSet<string>> map, string layer, string source)
    {
        if (!map.TryGetValue(layer, out var set))
            map[layer] = set = new HashSet<string>();
        set.Add(source);
    }

    /// <summary>Builds the fictitious sample region into <paramref name="outputDir"/>.</summary>
    public static RegionManifest BuildSample(string outputDir, string workDir, IProgress<(string Stage, double Fraction)>? progress = null, CancellationToken cancel = default,
        List<(string Path, string SourceName)>? aedImports = null)
    {
        Directory.CreateDirectory(workDir);
        var osmPath = Path.Combine(workDir, "sample-county.osm");
        Sample.SampleRegion.Write(osmPath);
        var info = new DataSourceInfo
        {
            Id = "sample",
            Name = "SAMPLE DATA (fictitious)",
            Licence = "Fictitious test data – not for operational use",
            Attribution = "SAMPLE DATA – fictitious",
            DownloadedUtc = DateTime.UtcNow,
            DataDateUtc = DateTime.UtcNow,
            IsSample = true
        };
        var def = new SourceDefinition { Id = "sample", Name = info.Name, Kind = SourceKinds.OsmPbf, Url = "file:///sample-county.osm", Required = true };
        return Build(new BuildRequest
        {
            RegionId = Sample.SampleRegion.RegionId,
            RegionName = Sample.SampleRegion.Name,
            OutputDir = outputDir,
            Sources = new List<SourceFile> { new(def, osmPath, info) },
            AedImports = aedImports ?? new(),
            IsSample = true
        }, progress, cancel);
    }
}

/// <summary>
/// Passes progress straight through on the reporting thread. (Progress&lt;T&gt; would queue each report to the thread
/// pool, so a late "search index 70 %" could land after the tile stage had started.)
/// </summary>
internal sealed class Immediate<T> : IProgress<T>
{
    private readonly Action<T> _report;
    public Immediate(Action<T> report) => _report = report;
    public void Report(T value) => _report(value);
}
