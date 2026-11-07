using FieldMap.Core.Build;
using FieldMap.Core.Data;
using FieldMap.Core.Radio;
using FieldMap.Core.Routing;
using FieldMap.Core.Search;
using FieldMap.Core.Tiles;

namespace FieldMap.Web;

/// <summary>One installed region: its manifest, tiles and (loaded on first use) routing graph.</summary>
public sealed class RegionData : IDisposable
{
    private readonly Lazy<RoadGraph?> _graph;
    private readonly Lazy<RouterPool?> _routers;

    public RegionData(string id, string dir)
    {
        Id = id;
        Dir = dir;
        Manifest = RegionManifest.Load(dir);
        Tiles = new MbTilesReader(Path.Combine(dir, RegionBuilder.TilesFile));
        Bounds = new GeoBounds(Manifest.MinLat, Manifest.MinLon, Manifest.MaxLat, Manifest.MaxLon);
        _graph = new Lazy<RoadGraph?>(() =>
        {
            var path = Path.Combine(dir, RegionBuilder.GraphFile);
            return File.Exists(path) ? RoadGraph.Load(path) : null;
        });
        _routers = new Lazy<RouterPool?>(() => Graph is { } g ? new RouterPool(g) : null);
    }

    public string Id { get; }
    public string Dir { get; }
    public RegionManifest Manifest { get; }
    public MbTilesReader Tiles { get; }
    public GeoBounds Bounds { get; }
    public RoadGraph? Graph => _graph.Value;
    public RouterPool? Routers => _routers.Value;

    public void Dispose() => Tiles.Dispose();
}

/// <summary>Routers aren't thread-safe and hold large search arrays, so a few are shared between requests.</summary>
public sealed class RouterPool
{
    private readonly RoadGraph _graph;
    private readonly Stack<Router> _free = new();
    private readonly SemaphoreSlim _slots = new(Math.Clamp(Environment.ProcessorCount, 1, 4));

    public RouterPool(RoadGraph graph) => _graph = graph;

    public async Task<T> RunAsync<T>(Func<Router, T> work, CancellationToken cancel)
    {
        await _slots.WaitAsync(cancel);
        Router? router = null;
        try
        {
            lock (_free)
                _free.TryPop(out router);
            router ??= new Router(_graph);
            var r = router;
            r.Closed.Clear();
            return await Task.Run(() => work(r), cancel);
        }
        finally
        {
            if (router != null)
                lock (_free)
                    _free.Push(router);
            _slots.Release();
        }
    }
}

/// <summary>Everything loaded from the data folder at one moment. Replaced as a whole when MapBuilder installs new data.</summary>
public sealed class MapSnapshot : IDisposable
{
    public required IReadOnlyList<RegionData> Regions { get; init; }
    public required SearchEngine Search { get; init; }
    public required IReadOnlyDictionary<string, List<Poi>> PoisByLayer { get; init; }
    public MbTilesReader? Coverage { get; init; }
    public IReadOnlyList<Repeater> Repeaters { get; init; } = Array.Empty<Repeater>();
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    /// <summary>Changes whenever the data changes; used to tell phones to drop cached tiles.</summary>
    public required string Version { get; init; }

    public bool HasData => Regions.Count > 0;

    public RegionData? RegionAt(GeoPoint p) => Regions.FirstOrDefault(r => r.Bounds.Contains(p.Lat, p.Lon));

    public RegionData? RegionFor(GeoPoint a, GeoPoint b) => Regions.FirstOrDefault(r => r.Bounds.Contains(a.Lat, a.Lon) && r.Bounds.Contains(b.Lat, b.Lon));

    public DateTime? DataDate => Regions.Select(r => r.Manifest.DataDate).Where(d => d != null).DefaultIfEmpty(null).Min();

    public void Dispose()
    {
        Search.Dispose();
        Coverage?.Dispose();
        foreach (var r in Regions)
            r.Dispose();
    }
}

/// <summary>
/// Loads the regions installed in the data folder (the same layout Field Map and MapBuilder use) and checks once a
/// minute for newly installed versions, so a nightly <c>MapBuilder update</c> is picked up without a restart.
/// </summary>
public sealed class MapHost : IDisposable
{
    private readonly ILogger<MapHost> _log;
    private readonly Timer _timer;
    private readonly object _reloadLock = new();
    private MapSnapshot _current;
    private string _fingerprint = "";

