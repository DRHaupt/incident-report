using System.Collections.Concurrent;
using FieldMap.Core.Imagery;
using FieldMap.Map;

namespace FieldMap.Services;

/// <summary>
/// Aerial/satellite imagery for the Imagery and Hybrid base maps: installed MBTiles packages (fully offline) and, when
/// an administrator has enabled one in Content/imagery-sources.json, an online source whose tiles are cached as they
/// are viewed. Areas can be saved for offline use when the source's terms allow it.
/// </summary>
public sealed class ImageryService : IDisposable
{
    private const long CacheTiles = 40_000; // ≈ 1 GB of JPEG tiles

    private readonly MapSettings _settings;
    private readonly SynchronizationContext? _ui;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentQueue<(int Z, int X, int Y)> _queue = new();
    private readonly SemaphoreSlim _workers = new(4);
    private RasterTileStore? _package;
    private RasterTileStore? _saved;
    private RasterTileStore? _cache;
    private RasterTileCache? _tiles;
    private ImagerySource? _current;
    private int _failures;
    private DateTime _lastRetry = DateTime.UtcNow;
    private int _generation;

    public ImageryService(MapSettings settings, ImageryCatalog catalog, Func<string> dataFolder)
    {
        _settings = settings;
        Catalog = catalog;
        DataFolder = dataFolder;
        _ui = SynchronizationContext.Current;
        Fetcher = new ImageryFetcher(_http, $"FieldMap/{Branding.Version} (offline emergency map)");
    }

    public ImageryCatalog Catalog { get; }
    public Func<string> DataFolder { get; }
    public ImageryFetcher Fetcher { get; }

    public string Folder => Path.Combine(DataFolder(), "imagery");

    /// <summary>A tile arrived or the selection changed (redraw maps).</summary>
    public event EventHandler? Changed;

    public BaseMapMode Mode => _settings.BaseMode;
    public ImagerySource? Current => _current;

    /// <summary>Tiles for the renderer, or null when the map is shown without imagery.</summary>
    public RasterTileCache? Tiles => _settings.BaseMode == BaseMapMode.Map ? null : _tiles;

    /// <summary>"Imagery: offline – showing saved tiles" etc.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Installed packages (excluding the online cache).</summary>
    public List<ImagerySource> Packages()
    {
        var list = new List<ImagerySource>();
        if (!Directory.Exists(Folder))
            return list;
        foreach (var f in Directory.GetFiles(Folder, "*.mbtiles").OrderBy(f => f))
        {
            try
            {
                list.Add(RasterTileStore.Describe(f));
            }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
            {
                // Unreadable file: not offered.
            }
        }
        return list;
    }

    /// <summary>Online sources enabled by an administrator (and with a key, if they need one).</summary>
    public List<ImagerySource> OnlineSources() =>
        Catalog.Sources.Where(s => s.Enabled && s.IsOnline && !string.IsNullOrWhiteSpace(s.Url) && (!s.NeedsKey || Key(s).Length > 0)).ToList();

    public List<ImagerySource> Available() => Packages().Concat(OnlineSources()).ToList();

    public string Key(ImagerySource s) => _settings.ImageryKeys.TryGetValue(s.Id, out var k) ? k : "";

    public bool TermsAccepted(ImagerySource s) => !s.IsOnline || string.IsNullOrWhiteSpace(s.Terms) || _settings.AcceptedImageryTerms.Contains(s.Id);

    public void AcceptTerms(ImagerySource s)
    {
        if (!_settings.AcceptedImageryTerms.Contains(s.Id))
            _settings.AcceptedImageryTerms.Add(s.Id);
        _settings.Save();
    }

