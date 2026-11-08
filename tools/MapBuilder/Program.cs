using FieldMap.Core.Imagery;
using FieldMap.Core.Data;
using FieldMap.Core.Sample;

namespace MapBuilder;

/// <summary>
/// MapBuilder – the same pipeline as Field Map's Data screen, for a connected PC or a script.
///
///   MapBuilder sample-osm &lt;file.osm&gt;                       write the fictitious Sample County as OSM XML
///   MapBuilder build-sample --data &lt;folder&gt;                build and install the sample region
///   MapBuilder build --region bc --data &lt;folder&gt; [--catalog regions.json] [--pbf file] [--addresses file]... [--aed file]...
///   MapBuilder update --region bc --data &lt;folder&gt; [--catalog regions.json]   download the latest sources, build, validate, install
///   MapBuilder validate --region bc --data &lt;folder&gt;
///   MapBuilder rollback --region bc --data &lt;folder&gt;
///   MapBuilder export --region bc --data &lt;folder&gt; --out &lt;folder or .fmupdate&gt;
///   MapBuilder install --package &lt;file.fmupdate&gt; --data &lt;folder&gt;
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        try
        {
            var opts = Options.Parse(args.Skip(1).ToArray());
            var progress = new Progress<(string Stage, double Fraction)>(p => Console.Error.Write($"\r{p.Stage,-48} {p.Fraction,6:P0}   "));
            switch (args[0])
            {
                case "sample-osm":
                    SampleRegion.Write(args[1]);
                    Console.WriteLine($"Wrote {args[1]} (fictitious sample data).");
                    return 0;
                case "build-sample":
                {
                    var store = new RegionStore(opts.Require("data"));
                    var staging = store.NewStagingDir(SampleRegion.RegionId);
                    RegionBuilder.BuildSample(staging, Path.Combine(store.Root, "work"), progress);
                    return Publish(store, SampleRegion.RegionId, staging);
                }
                case "build":
                {
                    var store = new RegionStore(opts.Require("data"));
                    var region = opts.Require("region");
                    var catalog = RegionCatalog.LoadOrDefault(opts.Get("catalog") ?? DefaultCatalog());
                    var def = catalog.Find(region) ?? throw new ArgumentException($"Region '{region}' is not in the catalogue.");
                    var sources = UpdateService.LocalSources(def, opts.Get("pbf"), opts.All("addresses"), opts.All("poi"));
                    var staging = store.NewStagingDir(region);
                    RegionBuilder.Build(new BuildRequest
                    {
                        RegionId = def.Id,
                        RegionName = def.Name,
                        OutputDir = staging,
                        Sources = sources,
                        AedImports = UpdateService.AedImports(store).Concat(opts.All("aed").Select(a => (a, Path.GetFileNameWithoutExtension(a)))).ToList(),
                        BuiltBy = "MapBuilder"
                    }, progress);
                    return Publish(store, region, staging);
                }
                case "update":
                {
                    var store = new RegionStore(opts.Require("data"));
                    var catalog = RegionCatalog.LoadOrDefault(opts.Get("catalog") ?? DefaultCatalog());
                    var def = catalog.Find(opts.Require("region")) ?? throw new ArgumentException("Unknown region.");
                    var service = new UpdateService(store);
                    var result = service.UpdateAsync(def, progress, CancellationToken.None).GetAwaiter().GetResult();
                    Console.Error.WriteLine();
                    Console.WriteLine(result.Message);
                    return result.Success ? 0 : 1;
                }
                case "validate":
                {
                    var store = new RegionStore(opts.Require("data"));
                    var dir = store.CurrentDir(opts.Require("region")) ?? throw new InvalidOperationException("Region not installed.");
                    var report = RegionValidator.Validate(dir);
                    Console.WriteLine(report);
                    foreach (var s in report.SpotChecks)
                        Console.WriteLine("Spot-check: " + s);
                    return report.Ok ? 0 : 1;
                }
                case "rollback":
                {
                    var store = new RegionStore(opts.Require("data"));
                    var v = store.Rollback(opts.Require("region"));
                    Console.WriteLine(v == null ? "Nothing to roll back to." : $"Now using {v}.");
                    return v == null ? 1 : 0;
                }
                case "export":
                {
                    var store = new RegionStore(opts.Require("data"));
                    var path = UpdatePackage.Export(store, opts.Require("region"), opts.Require("out"), new Progress<double>(f => Console.Error.Write($"\rExporting {f,6:P0}")));
                    Console.Error.WriteLine();
                    Console.WriteLine($"Wrote {path}");
                    return 0;
                }
                case "install":
                {
                    var store = new RegionStore(opts.Require("data"));
                    var result = UpdatePackage.Install(store, opts.Require("package"), progress);
                    Console.Error.WriteLine();
                    Console.WriteLine(result.Message);
                    return result.Success ? 0 : 1;
                }
                case "imagery":
                    return Imagery(opts);
                case "coverage":
                    return Coverage(opts);
                case "aed":
                    return Aed(opts, progress);
                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.\n\n{Usage}");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Builds the cell-coverage layer from government KML/KMZ/GeoJSON files, clipped to the installed regions, into the
    /// data folder (the same place the Field Map Data window puts it, so the web server and the app both find it).
    /// </summary>
    private static int Coverage(Options opts)
    {
        var store = new RegionStore(opts.Require("data"));
        var files = opts.All("file").ToList();
        if (files.Count == 0)
            throw new ArgumentException("Give at least one --file <coverage.kml|kmz|geojson>.");
        var bounds = store.InstalledRegions().Select(store.CurrentManifest).OfType<RegionManifest>()
            .Select(m => new FieldMap.Core.Geo.GeoBounds(m.MinLat, m.MinLon, m.MaxLat, m.MaxLon))
            .Aggregate(FieldMap.Core.Geo.GeoBounds.Empty, (a, b) => a.Include(b));
        var output = Path.Combine(store.ImportsDir, FieldMap.Core.Radio.CellCoverage.Folder, FieldMap.Core.Radio.CellCoverage.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + ".building";
        var summary = FieldMap.Core.Radio.CellCoverage.Build(files, temp, bounds.IsEmpty ? null : bounds, new Progress<double>(f => Console.Error.Write($"\rBuilding coverage {f,6:P0}")));
        File.Move(temp, output, overwrite: true);
        Console.Error.WriteLine();
        Console.WriteLine($"Cell coverage: {summary.Areas:N0} areas ({summary.Description}), {summary.Tiles:N0} tiles -> {output}");
        foreach (var w in summary.Warnings.Take(10))
            Console.WriteLine("  " + w);
        return 0;
    }

    /// <summary>
    /// Imports an AED list the way Field Map's Data window does: sites with only a street address are placed with the
    /// installed regions' offline address search, the placed sites are stored as the CSV every build merges, and the
    /// rest are written to a list for checking. Builds only read sites that have a position, so lists like the BC PAD
    /// Program's (addresses only) must go through this step first.
    /// </summary>
    private static int Aed(Options opts, IProgress<(string Stage, double Fraction)> progress)
    {
        var store = new RegionStore(opts.Require("data"));
        var file = opts.Require("file");
        var name = Path.GetFileNameWithoutExtension(file);
        var rows = AedFile.Read(file, opts.Get("source") ?? name, opts.Get("sheet"));
        if (rows.Count == 0)
            throw new InvalidDataException("No AED sites found. The list needs a name or address column, and latitude/longitude columns or street addresses.");

        using var search = new FieldMap.Core.Search.SearchEngine();
        var regions = 0;
        foreach (var id in store.InstalledRegions())
            if (store.CurrentDir(id) is { } dir && File.Exists(Path.Combine(dir, RegionBuilder.SearchFile)))
            {
                search.AddRegion(Path.Combine(dir, RegionBuilder.SearchFile), null);
                regions++;
            }
        var report = AedFile.Locate(rows, regions > 0 ? q => search.Search(q, null, 8) : null);
        var placed = rows.Where(r => r.HasPosition).ToList();
        var notPlaced = rows.Where(r => !r.HasPosition).ToList();

        Console.WriteLine($"{rows.Count:N0} AED sites in {Path.GetFileName(file)} ({rows.Sum(r => r.Count ?? 1):N0} AEDs):");
        Console.WriteLine($"  {report.Given:N0} with coordinates");
        if (report.FromText > 0)
            Console.WriteLine($"  {report.FromText:N0} from coordinates written in the list");
        if (report.ByAddress > 0)
            Console.WriteLine($"  {report.ByAddress:N0} placed by street address (approximate, shown as such)");
        if (report.NotPlaced > 0)
            Console.WriteLine($"  {report.NotPlaced:N0} could not be placed" + (regions == 0
                ? " – no map data is installed to look addresses up; install the region first, then run this again"
                : " (address not in the map data, or no house number)"));
        foreach (var g in rows.GroupBy(r => r.Source))
            Console.WriteLine($"    {g.Key}: {g.Count(r => r.HasPosition):N0} of {g.Count():N0} placed");
        if (placed.Count == 0)
        {
            Console.WriteLine("Nothing could be placed; nothing was imported.");
            return 1;
        }

        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var temp = Path.Combine(Path.GetTempPath(), "mapbuilder-aed-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(temp);
        try
        {
            var csv = Path.Combine(temp, safe + ".csv");
            AedFile.WriteCsv(csv, placed, DateTime.UtcNow);
            var stored = UpdateService.AddAedImport(store, csv, opts.Get("source") ?? name);
            Console.WriteLine($"Stored {placed.Count:N0} placed sites in {stored}; every build from now on merges them.");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
        if (notPlaced.Count > 0)
        {
            var dir = Path.Combine(store.ImportsDir, "aed-not-placed");
            Directory.CreateDirectory(dir);
            var list = Path.Combine(dir, safe + "-not-placed.csv");
            AedFile.WriteCsv(list, notPlaced, DateTime.UtcNow);
            Console.WriteLine($"{notPlaced.Count:N0} sites without a position are listed in {list}. Add latitude/longitude there and import that file too.");
        }

        if (opts.Get("rebuild") == null)
        {
            Console.WriteLine("They appear on the map after the next build: run `MapBuilder update --region <id> --data <folder>` (or add --rebuild here).");
            return 0;
        }
        // Apply now from the source files already downloaded (as the Data window does).
        var service = new UpdateService(store);
        var catalog = RegionCatalog.LoadOrDefault(opts.Get("catalog") ?? DefaultCatalog());
        var ok = true;
        foreach (var id in store.InstalledRegions().ToList())
        {
            UpdateResult r;
            if (id == SampleRegion.RegionId)
                r = service.InstallSample(progress);
            else if (catalog.Find(id) is { } def && service.CachedSources(def) is { } files)
                r = service.BuildAndPublish(def, files, progress, CancellationToken.None);
            else
            {
                Console.WriteLine($"{id}: the downloaded sources aren't on this machine; run `MapBuilder update --region {id}` to merge the AEDs.");
                continue;
            }
            Console.Error.WriteLine();
            Console.WriteLine($"{id}: {r.Message}");
            ok &= r.Success;
        }
        return ok ? 0 : 1;
    }

    private static int Publish(RegionStore store, string region, string staging)
    {
        Console.Error.WriteLine();
        var report = RegionValidator.Validate(staging, store.CurrentManifest(region));
        Console.WriteLine(report);
        if (!report.Ok)
        {
            Console.WriteLine("Validation failed; the installed map data was not changed.");
            Directory.Delete(staging, true);
            return 1;
        }
        var version = store.Publish(region, staging);
        Console.WriteLine($"Installed {region} version {version} in {store.Root}.");
        return 0;
    }

    /// <summary>Saves imagery tiles of an area into an MBTiles package for offline use.</summary>
    private static int Imagery(Options opts)
    {
        ImagerySource source;
        if (opts.Get("source") is { } id)
        {
            var catalog = ImageryCatalog.LoadOrEmpty(opts.Get("catalog") ?? Path.Combine(AppContext.BaseDirectory, "imagery-sources.json"));
            source = catalog.Sources.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException($"Imagery source '{id}' is not in the catalogue.");
        }
        else
        {
            var url = opts.Get("url") ?? opts.Get("wms") ?? throw new ArgumentException("--source, --url or --wms is required.");
            source = new ImagerySource
            {
                Id = "custom", Name = opts.Get("name") ?? "Imagery", Kind = opts.Get("wms") != null ? "wms" : "xyz", Url = url,
                Layers = opts.Get("layers") ?? "", Format = opts.Get("format") ?? "image/jpeg",
                Attribution = opts.Get("attribution") ?? "", Licence = opts.Get("licence") ?? "", AllowOffline = true, MaxZoom = 22
            };
        }
        if (opts.Get("accept-terms") == null)
            throw new ArgumentException($"Saving imagery for offline use must be allowed by the source's terms. Check them, then add --accept-terms.\nSource: {source.Name}\nLicence: {source.Licence}\n{source.Terms}");
        if (opts.Get("source") != null && !source.AllowOffline)
            throw new ArgumentException($"The catalogue says the terms of {source.Name} don't allow offline use (\"allowOffline\": false).");
        source.AllowOffline = true;
        var bbox = opts.Require("bbox").Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (bbox.Length != 4)
            throw new ArgumentException("--bbox is minLon,minLat,maxLon,maxLat");
        var area = new FieldMap.Core.Geo.GeoBounds(bbox[1], bbox[0], bbox[3], bbox[2]);
        var zoom = opts.Require("zoom").Split('-');
        var minZoom = int.Parse(zoom[0], System.Globalization.CultureInfo.InvariantCulture);
        var maxZoom = int.Parse(zoom[^1], System.Globalization.CultureInfo.InvariantCulture);
        var maxTiles = long.Parse(opts.Get("max-tiles") ?? "500000", System.Globalization.CultureInfo.InvariantCulture);
        var total = ImageryMath.CountTiles(area, Math.Max(minZoom, source.MinZoom), Math.Min(maxZoom, source.MaxZoom));
        Console.WriteLine($"{total:N0} tiles (about {total * 25 / 1024.0:N0} MB) from {source.Name}, zoom {minZoom}–{maxZoom}.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var fetcher = new ImageryFetcher(http, "FieldMap-MapBuilder/1.0") { Concurrency = int.Parse(opts.Get("concurrency") ?? "2", System.Globalization.CultureInfo.InvariantCulture) };
        using var store = new RasterTileStore(opts.Require("out"));
        var result = fetcher.SeedAsync(source, area, minZoom, maxZoom, store, opts.Get("key") ?? "",
            new Progress<SeedProgress>(p => Console.Error.Write($"\r{p.Done:N0}/{p.Total:N0} tiles, {p.Failed:N0} not available   ")), CancellationToken.None, maxTiles).GetAwaiter().GetResult();
        if (opts.Get("name") is { } name)
            store.SetMetadata(new Dictionary<string, string> { ["name"] = name });
        Console.Error.WriteLine();
        Console.WriteLine($"Saved {result.Fetched:N0} new tiles ({result.Skipped:N0} already there, {result.Failed:N0} not available) to {opts.Get("out")}. Install it in Field Map under Data & updates → Install imagery package.");
        return 0;
    }

    private static string DefaultCatalog() => Path.Combine(AppContext.BaseDirectory, "regions.json");

    private const string Usage = """
        MapBuilder – build and package Field Map region data (offline maps for BC and Alberta)

          sample-osm <file.osm>                        Write the fictitious Sample County as OSM XML
          build-sample --data <folder>                 Build and install the sample region
          build --region bc|ab --data <folder> [--catalog regions.json] --pbf <extract.osm.pbf>
                [--addresses <ODA csv/zip>]... [--poi <id>=<file>]... [--aed <csv/geojson>]...
          update --region bc|ab --data <folder> [--catalog regions.json]
                                                       Download the latest sources, build, validate, install
          validate --region <id> --data <folder>       Check the installed version and list spot-check entries
          rollback --region <id> --data <folder>       Switch back to the previous version
          export --region <id> --data <folder> --out <folder|file.fmupdate>
                                                       Write an offline update package (for USB)
          install --package <file.fmupdate> --data <folder>
                                                       Verify and install an update package
          aed --data <folder> --file <list.xlsx|csv|geojson|kml> [--sheet <name>] [--source <name>] [--rebuild]
                                                       Import an AED list; sites with only an address are placed
                                                       with the installed map data's address search
          coverage --data <folder> --file <coverage.kml|kmz|geojson> [--file …]
                                                       Build the cell-coverage layer, clipped to the installed regions
          imagery (--source <id> [--catalog imagery-sources.json] | --url <xyz template> | --wms <url> --layers <names>)
                  --bbox minLon,minLat,maxLon,maxLat --zoom 10-17 --out <file.mbtiles> --accept-terms
                  [--key <token>] [--name <text>] [--attribution <text>] [--licence <text>] [--max-tiles 500000]
                                                       Save imagery of an area as an offline package (resumable)
        """;
}

internal sealed class Options
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
                continue;
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
            if (!o._values.TryGetValue(key, out var list))
                o._values[key] = list = new List<string>();
            list.Add(value);
        }
        return o;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v[^1] : null;

    public IEnumerable<string> All(string key) => _values.TryGetValue(key, out var v) ? v : Enumerable.Empty<string>();

    public string Require(string key) => Get(key) ?? throw new ArgumentException($"--{key} is required.");
}