    public MapHost(WebOptions options, ILogger<MapHost> log)
    {
        _log = log;
        Store = new RegionStore(options.DataDir);
        Options = options;
        _current = Empty();
        Reload();
        _timer = new Timer(_ => Reload(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public RegionStore Store { get; }
    public WebOptions Options { get; }
    public MapSnapshot Current => Volatile.Read(ref _current);

    public string CoveragePath => Path.Combine(Store.ImportsDir, CellCoverage.Folder, CellCoverage.FileName);
    public string RepeatersPath => Path.Combine(Store.Root, "repeaters.json");

    private static MapSnapshot Empty() => new()
    {
        Regions = Array.Empty<RegionData>(),
        Search = new SearchEngine(),
        PoisByLayer = new Dictionary<string, List<Poi>>(),
        Version = "none"
    };

    /// <summary>Reloads when the installed versions, cell coverage or repeater list changed. Returns true when it did.</summary>
    public bool Reload()
    {
        lock (_reloadLock)
        {
            try
            {
                var regions = Store.InstalledRegions()
                    .Where(id => Options.Regions.Count == 0 || Options.Regions.Contains(id, StringComparer.OrdinalIgnoreCase))
                    .Select(id => (Id: id, Dir: Store.CurrentDir(id)))
                    .Where(r => r.Dir != null)
                    .OrderBy(r => r.Id, StringComparer.Ordinal)
                    .ToList();
                var fingerprint = string.Join("|", regions.Select(r => r.Id + "=" + Path.GetFileName(r.Dir))) +
                                  "|cov=" + Stamp(CoveragePath) + "|rpt=" + Stamp(RepeatersPath);
                if (fingerprint == _fingerprint)
                    return false;

                var problems = new List<string>();
                var loaded = new List<RegionData>();
                var search = new SearchEngine();
                foreach (var (id, dir) in regions)
                {
                    try
                    {
                        var region = new RegionData(id, dir!);
                        search.AddRegion(Path.Combine(dir!, RegionBuilder.SearchFile), null);
                        loaded.Add(region);
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"Region {id} could not be loaded: {ex.Message}");
                        _log.LogError(ex, "Region {Region} could not be loaded from {Dir}", id, dir);
                    }
                }
                var pois = search.LoadPois().GroupBy(p => p.Layer).ToDictionary(g => g.Key, g => g.ToList());

                MbTilesReader? coverage = null;
                if (File.Exists(CoveragePath))
                {
                    try { coverage = new MbTilesReader(CoveragePath); }
                    catch (Exception ex) { problems.Add("Cell coverage could not be loaded: " + ex.Message); }
                }

                IReadOnlyList<Repeater> repeaters = Array.Empty<Repeater>();
                if (File.Exists(RepeatersPath))
                {
                    try
                    {
                        // RepeaterBook's terms don't allow passing its data on, so those entries stay on the PC.
                        repeaters = new RepeaterStore(RepeatersPath).All.Where(r => r.Source != RepeaterSource.RepeaterBook).ToList();
                    }
                    catch (Exception ex) { problems.Add("The repeater list could not be loaded: " + ex.Message); }
                }

                var next = new MapSnapshot
                {
                    Regions = loaded,
                    Search = search,
                    PoisByLayer = pois,
                    Coverage = coverage,
                    Repeaters = repeaters,
                    Problems = problems,
                    Version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprint)))[..12].ToLowerInvariant()
                };
                var old = Interlocked.Exchange(ref _current, next);
                _fingerprint = fingerprint;
                _log.LogInformation("Map data loaded: {Regions} ({Version})", loaded.Count == 0 ? "no regions" : string.Join(", ", loaded.Select(r => r.Id + " " + r.Manifest.Version)), next.Version);
                // Requests that started on the old data may still be using it.
                _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Map data reload failed");
                return false;
            }
        }
    }

    private static string Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString() : "-";

    public void Dispose()
    {
        _timer.Dispose();
        _current.Dispose();
    }
}