    /// <summary>Opens the selected source (or the first available one). Call at start-up and after changes.</summary>
    public void Apply()
    {
        CloseStores();
        _generation++;
        var available = Available();
        var source = available.FirstOrDefault(s => s.Id == _settings.ImageryId) ?? available.FirstOrDefault(TermsAccepted);
        if (source == null || !TermsAccepted(source))
        {
            _current = null;
            Status = available.Count == 0 ? "No imagery installed or set up" : "Imagery terms not accepted yet";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        _current = source;
        try
        {
            if (source.Kind == "mbtiles")
            {
                _package = new RasterTileStore(source.FilePath!, readOnly: true);
                _tiles = new RasterTileCache(source.MinZoom, source.MaxZoom, _package.Get);
            }
            else
            {
                var saved = SavedPath(source);
                if (File.Exists(saved))
                    _saved = new RasterTileStore(saved, readOnly: true);
                if (source.AllowOffline)
                    _cache = new RasterTileStore(Path.Combine(Folder, "cache", Safe(source.Id) + ".mbtiles"));
                _tiles = new RasterTileCache(source.MinZoom, source.MaxZoom, Read, Request);
            }
            _tiles.TileArrived += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            Status = source.IsOnline ? "Online imagery" : "Installed imagery";
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            CloseStores();
            _current = null;
            Status = "Imagery unavailable: " + ex.Message;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string SavedPath(ImagerySource s) => Path.Combine(Folder, Safe(s.Id) + "-saved.mbtiles");

    private static string Safe(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private byte[]? Read(int z, int x, int y) => _saved?.Get(z, x, y) ?? _cache?.Get(z, x, y) ?? Memory(z, x, y);

    // Sources that don't allow keeping tiles are held in memory only, for this session.
    private readonly ConcurrentDictionary<(int, int, int), byte[]> _memory = new();

    private byte[]? Memory(int z, int x, int y) => _memory.TryGetValue((z, x, y), out var d) ? d : null;

    private void Request(int z, int x, int y)
    {
        if (_current is not { IsOnline: true } source || !source.Covers(z, x, y))
            return;
        _queue.Enqueue((z, x, y));
        _ = PumpAsync(source, _generation);
    }

    private async Task PumpAsync(ImagerySource source, int generation)
    {
        if (!await _workers.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            while (generation == _generation && _queue.TryDequeue(out var t))
            {
                byte[]? data = null;
                try
                {
                    data = await Fetcher.FetchAsync(source, t.Z, t.X, t.Y, Key(source), CancellationToken.None).ConfigureAwait(false);
                    Interlocked.Exchange(ref _failures, 0);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    Interlocked.Increment(ref _failures);
                }
                if (generation != _generation)
                    return;
                if (data != null)
                {
                    if (_cache != null)
                        _cache.Put(t.Z, t.X, t.Y, data);
                    else if (_memory.Count < 3000)
                        _memory[(t.Z, t.X, t.Y)] = data;
                }
                var ok = data != null;
                Post(() =>
                {
                    if (generation == _generation)
                        _tiles?.Arrived(t.Z, t.X, t.Y, ok);
                });
            }
        }
        finally
        {
            _workers.Release();
        }
    }

    private void Post(Action a)
    {
        if (_ui != null)
            _ui.Post(_ => a(), null);
        else
            a();
    }

    /// <summary>Once a second (app clock): status, retry after being offline, keep the cache to size.</summary>
    public void Tick()
    {
        if (_current is not { IsOnline: true })
            return;
        var offline = Volatile.Read(ref _failures) >= 3;
        var status = offline ? "Imagery: offline – showing saved tiles" : "Online imagery";
        if (status != Status)
        {
            Status = status;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        if (DateTime.UtcNow - _lastRetry > TimeSpan.FromMinutes(2))
        {
            _lastRetry = DateTime.UtcNow;
            if (offline)
            {
                Interlocked.Exchange(ref _failures, 0);
                _tiles?.RetryMissing();
                Changed?.Invoke(this, EventArgs.Empty);
            }
            _ = Task.Run(() => _cache?.TrimTo(CacheTiles));
        }
    }

    public void SetMode(BaseMapMode mode)
    {
        _settings.BaseMode = mode;
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Select(ImagerySource source)
    {
        _settings.ImageryId = source.Id;
        _settings.Save();
        Apply();
    }

    /// <summary>Saves the imagery of an area for offline use (sources that allow it).</summary>
    public async Task<SeedProgress> SaveAreaAsync(GeoBounds area, int minZoom, int maxZoom, IProgress<SeedProgress> progress, CancellationToken cancel)
    {
        if (_current is not { IsOnline: true } source)
            throw new InvalidOperationException("Choose an online imagery source first.");
        var path = SavedPath(source);
        // The read-only handle is reopened after saving.
        _saved?.Dispose();
        _saved = null;
        SeedProgress result;
        using (var store = new RasterTileStore(path))
            result = await Fetcher.SeedAsync(source, area, minZoom, maxZoom, store, Key(source), progress, cancel).ConfigureAwait(true);
        Apply();
        return result;
    }

    /// <summary>Copies an imagery package into the data folder.</summary>
    public ImagerySource Install(string file)
    {
        var desc = RasterTileStore.Describe(file);
        if (desc.MaxZoom == 0 && desc.MinZoom == 0)
            throw new InvalidDataException("The file has no tiles.");
        Directory.CreateDirectory(Folder);
        var target = Path.Combine(Folder, Path.GetFileName(file));
        File.Copy(file, target, overwrite: false);
        return RasterTileStore.Describe(target);
    }

    public void Delete(ImagerySource package)
    {
        if (package.FilePath == null)
            return;
        if (_current?.FilePath == package.FilePath || (_current != null && SavedPath(_current) == package.FilePath))
            CloseStores();
        File.Delete(package.FilePath);
        Apply();
    }

    private void CloseStores()
    {
        _tiles?.Dispose();
        _tiles = null;
        _package?.Dispose();
        _package = null;
        _saved?.Dispose();
        _saved = null;
        _cache?.Dispose();
        _cache = null;
        _memory.Clear();
        while (_queue.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        _generation++;
        CloseStores();
        _http.Dispose();
    }
}
