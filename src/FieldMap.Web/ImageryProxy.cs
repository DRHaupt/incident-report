using FieldMap.Core.Imagery;

namespace FieldMap.Web;

/// <summary>
/// Online satellite/aerial imagery for the phones, from the same imagery-sources.json as the Windows app. Phones ask
/// this server for tiles and the server fetches them from the imagery service, so it works behind the access code
/// and a Cloudflare Tunnel, keys stay on the server, and only the sources set up here can be reached (it isn't an
/// open proxy). Nothing is stored: tiles are only kept briefly in the phone's browser cache, as when viewing online.
/// </summary>
public sealed class ImageryProxy
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _keys;
    private readonly Dictionary<string, SemaphoreSlim> _slots = new();
    private readonly ILogger<ImageryProxy> _log;

    public ImageryProxy(WebOptions options, IWebHostEnvironment env, ILogger<ImageryProxy> log)
    {
        _log = log;
        _keys = options.ImageryKeys;
        var path = File.Exists(options.ImageryCatalog) ? options.ImageryCatalog : Path.Combine(env.ContentRootPath, "Content", "imagery-sources.json");
        if (!File.Exists(path))
            path = Path.Combine(AppContext.BaseDirectory, "Content", "imagery-sources.json");
        var catalog = ImageryCatalog.LoadOrEmpty(path);
        Sources = catalog.Sources
            .Where(s => s.Enabled && s.IsOnline && Allowed(s.Url))
            .Where(s => !s.NeedsKey || _keys.ContainsKey(s.Id))
            .ToList();
        foreach (var s in Sources)
            _slots[s.Id] = new SemaphoreSlim(8);
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FieldMapWeb/1.0 (emergency response map; +https://github.com/DRHaupt/incident-report)");
        log.LogInformation("Online imagery: {Sources}", Sources.Count == 0 ? "none" : string.Join(", ", Sources.Select(s => s.Name)));
    }

    public IReadOnlyList<ImagerySource> Sources { get; }

    // HTTPS services only (plain http just on this machine, for a local imagery server or tests).
    private static bool Allowed(string url) =>
        Uri.TryCreate(url.Replace("{z}", "0").Replace("{x}", "0").Replace("{y}", "0").Replace("{-y}", "0").Replace("{key}", ""), UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    public async Task<IResult> TileAsync(string id, int z, int x, int y, HttpContext http, CancellationToken cancel)
    {
        var source = Sources.FirstOrDefault(s => s.Id == id);
        if (source == null)
            return Results.NotFound();
        if (z < 0 || z > 22 || x < 0 || y < 0 || x >= 1 << z || y >= 1 << z)
            return Results.BadRequest();
        if (!source.Covers(z, x, y))
            return Results.NoContent();
        var slot = _slots[id];
        await slot.WaitAsync(cancel);
        try
        {
            using var resp = await _http.GetAsync(source.TileUrl(z, x, y, _keys.GetValueOrDefault(id, "")), cancel);
            if (resp.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NoContent)
                return Results.NoContent();
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            var data = await resp.Content.ReadAsByteArrayAsync(cancel);
            // Services answer "no imagery here" with an error page or a blank; only pass real images on.
            if (!ImageryMath.IsImage(data))
                return Results.NoContent();
            http.Response.Headers.CacheControl = "private, max-age=86400";
            var type = resp.Content.Headers.ContentType?.MediaType;
            return Results.Bytes(data, type is { } t && t.StartsWith("image/") ? t : source.Format);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            _log.LogDebug(ex, "Imagery tile {Id} {Z}/{X}/{Y} failed", id, z, x, y);
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        finally
        {
            slot.Release();
        }
    }
}
