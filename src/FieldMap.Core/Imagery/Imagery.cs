using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using FieldMap.Core.Geo;
using Microsoft.Data.Sqlite;

namespace FieldMap.Core.Imagery;

/// <summary>
/// An aerial/satellite imagery source: an online tile service (XYZ template or WMS) or an installed MBTiles package.
/// Online sources are listed in Content/imagery-sources.json and stay off until an administrator enables one whose
/// terms allow the intended use.
/// </summary>
public sealed class ImagerySource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>"xyz" (URL with {z}/{x}/{y}), "wms" (WMS 1.1.1 GetMap in EPSG:3857) or "mbtiles" (installed file).</summary>
    public string Kind { get; set; } = "xyz";

    public string Url { get; set; } = "";

    /// <summary>WMS layer names (comma-separated).</summary>
    public string Layers { get; set; } = "";

    public string Format { get; set; } = "image/jpeg";
    public int MinZoom { get; set; } = 0;
    public int MaxZoom { get; set; } = 18;
    public string Attribution { get; set; } = "";
    public string Licence { get; set; } = "";

    /// <summary>Terms shown before first use (and on the About screen).</summary>
    public string Terms { get; set; } = "";

    /// <summary>Whether the terms allow keeping tiles for offline use (enables "Save this area").</summary>
    public bool AllowOffline { get; set; }

    public bool Enabled { get; set; }

    /// <summary>Needs a key or token, substituted for {key} in the URL (entered in Settings).</summary>
    public bool NeedsKey { get; set; }

    public string Verified { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>Area covered (minLon, minLat, maxLon, maxLat); empty = everywhere.</summary>
    public double[]? Bounds { get; set; }

    /// <summary>For installed packages: the file.</summary>
    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public bool IsOnline => Kind is "xyz" or "wms";

    public bool Covers(int z, int x, int y)
    {
        if (z < MinZoom || z > MaxZoom)
            return false;
        if (Bounds is not { Length: 4 } b)
            return true;
        var t = ImageryMath.TileBounds(z, x, y);
        return !(t.MaxLon < b[0] || t.MinLon > b[2] || t.MaxLat < b[1] || t.MinLat > b[3]);
    }

    /// <summary>The request URL for a tile.</summary>
    public string TileUrl(int z, int x, int y, string key = "")
    {
        if (Kind == "wms")
        {
            var (minX, minY, maxX, maxY) = ImageryMath.TileMercatorBounds(z, x, y);
            var sep = Url.Contains('?') ? "&" : "?";
            return Url + sep + string.Format(CultureInfo.InvariantCulture,
                "SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&LAYERS={0}&STYLES=&SRS=EPSG:3857&BBOX={1:0.###},{2:0.###},{3:0.###},{4:0.###}&WIDTH=256&HEIGHT=256&FORMAT={5}&TRANSPARENT=FALSE",
                Uri.EscapeDataString(Layers), minX, minY, maxX, maxY, Uri.EscapeDataString(Format));
        }
        return Url.Replace("{z}", z.ToString(CultureInfo.InvariantCulture))
                  .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
                  .Replace("{y}", y.ToString(CultureInfo.InvariantCulture))
                  .Replace("{-y}", ((1 << z) - 1 - y).ToString(CultureInfo.InvariantCulture))
                  .Replace("{key}", Uri.EscapeDataString(key));
    }
}

public sealed class ImageryCatalog
{
    public List<ImagerySource> Sources { get; set; } = new();

    public static ImageryCatalog LoadOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? Json.Deserialize<ImageryCatalog>(File.ReadAllText(path)) : new ImageryCatalog();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new ImageryCatalog();
        }
    }
}

public static class ImageryMath
{
    public static GeoBounds TileBounds(int z, int x, int y)
    {
        var nw = WebMercator.FromWorld((double)x / (1 << z), (double)y / (1 << z));
        var se = WebMercator.FromWorld((double)(x + 1) / (1 << z), (double)(y + 1) / (1 << z));
        return new GeoBounds(se.Lat, nw.Lon, nw.Lat, se.Lon);
    }

