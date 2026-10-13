using FieldMap.Core.Build;
using FieldMap.Core.Data;
using FieldMap.Core.Routing;
using FieldMap.Core.Search;
using FieldMap.Core.Terrain;
using FieldMap.Core.Tiles;
using FieldMap.Map;

namespace FieldMap.Services;

/// <summary>One installed region version, opened for drawing, search and routing.</summary>
public sealed class LoadedRegion : IDisposable
{
    public required string Id { get; init; }
    public required string Dir { get; init; }
    public required RegionManifest Manifest { get; init; }
    public required MbTilesReader Tiles { get; init; }

    /// <summary>Loaded in the background (the BC graph is a few hundred MB).</summary>
    public required Task<RoadGraph?> Graph { get; init; }

    public GeoBounds Bounds => new(Manifest.MinLat, Manifest.MinLon, Manifest.MaxLat, Manifest.MaxLon);

    public TileSource Source => new(Tiles, Bounds);

    private Lazy<TerrainGrid?>? _terrain;

    /// <summary>The region's elevation grid, if its map data includes one (opened on first use).</summary>
    public TerrainGrid? Terrain => (_terrain ??= new Lazy<TerrainGrid?>(() =>
    {
        var path = Path.Combine(Dir, TerrainGrid.FileName);
        try
        {
            return File.Exists(path) ? TerrainGrid.Open(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    })).Value;

    public void Dispose()
    {
        Tiles.Dispose();
        if (_terrain is { IsValueCreated: true, Value: { } t })
            t.Dispose();
    }
}

/// <summary>
/// The map data in use: every installed region (the combined BC + Alberta package replaces the separate ones), their
/// search index, facilities and routing graphs. Reloaded after an update, rollback or package install.
/// </summary>
public sealed class MapDataService : IDisposable
{
    public const string Combined = "bcab";

    private readonly List<LoadedRegion> _regions = new();
    private SearchEngine? _search;

    public MapDataService(RegionStore store) => Store = store;

    public RegionStore Store { get; private set; }
    public IReadOnlyList<LoadedRegion> Regions => _regions;
    public SearchEngine Search => _search ??= new SearchEngine();
    public List<Poi> Pois { get; private set; } = new();
    public PoiIndex PoiIndex { get; private set; } = new(Array.Empty<Poi>());

    /// <summary>Messages about fallbacks (damaged current version) to show the user once.</summary>
    public List<string> Messages { get; } = new();

    public bool HasData => _regions.Count > 0;
    public bool IsSample => _regions.Any(r => r.Manifest.IsSample);

    public event EventHandler? Changed;

    public DateTime? DataDate => _regions.Select(r => r.Manifest.DataDate).Where(d => d != null).DefaultIfEmpty(null).Min();

    /// <summary>Attribution text for the loaded data (shown on the map and on exports).</summary>
    public string Attribution => _regions.Count == 0
        ? "No map data installed"
        : string.Join(" · ", _regions.SelectMany(r => r.Manifest.Sources).Select(s => s.Attribution).Where(a => a.Length > 0).Distinct());

    public void UseStore(RegionStore store)
    {
        Store = store;
        Load();
    }

    public void Load()
    {
        Close();
        Messages.Clear();
        Store.CleanStaging();
        var ids = Store.InstalledRegions().ToList();
        if (ids.Contains(Combined))
            ids.RemoveAll(id => id is "bc" or "ab");
        var search = new SearchEngine();
        foreach (var id in ids)
        {
            var (version, message) = Store.ResolveCurrent(id);
            if (message != null)
                Messages.Add(message);
            if (version == null)
                continue;
            var dir = Store.VersionDir(id, version);
            try
            {
                var manifest = RegionManifest.Load(dir);
                var graphPath = Path.Combine(dir, RegionBuilder.GraphFile);
                var graphTask = Task.Run(() =>
                {
                    try
                    {
                        return RoadGraph.Load(graphPath);
                    }
                    catch (Exception)
                    {
                        return (RoadGraph?)null;
                    }
                });
                var region = new LoadedRegion { Id = id, Dir = dir, Manifest = manifest, Tiles = new MbTilesReader(Path.Combine(dir, RegionBuilder.TilesFile)), Graph = graphTask };
                _regions.Add(region);
                search.AddRegion(Path.Combine(dir, RegionBuilder.SearchFile), null);
            }
            catch (Exception ex)
            {
                Messages.Add($"The {id} map data could not be opened: {ex.Message}");
            }
        }
        _search = search;
        try
        {
            CellCoverage = File.Exists(CellCoveragePath) ? new MbTilesReader(CellCoveragePath) : null;
        }
        catch (Exception ex)
        {
            CellCoverage = null;
            Messages.Add("The cell coverage layer could not be opened: " + ex.Message);
        }
        Pois = search.LoadPois();
        PoiIndex = new PoiIndex(Pois);
        // Give the search engine the graphs once they are loaded (for "What's here?" road names).
        for (var i = 0; i < _regions.Count; i++)
        {
            var index = i;
            _ = _regions[i].Graph.ContinueWith(t =>
            {
                if (t.Result is { } g && ReferenceEquals(_search, search))
                    search.AttachGraph(index, g);
            }, TaskScheduler.Default);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The imported cell-coverage tileset (Data &amp; updates → Cell coverage), if any.</summary>
    public MbTilesReader? CellCoverage { get; private set; }

    /// <summary>Releases the cell-coverage file (before replacing or deleting it).</summary>
    public void CloseCellCoverage()
    {
        CellCoverage?.Dispose();
        CellCoverage = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string CellCoveragePath => Path.Combine(Store.ImportsDir, Core.Radio.CellCoverage.Folder, Core.Radio.CellCoverage.FileName);

    /// <summary>Elevation grid covering a point, from whichever installed region has one.</summary>
    public TerrainGrid? TerrainAt(GeoPoint p) =>
        _regions.Where(r => r.Bounds.Expand(2000).Contains(p.Lat, p.Lon)).Select(r => r.Terrain).FirstOrDefault(t => t != null);

    public bool HasTerrain => _regions.Any(r => File.Exists(Path.Combine(r.Dir, TerrainGrid.FileName)));

    public LoadedRegion? RegionAt(GeoPoint p) =>
        _regions.FirstOrDefault(r => r.Bounds.Expand(2000).Contains(p.Lat, p.Lon));

    /// <summary>The region whose network covers both points (for routing), if any.</summary>
    public LoadedRegion? RegionFor(GeoPoint a, GeoPoint b) =>
        _regions.FirstOrDefault(r => r.Bounds.Expand(5000).Contains(a.Lat, a.Lon) && r.Bounds.Expand(5000).Contains(b.Lat, b.Lon));

    public GeoBounds Bounds => _regions.Aggregate(GeoBounds.Empty, (b, r) => b.Include(r.Bounds));

    public Poi? FindPoi(long id) => Pois.FirstOrDefault(p => p.Id == id);

    private void Close()
    {
        foreach (var r in _regions)
            r.Dispose();
        _regions.Clear();
        CellCoverage?.Dispose();
        CellCoverage = null;
        _search?.Dispose();
        _search = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public void Dispose() => Close();
}
