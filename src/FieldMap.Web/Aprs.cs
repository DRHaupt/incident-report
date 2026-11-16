using System.Globalization;
using FieldMap.Core.Aprs;
using FieldMap.Core.Geo;

namespace FieldMap.Web;

/// <summary>APRS-IS settings ("AprsIs" under "FieldMap" in appsettings.json). Off unless Enabled is true.</summary>
public sealed class AprsIsOptions
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = "noam.aprs2.net";
    public int Port { get; set; } = 14580;

    /// <summary>Optional call sign for the login. The server never transmits, so no passcode is used either way.</summary>
    public string Callsign { get; set; } = "";

    /// <summary>Extra APRS-IS filter terms added to the area filter, e.g. "b/VE7ABC*".</summary>
    public string ExtraFilter { get; set; } = "";

    /// <summary>Stations not heard for this long are left off the phones' map.</summary>
    public int ShowMinutes { get; set; } = 60;

    /// <summary>How much of a moving station's track the phones get.</summary>
    public int TrackMinutes { get; set; } = 60;
}

/// <summary>
/// Listens to APRS-IS, receive-only, for the area of the installed map regions, and keeps the latest state of every
/// station heard (reusing the Windows app's APRS parser and station tracker). Phones read it from /api/aprs.
/// </summary>
public sealed class AprsHub : BackgroundService
{
    private readonly AprsIsOptions _options;
    private readonly MapHost _host;
    private readonly ILogger<AprsHub> _log;
    private AprsIsClient? _client;

    public AprsHub(WebOptions options, MapHost host, ILogger<AprsHub> log)
    {
        _options = options.AprsIs;
        _host = host;
        _log = log;
    }

    public AprsStationTracker Tracker { get; } = new() { MaxStations = 10000, MaxTrackPoints = 400 };
    public bool Enabled => _options.Enabled;
    public AprsIsOptions Options => _options;
    public AprsLinkState State => _client?.State ?? AprsLinkState.Off;
    public string StatusText => _client?.StatusText ?? "Off";
    public long PacketCount => _client?.PacketCount ?? 0;
    public DateTime? LastPacketUtc => _client?.LastPacketUtc;
    public string Filter => _client?.Filter ?? "";

    /// <summary>
    /// The area filter: one APRS-IS "a/" (box) term per installed region, from its map bounds, plus any extra terms.
    /// </summary>
    public static string AreaFilter(IEnumerable<GeoBounds> regions, string extra)
    {
        var inv = CultureInfo.InvariantCulture;
        var terms = regions.Select(b => string.Format(inv, "a/{0:0.##}/{1:0.##}/{2:0.##}/{3:0.##}", b.MaxLat, b.MinLon, b.MinLat, b.MaxLon)).ToList();
        if (!string.IsNullOrWhiteSpace(extra)) terms.Add(extra.Trim());
        return string.Join(' ', terms);
    }