    /// <summary>Tile extent in EPSG:3857 metres.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) TileMercatorBounds(int z, int x, int y)
    {
        const double half = 20037508.342789244;
        var size = 2 * half / (1 << z);
        var minX = -half + x * size;
        var maxY = half - y * size;
        return (minX, maxY - size, minX + size, maxY);
    }

    /// <summary>Tiles covering an area at a zoom.</summary>
    public static IEnumerable<(int Z, int X, int Y)> Tiles(GeoBounds b, int z)
    {
        var n = 1 << z;
        var (x0, y0) = WebMercator.ToWorld(b.MaxLat, b.MinLon);
        var (x1, y1) = WebMercator.ToWorld(b.MinLat, b.MaxLon);
        int tx0 = Math.Clamp((int)(x0 * n), 0, n - 1), tx1 = Math.Clamp((int)(x1 * n), 0, n - 1);
        int ty0 = Math.Clamp((int)(y0 * n), 0, n - 1), ty1 = Math.Clamp((int)(y1 * n), 0, n - 1);
        for (var x = tx0; x <= tx1; x++)
            for (var y = ty0; y <= ty1; y++)
                yield return (z, x, y);
    }

    public static long CountTiles(GeoBounds b, int minZoom, int maxZoom)
    {
        long total = 0;
        for (var z = minZoom; z <= maxZoom; z++)
        {
            var n = 1 << z;
            var (x0, y0) = WebMercator.ToWorld(b.MaxLat, b.MinLon);
            var (x1, y1) = WebMercator.ToWorld(b.MinLat, b.MaxLon);
            long w = Math.Clamp((int)(x1 * n), 0, n - 1) - Math.Clamp((int)(x0 * n), 0, n - 1) + 1;
            long h = Math.Clamp((int)(y1 * n), 0, n - 1) - Math.Clamp((int)(y0 * n), 0, n - 1) + 1;
            total += w * h;
        }
        return total;
    }

    /// <summary>True for PNG or JPEG data (servers return HTML or XML error pages with status 200).</summary>
    public static bool IsImage(byte[] d) =>
        d.Length > 8 && ((d[0] == 0xFF && d[1] == 0xD8) || (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47));
}

