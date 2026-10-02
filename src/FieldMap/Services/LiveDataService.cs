using FieldMap.Core.Live;

namespace FieldMap.Services;

/// <summary>
/// Optional live layers (road events, wildfire perimeters, evacuation orders, weather alerts). Fetched in the background
/// when enabled; when offline the last cached result is shown with its time. Nothing else depends on them.
/// </summary>
public sealed class LiveDataService : IDisposable
{
    private readonly LiveFeedService _service;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60_000 };
    private readonly Dictionary<string, DateTime> _lastAttempt = new();
    private readonly MapSettings _settings;
    private bool _busy;

    public LiveDataService(LiveFeedCatalog catalog, string cacheDir, MapSettings settings)
    {
        Catalog = catalog;
        _settings = settings;
        _service = new LiveFeedService(new HttpClient(), cacheDir);
        foreach (var f in catalog.Feeds)
            Results[f.Id] = _service.Cached(f);
        _timer.Tick += async (_, _) => await RefreshAsync(false);
    }

    public LiveFeedCatalog Catalog { get; }
    public Dictionary<string, LiveResult> Results { get; } = new();

    public event EventHandler? Changed;

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync(false);
    }

    public IEnumerable<LiveFeedDefinition> ActiveFeeds =>
        Catalog.Feeds.Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Url) && _settings.FeedOn(f.Id));

    public async Task RefreshAsync(bool force)
    {
        if (_busy || !_settings.ShowLiveData)
            return;
        _busy = true;
        try
        {
            foreach (var feed in ActiveFeeds)
            {
                if (feed.NeedsKey)
                    feed.Key = _settings.Ab511Key;
                if (!force && _lastAttempt.TryGetValue(feed.Id, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(feed.RefreshMinutes))
                    continue;
                _lastAttempt[feed.Id] = DateTime.UtcNow;
                Results[feed.Id] = await _service.FetchAsync(feed);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    public static string Label(LiveResult r) => LiveFeedService.Label(r);

    public void Dispose() => _timer.Dispose();
}
