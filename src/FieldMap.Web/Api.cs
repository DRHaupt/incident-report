using System.Globalization;
using FieldMap.Core.Build;
using FieldMap.Core.Layers;
using FieldMap.Core.Overlays;
using FieldMap.Core.Routing;
using FieldMap.Core.Search;
using FieldMap.Core.Tiles;

namespace FieldMap.Web;

/// <summary>The JSON and tile endpoints the phone app uses.</summary>
public static class Api
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly byte[] EmptyGzip = Convert.FromHexString("1f8b080000000000001303000000000000000000");

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/status", (MapHost host, AccessGate gate, HttpContext http) =>
            Results.Json(new { ok = true, hasData = host.Current.HasData, codeRequired = gate.Enabled, signedIn = gate.IsAllowed(http) }));

        app.MapPost("/api/login", async (HttpContext http, AccessGate gate) =>
        {
            var body = await http.Request.ReadFromJsonAsync<LoginRequest>();
            var problem = gate.TryLogin(http, body?.Code);
            return problem == null ? Results.Json(new { ok = true }) : Results.Json(new { ok = false, error = problem }, statusCode: 403);
        });

        app.MapGet("/imagery/{id}/{z:int}/{x:int}/{y:int}", (string id, int z, int x, int y, ImageryProxy imagery, HttpContext http, CancellationToken cancel) =>
            imagery.TileAsync(id, z, x, y, http, cancel));

        app.MapGet("/api/info", (MapHost host, EventFiles events, ImageryProxy imagery, AprsHub aprs, LiveHub live) =>
        {
            var s = host.Current;
            return Results.Json(new
            {
                version = s.Version,
                hasData = s.HasData,
                dataDate = s.DataDate?.ToString("yyyy-MM-dd", Inv),
                regions = s.Regions.Select(r => new
                {
                    id = r.Id,
                    name = r.Manifest.RegionName,
                    version = r.Manifest.Version,
                    isSample = r.Manifest.IsSample,
                    dataDate = r.Manifest.DataDate?.ToString("yyyy-MM-dd", Inv),
                    bounds = new[] { r.Bounds.MinLon, r.Bounds.MinLat, r.Bounds.MaxLon, r.Bounds.MaxLat },
                    minZoom = r.Tiles.MinZoom,
                    maxZoom = r.Tiles.MaxZoom
                }),
                layers = LayerCatalog.All.Where(l => l.Id != LayerCatalog.Hazard).Select(l => new
                {
                    id = l.Id,
                    title = l.Title,
                    glyph = l.Glyph,
                    color = "#" + (l.Color & 0xFFFFFF).ToString("x6", Inv),
                    minZoom = l.MinZoom,
                    defaultOn = l.DefaultOn,
                    warning = l.Warning,
                    count = s.PoisByLayer.TryGetValue(l.Id, out var list) ? list.Count : 0
                }),
                aprs = aprs.Enabled,
                live = live.Enabled && live.Feeds.Count > 0,
                coverage = s.Coverage == null ? null : new { maxZoom = s.Coverage.MaxZoom, description = s.Coverage.Metadata.GetValueOrDefault("description", "") },
                imagery = imagery.Sources.Select(i => new
                {
                    id = i.Id, name = i.Name, attribution = i.Attribution, terms = i.Terms, licence = i.Licence,
                    notes = i.Notes, minZoom = i.MinZoom, maxZoom = i.MaxZoom, bounds = i.Bounds
                }),
                repeaters = s.Repeaters.Count,
                events = events.List().Count,
                problems = s.Problems
            });
        });

        // ------------------------------------------------------------------ tiles and fonts

        app.MapGet("/tiles/{region}/{z:int}/{x:int}/{y:int}.pbf", (string region, int z, int x, int y, MapHost host, AccessGate gate, HttpContext http) =>
        {
            var s = host.Current;
            var tiles = region == "coverage" ? s.Coverage : s.Regions.FirstOrDefault(r => r.Id == region)?.Tiles;
            if (tiles == null)
                return Results.NotFound();
            if (z < 0 || z > 22 || x < 0 || y < 0 || x >= 1 << z || y >= 1 << z)
                return Results.BadRequest();
            var raw = tiles.GetRaw(z, x, y);
            if (raw == null || raw.Length == 0)
                return Results.NoContent();
            // URLs carry ?v=<data version>, so a tile never changes under the same URL. With an access code they're
            // "private": a CDN in front (e.g. a Cloudflare cache rule) must not hand them to phones that aren't signed in.
            http.Response.Headers.CacheControl = (gate.Enabled ? "private" : "public") + ", max-age=604800, immutable";
            if (raw.Length > 2 && raw[0] == 0x1f && raw[1] == 0x8b)
                http.Response.Headers.ContentEncoding = "gzip";
            return Results.Bytes(raw, "application/x-protobuf");
        });

        app.MapGet("/fonts/{stack}/{range}.pbf", (string stack, string range, IWebHostEnvironment env, HttpContext http) =>
        {
            http.Response.Headers.ContentEncoding = "gzip";
            http.Response.Headers.CacheControl = "public, max-age=2592000";
            if (!System.Text.RegularExpressions.Regex.IsMatch(range, @"^\d{1,5}-\d{1,5}$"))
                return Results.Bytes(EmptyGzip, "application/x-protobuf");
            var path = Path.Combine(env.WebRootPath, "fonts", range + ".pbf.gz");
            return Results.Bytes(File.Exists(path) ? File.ReadAllBytes(path) : EmptyGzip, "application/x-protobuf");
        });

        // ------------------------------------------------------------------ points of interest

        app.MapGet("/api/pois", (string bbox, string? layers, MapHost host) =>
        {
            if (!TryBox(bbox, out var box))
                return Results.BadRequest(new { error = "bbox=minLon,minLat,maxLon,maxLat" });
            var wanted = (layers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            var s = host.Current;
            var features = new List<object>();
            var truncated = false;
            foreach (var (layer, list) in s.PoisByLayer)
            {
                if (!wanted.Contains(layer))
                    continue;
                foreach (var p in list)
                {
                    if (!box.Contains(p.Lat, p.Lon))
                        continue;
                    if (features.Count >= 4000)
                    {
                        truncated = true;
                        break;
                    }
                    features.Add(new
                    {
                        type = "Feature",
                        id = p.Id,
                        geometry = new { type = "Point", coordinates = new[] { Math.Round(p.Lon, 6), Math.Round(p.Lat, 6) } },
                        properties = new { id = p.Id, layer = p.Layer, kind = p.Kind, name = p.DisplayName }
                    });
                }
            }
            return Results.Json(new { type = "FeatureCollection", truncated, features });
        });

        app.MapGet("/api/poi/{id:long}", (long id, MapHost host) =>
        {
            var p = host.Current.PoisByLayer.Values.SelectMany(l => l).FirstOrDefault(x => x.Id == id);
            return p == null ? Results.NotFound() : Results.Json(PoiJson(p, null));
        });

        app.MapGet("/api/business/{id:long}", (long id, MapHost host) =>
        {
            var b = host.Current.Search.GetBusiness(id);
            return b == null ? Results.NotFound() : Results.Json(new
            {
                id = b.Id, name = b.Name, kind = b.Kind, lat = b.Lat, lon = b.Lon,
                address = b.Address, phone = b.Phone, hours = b.Hours, website = b.Website,
                source = "OpenStreetMap", coords = Coords(b.Point)
            });
        });

        app.MapGet("/api/repeaters", (MapHost host) => Results.Json(host.Current.Repeaters.Select(r => new
        {
            label = r.Label, name = r.Name, callSign = r.CallSign, lat = r.Lat, lon = r.Lon,
            frequency = r.FrequencyText, mode = r.Mode, owner = r.Owner, notes = r.Notes,
            source = Core.Radio.Repeater.SourceLabel(r.Source)
        })));

        // ------------------------------------------------------------------ search, nearest, what's here

        app.MapGet("/api/search", (string q, double? lat, double? lon, MapHost host) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Length > 200)
                return Results.Json(Array.Empty<object>());
            GeoPoint? near = lat is { } a && lon is { } o ? new GeoPoint(a, o) : null;
            var results = host.Current.Search.Search(q, near, 25);
            return Results.Json(results.Select(r => new
            {
                kind = r.Kind.ToString().ToLowerInvariant(),
                name = r.Name,
                detail = r.Detail,
                lat = r.Point.Lat,
                lon = r.Point.Lon,
                layer = r.Layer,
                poiId = r.Kind == ResultKind.Poi ? r.PoiId : null,
                businessId = r.Kind == ResultKind.Business ? r.PoiId : null,
                approximate = r.Approximate,
                distance = near is { } n && r.Kind != ResultKind.Category ? Math.Round(n.DistanceTo(r.Point)) : (double?)null
            }));
        });

        app.MapGet("/api/nearest", async (string layer, double lat, double lon, int? count, string? profile, MapHost host, CancellationToken cancel) =>
        {
            var def = LayerCatalog.Find(layer);
            if (def == null)
                return Results.NotFound();
            var s = host.Current;
            var origin = new GeoPoint(lat, lon);
            var n = Math.Clamp(count ?? 8, 1, 25);
            var candidates = s.Search.NearestPois(layer, origin, Math.Max(n * 4, 30));
            var region = s.RegionAt(origin);
            var vehicle = Profile(profile);
            List<Router.Reach>? reach = null;
            if (region?.Routers is { } pool && candidates.Count > 0)
            {
                var points = candidates.Select(c => c.Point).ToList();
                reach = await pool.RunAsync(r => r.NearestByTime(origin, points, vehicle, n), cancel);
            }
            var items = reach != null && reach.Count > 0
                ? reach.Select(r => PoiJson(candidates[r.Index], origin, r.DurationSeconds, r.DistanceMeters)).ToList()
                : candidates.Take(n).Select(c => PoiJson(c, origin)).ToList();
            return Results.Json(new { layer = def.Title, warning = def.Warning, byDriveTime = reach is { Count: > 0 }, items });
        });

        app.MapGet("/api/whatshere", (double lat, double lon, MapHost host) =>
        {
            var s = host.Current;
            var p = new GeoPoint(lat, lon);
            var w = s.HasData ? s.Search.Reverse(p) : null;
            return Results.Json(new
            {
                lat, lon,
                summary = w?.Summary ?? "No map data here",
                address = w?.Address,
                addressDistance = w?.AddressDistance,
                intersection = w?.Intersection,
                road = w?.Road,
                locality = w?.Locality,
                region = s.RegionAt(p)?.Manifest.RegionName,
                coords = Coords(p)
            });
        });

        // ------------------------------------------------------------------ directions

        app.MapGet("/api/route", async (string from, string to, string? profile, MapHost host, CancellationToken cancel) =>
        {
            if (!TryPoint(from, out var a) || !TryPoint(to, out var b))
                return Results.BadRequest(new { error = "from=lat,lon&to=lat,lon" });
            var s = host.Current;
            var region = s.RegionFor(a, b);
            if (region == null)
                return Results.Json(new { error = s.RegionAt(a) != null && s.RegionAt(b) != null
                    ? "The start and destination are in different regions."
                    : "This location is outside the map data on the server." }, statusCode: 422);
            if (region.Routers is not { } pool)
                return Results.Json(new { error = "This region has no routing data." }, statusCode: 422);
            var vehicle = Profile(profile);
            var route = await pool.RunAsync(r => r.Route(a, b, vehicle), cancel);
            if (route == null)
                return Results.Json(new { error = "No route found. One of the points may be too far from a road this vehicle can use." }, statusCode: 422);
            return Results.Json(new
            {
                profile = vehicle.Name,
                distance = Math.Round(route.DistanceMeters),
                duration = Math.Round(route.DurationSeconds),
                distanceText = Instructions.FormatDistance(route.DistanceMeters),
                durationText = Instructions.FormatDuration(route.DurationSeconds),
                startOffset = Math.Round(route.StartOffsetMeters),
                endOffset = Math.Round(route.EndOffsetMeters),
                warnings = route.Warnings,
                geometry = route.Geometry.Select(p => new[] { Math.Round(p.Lon, 6), Math.Round(p.Lat, 6) }),
                steps = route.Steps.Select(st => new
                {
                    maneuver = st.Maneuver.ToString().ToLowerInvariant(),
                    instruction = st.Instruction,
                    road = st.Road,
                    distance = Math.Round(st.DistanceMeters),
                    distanceText = Instructions.FormatDistance(st.DistanceMeters),
                    along = Math.Round(st.AlongMeters),
                    lat = st.Location.Lat,
                    lon = st.Location.Lon
                })
            });
        });

        // ------------------------------------------------------------------ event files

        app.MapGet("/api/events", (EventFiles events) => Results.Json(events.List().Select(e => new
        {
            id = e.Id, name = e.Name, file = e.File, summary = e.Summary, bytes = e.Bytes, modified = e.ModifiedUtc
        })));

        app.MapGet("/api/events/{id}", (string id, EventFiles events) =>
            events.Get(id) is { } json ? Results.Content(json.ToJsonString(), "application/geo+json") : Results.NotFound());

        // A file chosen on the phone is converted here (so KMZ, CSV and styled KML read exactly as on the PC) and kept
        // on the phone only; nothing is stored on the server.
        app.MapPost("/api/convert", async (HttpRequest request, WebOptions opts, CancellationToken cancel) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Send the file as multipart/form-data." });
            var form = await request.ReadFormAsync(cancel);
            var file = form.Files.FirstOrDefault();
            if (file == null)
                return Results.BadRequest(new { error = "No file." });
            if (file.Length > opts.MaxUploadMb * 1024L * 1024)
                return Results.Json(new { error = $"The file is larger than {opts.MaxUploadMb} MB." }, statusCode: 413);
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!MapFileImporter.Extensions.Contains(ext))
                return Results.Json(new { error = "Choose a KML, KMZ, GPX, GeoJSON or CSV file." }, statusCode: 415);
            var temp = Path.Combine(Path.GetTempPath(), "fieldmap-web-" + Guid.NewGuid().ToString("N") + ext);
            try
            {
                await using (var fs = File.Create(temp))
                    await file.CopyToAsync(fs, cancel);
                var content = await Task.Run(() => MapFileImporter.LoadLarge(temp, 400_000), cancel);
                var name = content.Name.Length > 0 ? content.Name : Path.GetFileNameWithoutExtension(file.FileName);
                var json = OverlayGeoJson.ToFeatureCollection(content, "local-" + Guid.NewGuid().ToString("N")[..10], name);
                return Results.Content(json.ToJsonString(), "application/geo+json");
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or System.Xml.XmlException or System.Text.Json.JsonException or IOException)
            {
                return Results.Json(new { error = "The file couldn't be read: " + ex.Message }, statusCode: 422);
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }).DisableAntiforgery();
    }

    public sealed record LoginRequest(string? Code);

    internal static VehicleProfile Profile(string? name) => name?.ToLowerInvariant() switch
    {
        "emergency" => VehicleProfile.Emergency,
        "walking" => VehicleProfile.Walking,
        "truck" => VehicleProfile.Truck(),
        _ => VehicleProfile.Car
    };

    internal static object Coords(GeoPoint p) => new
    {
        dec = CoordinateFormat.Decimal(p.Lat, p.Lon),
        ddm = CoordinateFormat.Ddm(p.Lat, p.Lon),
        dms = CoordinateFormat.Dms(p.Lat, p.Lon),
        mgrs = CoordinateFormat.Mgrs(p.Lat, p.Lon),
        utm = CoordinateFormat.Utm(p.Lat, p.Lon)
    };

    private static object PoiJson(Poi p, GeoPoint? from, double? driveSeconds = null, double? driveMeters = null) => new
    {
        id = p.Id,
        layer = p.Layer,
        kind = p.Kind,
        kindLabel = p.KindLabel,
        name = p.DisplayName,
        lat = p.Lat,
        lon = p.Lon,
        props = p.Props,
        source = p.Source,
        lastVerified = p.LastVerified,
        warning = LayerCatalog.Find(p.Layer)?.Warning,
        distance = from is { } f ? Math.Round(f.DistanceTo(p.Point)) : (double?)null,
        driveSeconds = driveSeconds is { } d ? Math.Round(d) : (double?)null,
        driveMeters = driveMeters is { } m ? Math.Round(m) : (double?)null,
        coords = Coords(p.Point)
    };

    internal static bool TryPoint(string? s, out GeoPoint p)
    {
        p = default;
        var parts = (s ?? "").Split(',');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, Inv, out var lat) || !double.TryParse(parts[1], NumberStyles.Float, Inv, out var lon))
            return false;
        if (lat is < -90 or > 90 || lon is < -180 or > 180)
            return false;
        p = new GeoPoint(lat, lon);
        return true;
    }

    internal static bool TryBox(string? s, out GeoBounds box)
    {
        box = default;
        var v = (s ?? "").Split(',').Select(x => double.TryParse(x, NumberStyles.Float, Inv, out var d) ? d : double.NaN).ToArray();
        if (v.Length != 4 || v.Any(double.IsNaN))
            return false;
        box = new GeoBounds(Math.Min(v[1], v[3]), Math.Min(v[0], v[2]), Math.Max(v[1], v[3]), Math.Max(v[0], v[2]));
        return true;
    }
}