/// <summary>
/// A read/write MBTiles file of raster tiles (an imagery package or the online cache). Thread-safe. Tiles are added
/// as they arrive, so a seeding run can stop and resume.
/// </summary>
public sealed class RasterTileStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public RasterTileStore(string path, bool readOnly = false)
    {
        Path = path;
        if (!readOnly)
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        _db.Open();
        if (!readOnly)
        {
            Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            Exec("CREATE TABLE IF NOT EXISTS metadata (name TEXT PRIMARY KEY, value TEXT);");
            Exec("CREATE TABLE IF NOT EXISTS tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB);");
            Exec("CREATE UNIQUE INDEX IF NOT EXISTS tile_index ON tiles (zoom_level, tile_column, tile_row);");
        }
        ReadOnly = readOnly;
    }

    public string Path { get; }
    public bool ReadOnly { get; }

    private void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public byte[]? Get(int z, int x, int y)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT tile_data FROM tiles WHERE zoom_level=$z AND tile_column=$x AND tile_row=$y";
            c.Parameters.AddWithValue("$z", z);
            c.Parameters.AddWithValue("$x", x);
            c.Parameters.AddWithValue("$y", (1 << z) - 1 - y);
            return c.ExecuteScalar() as byte[];
        }
    }

    public bool Has(int z, int x, int y)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT 1 FROM tiles WHERE zoom_level=$z AND tile_column=$x AND tile_row=$y";
            c.Parameters.AddWithValue("$z", z);
            c.Parameters.AddWithValue("$x", x);
            c.Parameters.AddWithValue("$y", (1 << z) - 1 - y);
            return c.ExecuteScalar() != null;
        }
    }

    public void Put(int z, int x, int y, byte[] data)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT OR REPLACE INTO tiles VALUES ($z, $x, $y, $d)";
            c.Parameters.AddWithValue("$z", z);
            c.Parameters.AddWithValue("$x", x);
            c.Parameters.AddWithValue("$y", (1 << z) - 1 - y);
            c.Parameters.AddWithValue("$d", data);
            c.ExecuteNonQuery();
        }
    }

    public void SetMetadata(IDictionary<string, string> values)
    {
        lock (_lock)
            foreach (var (k, v) in values)
            {
                using var c = _db.CreateCommand();
                c.CommandText = "INSERT OR REPLACE INTO metadata VALUES ($n, $v)";
                c.Parameters.AddWithValue("$n", k);
                c.Parameters.AddWithValue("$v", v);
                c.ExecuteNonQuery();
            }
    }

    public Dictionary<string, string> Metadata()
    {
        lock (_lock)
        {
            var d = new Dictionary<string, string>();
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT name, value FROM metadata";
            using var r = c.ExecuteReader();
            while (r.Read())
                d[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            return d;
        }
    }

    public long Count()
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM tiles";
            return (long)c.ExecuteScalar()!;
        }
    }

    public (int Min, int Max)? ZoomRange()
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT MIN(zoom_level), MAX(zoom_level) FROM tiles";
            using var r = c.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0))
                return null;
            return (r.GetInt32(0), r.GetInt32(1));
        }
    }

    /// <summary>Removes the oldest-added tiles until at most <paramref name="keep"/> remain (for the online cache).</summary>
    public int TrimTo(long keep)
    {
        lock (_lock)
        {
            using var count = _db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM tiles";
            var n = (long)count.ExecuteScalar()!;
            if (n <= keep)
                return 0;
            using var c = _db.CreateCommand();
            c.CommandText = "DELETE FROM tiles WHERE rowid IN (SELECT rowid FROM tiles ORDER BY rowid LIMIT $n)";
            c.Parameters.AddWithValue("$n", n - keep);
            return c.ExecuteNonQuery();
        }
    }

    /// <summary>Builds an installed-package source description from the file's metadata.</summary>
    public static ImagerySource Describe(string path)
    {
        using var s = new RasterTileStore(path, readOnly: true);
        var m = s.Metadata();
        var zoom = s.ZoomRange();
        double[]? bounds = null;
        if (m.TryGetValue("bounds", out var b))
        {
            var parts = b.Split(',').Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
            if (parts.Length == 4 && parts.All(double.IsFinite))
                bounds = parts;
        }
        return new ImagerySource
        {
            Id = "file:" + System.IO.Path.GetFileNameWithoutExtension(path),
            Name = (m.GetValueOrDefault("name") is { Length: > 0 } n ? n : System.IO.Path.GetFileNameWithoutExtension(path))
                   + (path.EndsWith("-saved.mbtiles", StringComparison.OrdinalIgnoreCase) ? " (saved areas)" : ""),
            Kind = "mbtiles",
            FilePath = path,
            MinZoom = zoom?.Min ?? 0,
            MaxZoom = zoom?.Max ?? 0,
            Attribution = m.GetValueOrDefault("attribution") ?? "",
            Licence = m.GetValueOrDefault("licence") ?? m.GetValueOrDefault("license") ?? "",
            Notes = m.GetValueOrDefault("description") ?? "",
            Bounds = bounds,
            AllowOffline = true,
            Enabled = true
        };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!ReadOnly)
            {
                // Leave a single self-contained file (no -wal/-shm) so packages can be copied to USB or installed.
                try
                {
                    Exec("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
                }
                catch (SqliteException)
                {
                    // Another reader has it open; SQLite checkpoints when the last connection closes.
                }
            }
            _db.Close();
            _db.Dispose();
        }
    }
}

