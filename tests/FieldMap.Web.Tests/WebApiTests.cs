using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldMap.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FieldMap.Web.Tests;

/// <summary>Builds the fictitious sample region once, the way MapBuilder installs it, for all the API tests.</summary>
public sealed class ServerFixture : IDisposable
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "fm-web-" + Guid.NewGuid().ToString("N"));

    public ServerFixture()
    {
        var store = new RegionStore(DataDir);
        var staging = store.NewStagingDir("sample");
        RegionBuilder.BuildSample(staging, Path.Combine(DataDir, "work"));
        store.Publish("sample", staging);
        Directory.CreateDirectory(Path.Combine(DataDir, "events"));
        File.WriteAllText(Path.Combine(DataDir, "events", "fair.kml"), """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>Fair</name>
              <Folder><name>Posts</name><Placemark><name>First Aid 1</name><description>AED here</description><Point><coordinates>-125.903,57.406</coordinates></Point></Placemark></Folder>
              <Placemark><name>Grounds</name><Style><PolyStyle><color>440000ff</color></PolyStyle></Style><Polygon><outerBoundaryIs><LinearRing><coordinates>-125.905,57.404 -125.899,57.404 -125.899,57.407 -125.905,57.404</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>
            </Document></kml>
            """);
    }

    private WebApplicationFactory<Program>? _default;

    /// <summary>One server shared by the tests that don't change its settings.</summary>
    public WebApplicationFactory<Program> Default => _default ??= Factory();

    public WebApplicationFactory<Program> Factory(string? accessCode = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("FieldMap:DataDir", DataDir);
            if (accessCode != null)
                b.UseSetting("FieldMap:AccessCode", accessCode);
        });

    public void Dispose()
    {
        _default?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class WebApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fx;
    private readonly HttpClient _http;

    public WebApiTests(ServerFixture fx)
    {
        _fx = fx;
        _http = fx.Default.CreateClient();
    }

    private async Task<JsonElement> Json(string url)
    {
        var res = await _http.GetAsync(url);
        Assert.True(res.IsSuccessStatusCode, $"{url}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Info_lists_the_region_layers_and_event_files()
    {
        var info = await Json("/api/info");
        Assert.True(info.GetProperty("hasData").GetBoolean());
        var region = Assert.Single(info.GetProperty("regions").EnumerateArray());
        Assert.Equal("sample", region.GetProperty("id").GetString());
        Assert.Contains(info.GetProperty("layers").EnumerateArray(), l => l.GetProperty("id").GetString() == "hospital" && l.GetProperty("count").GetInt32() > 0);
        Assert.Equal(1, info.GetProperty("events").GetInt32());
        // The imagery list shipped with the apps: online satellite out of the box, with its terms.
        var imagery = info.GetProperty("imagery").EnumerateArray().ToList();
        Assert.Contains(imagery, i => i.GetProperty("id").GetString() == "esri-world-imagery" && i.GetProperty("terms").GetString()!.Length > 20);
        Assert.DoesNotContain(imagery, i => i.GetProperty("id").GetString() == "esri-world-imagery-export"); // needs a key
    }

    [Fact]
    public async Task Tiles_come_back_gzipped_with_long_caching_and_missing_tiles_are_empty()
    {
        var info = await Json("/api/info");
        var b = info.GetProperty("regions")[0].GetProperty("bounds").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var (x, y) = Core.Geo.WebMercator.Tile((b[1] + b[3]) / 2, (b[0] + b[2]) / 2, 12);
        var res = await _http.GetAsync($"/tiles/sample/12/{x}/{y}.pbf?v=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/x-protobuf", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("gzip", res.Content.Headers.ContentEncoding);
        Assert.Contains("immutable", res.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await _http.GetAsync("/tiles/sample/12/0/0.pbf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/tiles/nowhere/1/0/0.pbf")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/tiles/sample/3/9/0.pbf")).StatusCode);
    }

    [Fact]
    public async Task Search_nearest_and_whats_here_answer_from_the_region()
    {
        var results = await Json("/api/search?q=hospital&lat=57.40&lon=-125.95");
        Assert.Equal("category", results[0].GetProperty("kind").GetString());
        Assert.Contains(results.EnumerateArray(), r => r.GetProperty("kind").GetString() == "poi" && r.GetProperty("name").GetString()!.Contains("Hospital"));

        var nearest = await Json("/api/nearest?layer=hospital&lat=57.40&lon=-125.95&count=2");
        Assert.True(nearest.GetProperty("byDriveTime").GetBoolean());
        var first = nearest.GetProperty("items")[0];
        Assert.True(first.GetProperty("driveSeconds").GetDouble() > 0);
        Assert.StartsWith("10V ", first.GetProperty("coords").GetProperty("mgrs").GetString());

        var poi = await Json($"/api/poi/{first.GetProperty("id").GetInt64()}");
        Assert.Equal(first.GetProperty("name").GetString(), poi.GetProperty("name").GetString());

        var here = await Json("/api/whatshere?lat=57.405&lon=-125.9");
        Assert.Contains("Sampleton", here.GetProperty("summary").GetString());
        Assert.Equal("10V CJ 25780 66185", here.GetProperty("coords").GetProperty("mgrs").GetString());
    }

    [Fact]
    public async Task Pois_are_limited_to_the_box_and_the_chosen_layers()
    {
        var all = await Json("/api/pois?bbox=-126.1,57.3,-125.7,57.5&layers=hospital,fire");
        var layers = all.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("properties").GetProperty("layer").GetString()).Distinct().ToList();
        Assert.Contains("hospital", layers);
        Assert.All(layers, l => Assert.True(l is "hospital" or "fire"));
        var none = await Json("/api/pois?bbox=-80,43,-79,44&layers=hospital");
        Assert.Empty(none.GetProperty("features").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/api/pois?bbox=nope")).StatusCode);
    }

    [Fact]
    public async Task Route_gives_geometry_and_steps_or_a_plain_reason()
    {
        var r = await Json("/api/route?from=57.40,-125.95&to=57.4093,-125.9053&profile=emergency");
        Assert.True(r.GetProperty("distance").GetDouble() > 1000);
        Assert.True(r.GetProperty("geometry").GetArrayLength() > 2);
        Assert.Equal("depart", r.GetProperty("steps")[0].GetProperty("maneuver").GetString());
        Assert.Equal("Emergency vehicle", r.GetProperty("profile").GetString());

        var outside = await _http.GetAsync("/api/route?from=57.40,-125.95&to=49.28,-123.12");
        Assert.Equal(422, (int)outside.StatusCode);
        Assert.Contains("outside", (await outside.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Event_files_in_the_folder_and_uploaded_files_become_geojson()
    {
        var list = await Json("/api/events");
        var entry = Assert.Single(list.EnumerateArray());
        Assert.Equal("Fair", entry.GetProperty("name").GetString());
        var fc = await Json($"/api/events/{entry.GetProperty("id").GetString()}");
        var features = fc.GetProperty("features").EnumerateArray().ToList();
        var point = features.Single(f => f.GetProperty("geometry").GetProperty("type").GetString() == "Point");
        Assert.Equal("Posts", point.GetProperty("properties").GetProperty("folder").GetString());
        var area = features.Single(f => f.GetProperty("geometry").GetProperty("type").GetString() == "Polygon");
        Assert.Equal("#ff0000", area.GetProperty("properties").GetProperty("fill").GetString());
        var ring = area.GetProperty("geometry").GetProperty("coordinates")[0];
        Assert.Equal(ring[0].ToString(), ring[ring.GetArrayLength() - 1].ToString());

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("""{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"name":"Command post"},"geometry":{"type":"Point","coordinates":[-125.9,57.4]}}]}"""), "file", "cp.geojson");
        var converted = await _http.PostAsync("/api/convert", form);
        Assert.Equal(HttpStatusCode.OK, converted.StatusCode);
        var json = await converted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Command post", json.GetProperty("features")[0].GetProperty("properties").GetProperty("name").GetString());

        using var bad = new MultipartFormDataContent();
        bad.Add(new StringContent("hello"), "file", "notes.docx");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _http.PostAsync("/api/convert", bad)).StatusCode);
    }

    [Fact]
    public async Task The_app_and_label_fonts_are_served()
    {
        var html = await _http.GetStringAsync("/");
        Assert.Contains("js/app.js", html);
        var font = await _http.GetAsync("/fonts/Noto%20Sans%20Regular/0-255.pbf");
        Assert.Equal(HttpStatusCode.OK, font.StatusCode);
        Assert.Contains("gzip", font.Content.Headers.ContentEncoding);
        var missing = await _http.GetAsync("/fonts/Noto%20Sans%20Regular/65280-65535.pbf");
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode); // an empty range, so the map doesn't log errors
        Assert.Equal("application/manifest+json", (await _http.GetAsync("/manifest.webmanifest")).Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task With_an_access_code_the_data_needs_the_cookie_and_wrong_codes_are_refused()
    {
        using var factory = _fx.Factory("blue-heron-42");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/info")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/tiles/sample/0/0/0.pbf")).StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.True(status.GetProperty("codeRequired").GetBoolean());
        Assert.False(status.GetProperty("signedIn").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode); // the app itself loads, to show the sign-in

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/login", new { code = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/login", new { code = "blue-heron-42" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/info")).StatusCode);

        // Behind a CDN (Cloudflare) the tiles must not be cached for everyone once they need the code.
        var info = await client.GetFromJsonAsync<JsonElement>("/api/info");
        var b = info.GetProperty("regions")[0].GetProperty("bounds").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var (x, y) = Core.Geo.WebMercator.Tile((b[1] + b[3]) / 2, (b[0] + b[2]) / 2, 12);
        var tile = await client.GetAsync($"/tiles/sample/12/{x}/{y}.pbf?v=1");
        Assert.Equal(HttpStatusCode.OK, tile.StatusCode);
        Assert.True(tile.Headers.CacheControl?.Private);
        Assert.False(tile.Headers.CacheControl?.Public);
    }
}

/// <summary>Online imagery: only configured sources, real images passed on, behind the access code.</summary>
public sealed class ImageryProxyTests : IClassFixture<ServerFixture>, IDisposable
{
    private readonly ServerFixture _fx;
    private readonly System.Net.HttpListener _upstream = new();
    private readonly string _catalog = Path.Combine(Path.GetTempPath(), "fm-imagery-" + Guid.NewGuid().ToString("N") + ".json");
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    public int Requests;

    public ImageryProxyTests(ServerFixture fx)
    {
        _fx = fx;
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _upstream.Prefixes.Add($"http://127.0.0.1:{port}/");
        _upstream.Start();
        _ = Task.Run(async () =>
        {
            while (_upstream.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await _upstream.GetContextAsync(); } catch { return; }
                Interlocked.Increment(ref Requests);
                // "/tile/5/10/12" is a real image; "/tile/5/0/0" answers with an HTML error page like some services do.
                var empty = ctx.Request.Url!.AbsolutePath.EndsWith("/0/0");
                var body = empty ? "<html>no imagery</html>"u8.ToArray() : Png;
                ctx.Response.ContentType = empty ? "text/html" : "image/png";
                ctx.Response.OutputStream.Write(body);
                ctx.Response.Close();
            }
        });
        File.WriteAllText(_catalog, $$"""
            { "sources": [
              { "id": "test-sat", "name": "Test imagery", "kind": "xyz", "url": "http://127.0.0.1:{{port}}/tile/{z}/{x}/{y}", "maxZoom": 18,
                "attribution": "TEST", "licence": "test", "terms": "Test terms", "enabled": true },
              { "id": "off", "name": "Not enabled", "kind": "xyz", "url": "https://example.org/{z}/{x}/{y}", "attribution": "x", "licence": "x", "terms": "x", "enabled": false },
              { "id": "keyed", "name": "Needs a key", "kind": "xyz", "url": "https://example.org/{z}/{x}/{y}?k={key}", "needsKey": true, "attribution": "x", "licence": "x", "terms": "x", "enabled": true },
              { "id": "remote-http", "name": "Plain http elsewhere", "kind": "xyz", "url": "http://example.org/{z}/{x}/{y}", "attribution": "x", "licence": "x", "terms": "x", "enabled": true }
            ] }
            """);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory(string? code = null) =>
        _fx.Factory(code).WithWebHostBuilder(b => b.UseSetting("FieldMap:ImageryCatalog", _catalog));

    [Fact]
    public async Task Only_enabled_https_or_local_sources_without_missing_keys_are_offered_and_served()
    {
        using var factory = Factory();
        var http = factory.CreateClient();
        var info = await http.GetFromJsonAsync<JsonElement>("/api/info");
        var ids = info.GetProperty("imagery").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "test-sat" }, ids);
        Assert.Equal("Test terms", info.GetProperty("imagery")[0].GetProperty("terms").GetString());

        var tile = await http.GetAsync("/imagery/test-sat/5/10/12");
        Assert.Equal(HttpStatusCode.OK, tile.StatusCode);
        Assert.Equal("image/png", tile.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await tile.Content.ReadAsByteArrayAsync());
        Assert.True(tile.Headers.CacheControl?.Private);

        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync("/imagery/test-sat/5/0/0")).StatusCode); // not an image
        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync("/imagery/test-sat/19/0/0")).StatusCode); // beyond max zoom
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/imagery/off/1/0/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/imagery/keyed/1/0/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/imagery/remote-http/1/0/0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/imagery/test-sat/3/9/0")).StatusCode);
    }

    [Fact]
    public async Task Imagery_needs_the_access_code_when_one_is_set()
    {
        using var factory = Factory("heron");
        var http = factory.CreateClient();
        var before = Requests;
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/imagery/test-sat/5/10/12")).StatusCode);
        Assert.Equal(before, Requests); // nothing fetched for a phone that isn't signed in
    }

    public void Dispose()
    {
        _upstream.Close();
        File.Delete(_catalog);
    }

    [Fact]
    public async Task The_installed_app_signs_in_with_a_token_from_its_own_origin()
    {
        using var factory = _fx.Factory("blue-heron-42");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        HttpRequestMessage FromApp(HttpMethod method, string path, string origin = "capacitor://localhost")
        {
            var m = new HttpRequestMessage(method, path);
            m.Headers.Add("Origin", origin);
            return m;
        }

        // The app's browser asks first (no sign-in on that request): allowed for the app, with the Authorization header.
        var ask = FromApp(HttpMethod.Options, "/api/info");
        ask.Headers.Add("Access-Control-Request-Method", "GET");
        ask.Headers.Add("Access-Control-Request-Headers", "authorization");
        var allowed = await client.SendAsync(ask);
        Assert.True(allowed.IsSuccessStatusCode);
        Assert.Equal("capacitor://localhost", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("authorization", string.Join(",", allowed.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);

        // Not signed in: refused, but readable by the app so it can show the sign-in.
        var refused = await client.SendAsync(FromApp(HttpMethod.Get, "/api/info"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("capacitor://localhost", refused.Headers.GetValues("Access-Control-Allow-Origin").Single());

        // A browser sign-in gets no token; the app asks for one.
        var web = await (await client.PostAsJsonAsync("/api/login", new { code = "blue-heron-42" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(web.TryGetProperty("token", out _));
        var login = FromApp(HttpMethod.Post, "/api/login", "https://localhost");
        login.Content = JsonContent.Create(new { code = "blue-heron-42", app = true });
        var signedIn = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var token = (await signedIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        var info = FromApp(HttpMethod.Get, "/api/info");
        info.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(info)).StatusCode);
        var status = FromApp(HttpMethod.Get, "/api/status");
        status.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.True((await (await client.SendAsync(status)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("signedIn").GetBoolean());
        var wrong = FromApp(HttpMethod.Get, "/api/info");
        wrong.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);

        // Any other website gets no permission to read the answers.
        var other = await client.SendAsync(FromApp(HttpMethod.Get, "/api/status", "https://example.com"));
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