    private string CurrentFilter() => AreaFilter(_host.Current.Regions.Select(r => r.Bounds), _options.ExtraFilter);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!_options.Enabled)
            return;
        var filter = CurrentFilter();
        var login = AprsBeacon.IsValidCallsign(_options.Callsign) ? _options.Callsign.Trim().ToUpperInvariant() : "FMWEB" + Random.Shared.Next(1000, 9999);
        // Passcode "-1": receive-only. The web server never sends anything to APRS-IS.
        _client = new AprsIsClient(_options.Server.Trim(), _options.Port, login, "-1", filter, "FieldMapWeb", typeof(AprsHub).Assembly.GetName().Version?.ToString(3) ?? "1");
        _client.PacketReceived += (_, line) =>
        {
            try { Tracker.Process(AprsParser.Parse(line), AprsVia.Internet, DateTime.UtcNow); }
            catch (Exception ex) { _log.LogDebug(ex, "APRS packet skipped: {Line}", line); }
        };
        var lastState = "";
        _client.StateChanged += (_, _) =>
        {
            var s = _client.StatusText;
            if (s != lastState) { lastState = s; _log.LogInformation("APRS-IS: {Status}", s); }
        };
        _log.LogInformation("APRS-IS: connecting to {Server}:{Port} as {Login} (receive-only), filter {Filter}", _options.Server, _options.Port, login, filter.Length > 0 ? filter : "(none: no map regions yet)");
        _client.Start();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stop);
                Tracker.Purge(DateTime.UtcNow, TimeSpan.FromHours(12), TimeSpan.FromMinutes(Math.Max(5, _options.TrackMinutes)));
                // A region added or removed (MapBuilder): follow it without a restart.
                var now = CurrentFilter();
                if (now != _client.Filter && _client.State == AprsLinkState.Connected)
                {
                    try { await _client.SetFilterAsync(now, stop); _log.LogInformation("APRS-IS: filter now {Filter}", now); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException) { _log.LogWarning("APRS-IS: couldn't change the filter: {Message}", ex.Message); }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _client.Stop();
            _client.Dispose();
        }
    }

    // ------------------------------------------------------------------ what phones get

    private object? _cache;
    private int _cacheVersion = -1;
    private int _served;          // changes on every rebuild, so phones also see stations ageing out
    private DateTime _cacheAt;
    private readonly object _cacheLock = new();

    /// <summary>
    /// Stations with a position heard within ShowMinutes, as compact JSON-ready objects. Cached until the tracker changes
    /// (and at most 30 s, so ages and the time window move on), so many phones polling cost one snapshot.
    /// </summary>
    public (int Version, object Stations) Stations()
    {
        var v = Tracker.Version;
        lock (_cacheLock)
        {
            if (_cache != null && v == _cacheVersion && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(30))
                return (_served, _cache);
            var now = DateTime.UtcNow;
            var cutoff = now - TimeSpan.FromMinutes(Math.Max(1, _options.ShowMinutes));
            var trackCutoff = now - TimeSpan.FromMinutes(Math.Max(1, _options.TrackMinutes));
            _cache = Tracker.Snapshot()
                .Where(s => s.HasPosition && s.LastHeardUtc >= cutoff)
                .OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => new
                {
                    key = s.Key,
                    source = s.Source,
                    isObject = s.IsObject,
                    lat = Math.Round(s.Lat!.Value, 5),
                    lon = Math.Round(s.Lon!.Value, 5),
                    ambiguity = s.Ambiguity,
                    symbol = s.SymbolName,
                    glyph = AprsSymbols.Glyph(s.SymbolTable, s.SymbolCode),
                    group = AprsSymbols.Group(s.SymbolTable, s.SymbolCode).ToString().ToLowerInvariant(),
                    overlay = AprsSymbols.Overlay(s.SymbolTable)?.ToString(),
                    course = s.CourseDegrees,
                    speedKmh = s.SpeedKmh is { } k ? Math.Round(k) : (double?)null,
                    altitude = s.AltitudeMeters is { } a ? Math.Round(a) : (double?)null,
                    comment = s.Comment,
                    status = s.Status,
                    micE = s.MicEMessage,
                    emergency = s.Emergency,
                    weather = s.Weather?.ToString(),
                    path = s.Path,
                    heard = s.LastHeardUtc,
                    ageSeconds = Math.Round((now - s.LastHeardUtc).TotalSeconds),
                    track = s.Track.Count > 1
                        ? s.Track.Where(p => p.TimeUtc >= trackCutoff).Select(p => new[] { Math.Round(p.Lon, 5), Math.Round(p.Lat, 5) }).ToArray()
                        : Array.Empty<double[]>()
                })
                .ToList();
            _cacheVersion = v;
            _cacheAt = now;
            _served++;
            return (_served, _cache);
        }
    }
}

public static class AprsApi
{
    public static void Map(WebApplication app)
    {
        // Polled by phones that show the APRS layer. ?v= is the version they already have: when nothing changed they
        // get a small "unchanged" answer instead of every station again.
        app.MapGet("/api/aprs", (int? v, AprsHub hub) =>
        {
            if (!hub.Enabled)
                return Results.Json(new { enabled = false });
            var status = new
            {
                state = hub.State.ToString().ToLowerInvariant(),
                text = hub.StatusText,
                packets = hub.PacketCount,
                lastPacket = hub.LastPacketUtc,
                server = hub.Options.Server
            };
            var (version, stations) = hub.Stations();
            return v == version
                ? Results.Json(new { enabled = true, version, unchanged = true, status })
                : Results.Json(new { enabled = true, version, status, showMinutes = hub.Options.ShowMinutes, stations });
        });
    }
}