public sealed record SeedProgress(long Done, long Total, long Fetched, long Skipped, long Failed);

/// <summary>Downloads imagery tiles from an online source (polite: few requests at once, identifies itself).</summary>
public sealed class ImageryFetcher
{
    private readonly HttpClient _http;

    public ImageryFetcher(HttpClient http, string userAgent)
    {
        _http = http;
        UserAgent = userAgent;
    }

    public string UserAgent { get; }
    public int Concurrency { get; set; } = 2;

    /// <summary>Fetches one tile; null when the server has none or sent something that isn't an image.</summary>
    public async Task<byte[]?> FetchAsync(ImagerySource source, int z, int x, int y, string key, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, source.TileUrl(z, x, y, key));
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var resp = await _http.SendAsync(req, cancel).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.NoContent)
            return null;
        resp.EnsureSuccessStatusCode();
        var data = await resp.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
        return ImageryMath.IsImage(data) ? data : null;
    }

    /// <summary>
    /// Saves every tile of an area and zoom range into <paramref name="store"/>, skipping tiles already there (so it can
    /// resume). Refuses sources whose terms don't allow offline use.
    /// </summary>
    public async Task<SeedProgress> SeedAsync(ImagerySource source, GeoBounds area, int minZoom, int maxZoom, RasterTileStore store, string key,
        IProgress<SeedProgress>? progress, CancellationToken cancel, long maxTiles = 200_000)
    {
        if (!source.AllowOffline)
            throw new InvalidOperationException($"The terms of {source.Name} don't allow saving tiles for offline use.");
        minZoom = Math.Max(minZoom, source.MinZoom);
        maxZoom = Math.Min(maxZoom, source.MaxZoom);
        var total = ImageryMath.CountTiles(area, minZoom, maxZoom);
        if (total > maxTiles)
            throw new InvalidOperationException($"{total:N0} tiles is more than the limit of {maxTiles:N0}. Choose a smaller area or fewer zoom levels.");
        long done = 0, fetched = 0, skipped = 0, failed = 0;
        var tiles = Enumerable.Range(minZoom, maxZoom - minZoom + 1).SelectMany(z => ImageryMath.Tiles(area, z)).Where(t => source.Covers(t.Z, t.X, t.Y));
        await Parallel.ForEachAsync(tiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Concurrency), CancellationToken = cancel }, async (t, ct) =>
        {
            if (store.Has(t.Z, t.X, t.Y))
                Interlocked.Increment(ref skipped);
            else
            {
                try
                {
                    var data = await FetchAsync(source, t.Z, t.X, t.Y, key, ct).ConfigureAwait(false);
                    if (data != null)
                    {
                        store.Put(t.Z, t.X, t.Y, data);
                        Interlocked.Increment(ref fetched);
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                    }
                }
                catch (HttpRequestException)
                {
                    Interlocked.Increment(ref failed);
                }
            }
            var d = Interlocked.Increment(ref done);
            if (d % 25 == 0 || d == total)
                progress?.Report(new SeedProgress(d, total, Interlocked.Read(ref fetched), Interlocked.Read(ref skipped), Interlocked.Read(ref failed)));
        }).ConfigureAwait(false);
        store.SetMetadata(new Dictionary<string, string>
        {
            ["name"] = source.Name,
            ["format"] = source.Format.Contains("png") ? "png" : "jpg",
            ["type"] = "baselayer",
            ["attribution"] = source.Attribution,
            ["licence"] = source.Licence,
            ["source"] = source.Id,
            ["bounds"] = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", area.MinLon, area.MinLat, area.MaxLon, area.MaxLat),
            ["minzoom"] = minZoom.ToString(CultureInfo.InvariantCulture),
            ["maxzoom"] = maxZoom.ToString(CultureInfo.InvariantCulture),
            ["saved"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        });
        var result = new SeedProgress(done, total, fetched, skipped, failed);
        progress?.Report(result);
        return result;
    }
}
