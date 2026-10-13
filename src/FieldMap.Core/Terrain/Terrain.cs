using System.Globalization;
using FieldMap.Core.Geo;
using Microsoft.Data.Sqlite;

namespace FieldMap.Core.Terrain;

/// <summary>
/// A region's elevation grid (terrain.sqlite): the tiles of one CDEM resolution level that cover the region, stored
/// exactly as published (LZW-compressed 16-bit metres, 512×512) with the grid's position in EPSG:3979.
/// </summary>
public sealed class TerrainGrid : IDisposable
{
    public const string FileName = "terrain.sqlite";
    private const int CacheTiles = 48;

    private readonly SqliteConnection _db;
    private readonly SqliteCommand _get;
    private readonly object _gate = new();
    private readonly Dictionary<(int, int), short[]?> _cache = new();
    private readonly LinkedList<(int, int)> _lru = new();
    private readonly CogImage _layout;

    private TerrainGrid(SqliteConnection db, Dictionary<string, string> meta)
    {
        _db = db;
        double D(string k) => double.Parse(meta[k], CultureInfo.InvariantCulture);
        int I(string k) => int.Parse(meta[k], CultureInfo.InvariantCulture);
        OriginX = D("origin_x");
        OriginY = D("origin_y");
        PixelSize = D("pixel_size");
        Width = I("width");
        Height = I("height");
        NoData = meta.TryGetValue("nodata", out var nd) && double.TryParse(nd, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (short)v : short.MinValue;
        Source = meta.GetValueOrDefault("source", "");
        _layout = new CogImage { TileWidth = I("tile_size"), TileHeight = I("tile_size"), Compression = I("compression"), BitsPerSample = 16, SampleFormat = 2, Width = Width, Height = Height };
        _get = db.CreateCommand();
        _get.CommandText = "SELECT data FROM tiles WHERE col=$c AND row=$r";
        _get.Parameters.Add(new SqliteParameter("$c", 0));
        _get.Parameters.Add(new SqliteParameter("$r", 0));
    }

    public double OriginX { get; }
    public double OriginY { get; }
    public double PixelSize { get; }
    public int Width { get; }
    public int Height { get; }
    public short NoData { get; }
    public string Source { get; }
    public int TileSize => _layout.TileWidth;

    public static TerrainGrid Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        var meta = new Dictionary<string, string>();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT key, value FROM meta";
            using var r = c.ExecuteReader();
            while (r.Read())
                meta[r.GetString(0)] = r.GetString(1);
        }
        return new TerrainGrid(db, meta);
    }

    /// <summary>Elevation in metres of grid cell (px, py), or null outside the data (ocean, beyond the region).</summary>
    public short? Cell(int px, int py)
    {
        if (px < 0 || py < 0 || px >= Width || py >= Height)
            return null;
        var t = Tile(px / TileSize, py / TileSize);
        if (t == null)
            return null;
        var v = t[(py % TileSize) * TileSize + px % TileSize];
        return v == NoData || v < -500 ? null : v;
    }

    /// <summary>Grid position (fractional cells) of a projected point.</summary>
    public (double Px, double Py) ToGrid(double x, double y) => ((x - OriginX) / PixelSize - 0.5, (OriginY - y) / PixelSize - 0.5);

    /// <summary>Bilinear elevation (metres) at projected EPSG:3979 coordinates.</summary>
    public double? ElevationAtProjected(double x, double y)
    {
        var (gx, gy) = ToGrid(x, y);
        int x0 = (int)Math.Floor(gx), y0 = (int)Math.Floor(gy);
        double fx = gx - x0, fy = gy - y0;
        double sum = 0, weight = 0;
        void Add(int cx, int cy, double w)
        {
            if (w <= 0)
                return;
            if (Cell(cx, cy) is { } h)
            {
                sum += h * w;
                weight += w;
            }
        }
        Add(x0, y0, (1 - fx) * (1 - fy));
        Add(x0 + 1, y0, fx * (1 - fy));
        Add(x0, y0 + 1, (1 - fx) * fy);
        Add(x0 + 1, y0 + 1, fx * fy);
        return weight > 0.25 ? sum / weight : null;
    }

    /// <summary>Bilinear elevation (metres above sea level) at a GPS position.</summary>
    public double? Elevation(double lat, double lon)
    {
        var (x, y) = CanadaAtlasLambert.Forward(lat, lon);
        return ElevationAtProjected(x, y);
    }

    public double? Elevation(GeoPoint p) => Elevation(p.Lat, p.Lon);

    private short[]? Tile(int col, int row)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue((col, row), out var cached))
            {
                _lru.Remove((col, row));
                _lru.AddFirst((col, row));
                return cached;
            }
            _get.Parameters["$c"].Value = col;
            _get.Parameters["$r"].Value = row;
            var blob = _get.ExecuteScalar() as byte[];
            var tile = blob == null ? null : CogFile.DecodeTile(_layout, blob);
            _cache[(col, row)] = tile;
            _lru.AddFirst((col, row));
            if (_lru.Count > CacheTiles)
            {
                _cache.Remove(_lru.Last!.Value);
                _lru.RemoveLast();
            }
            return tile;
        }
    }

    /// <summary>Writes an uncompressed grid (for tests and tools): elevation(col, row) in metres, null for no data.</summary>
    internal static void Write(string path, double originX, double originY, double pixelSize, int width, int height, Func<int, int, short?> elevation, int tileSize = 256)
    {
        if (File.Exists(path))
            File.Delete(path);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var c = db.CreateCommand();
            c.CommandText = "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT); CREATE TABLE tiles (col INTEGER, row INTEGER, data BLOB, PRIMARY KEY (col, row));";
            c.ExecuteNonQuery();
            using var tx = db.BeginTransaction();
            c.Transaction = tx;
            const short noData = -32767;
            for (var tr = 0; tr * tileSize < height; tr++)
                for (var tc = 0; tc * tileSize < width; tc++)
                {
                    var data = new byte[tileSize * tileSize * 2];
                    for (var y = 0; y < tileSize; y++)
                        for (var x = 0; x < tileSize; x++)
                        {
                            int col = tc * tileSize + x, row = tr * tileSize + y;
                            var v = col < width && row < height ? elevation(col, row) ?? noData : noData;
                            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan((y * tileSize + x) * 2), v);
                        }
                    c.CommandText = "INSERT INTO tiles VALUES ($c, $r, $d)";
                    c.Parameters.Clear();
                    c.Parameters.AddWithValue("$c", tc);
                    c.Parameters.AddWithValue("$r", tr);
                    c.Parameters.AddWithValue("$d", data);
                    c.ExecuteNonQuery();
                }
            foreach (var (k, v) in new Dictionary<string, string>
                     {
                         ["epsg"] = "3979", ["origin_x"] = originX.ToString("R", CultureInfo.InvariantCulture), ["origin_y"] = originY.ToString("R", CultureInfo.InvariantCulture),
                         ["pixel_size"] = pixelSize.ToString("R", CultureInfo.InvariantCulture), ["width"] = width.ToString(CultureInfo.InvariantCulture),
                         ["height"] = height.ToString(CultureInfo.InvariantCulture), ["tile_size"] = tileSize.ToString(CultureInfo.InvariantCulture),
                         ["compression"] = "1", ["nodata"] = noData.ToString(CultureInfo.InvariantCulture), ["source"] = "test"
                     })
            {
                c.CommandText = "INSERT INTO meta VALUES ($k, $v)";
                c.Parameters.Clear();
                c.Parameters.AddWithValue("$k", k);
                c.Parameters.AddWithValue("$v", v);
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        _get.Dispose();
        _db.Dispose();
    }
}

/// <summary>Copies the part of NRCan's national elevation model that covers a region into a <see cref="TerrainGrid"/>.</summary>
public static class TerrainExtractor
{
    /// <summary>
    /// Canadian Digital Elevation Model, national cloud-optimised GeoTIFF (NRCan datacube; Open Government Licence – Canada).
    /// </summary>
    public static readonly Uri CdemUrl = new("https://datacube-prod-data-public.s3.ca-central-1.amazonaws.com/store/elevation/cdem-cdsm/cdem/cdem-canada-dem.tif");

    public const string Attribution = "Elevation: Canadian Digital Elevation Model, Natural Resources Canada (Open Government Licence – Canada)";

    /// <summary>Projected bounding box of a lat/lon box (the Lambert projection bends its edges, so they are sampled).</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) ProjectBounds(GeoBounds b, double marginMetres)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var i = 0; i <= 50; i++)
        {
            var t = i / 50.0;
            foreach (var (lat, lon) in new[]
                     {
                         (b.MinLat, b.MinLon + (b.MaxLon - b.MinLon) * t), (b.MaxLat, b.MinLon + (b.MaxLon - b.MinLon) * t),
                         (b.MinLat + (b.MaxLat - b.MinLat) * t, b.MinLon), (b.MinLat + (b.MaxLat - b.MinLat) * t, b.MaxLon)
                     })
            {
                var (x, y) = CanadaAtlasLambert.Forward(lat, lon);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        return (minX - marginMetres, minY - marginMetres, maxX + marginMetres, maxY + marginMetres);
    }

    /// <summary>
    /// Writes the tiles of the level nearest <paramref name="pixelMetres"/> that cover <paramref name="bounds"/> to
    /// <paramref name="outputPath"/>. Neighbouring tiles are fetched together in requests of up to 16 MB.
    /// </summary>
    public static async Task<long> ExtractAsync(IRangeSource source, GeoBounds bounds, double pixelMetres, string outputPath,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var cog = await CogFile.OpenAsync(source, cancel);
        if (cog.Epsg != 0 && cog.Epsg != 3979)
            throw new InvalidDataException($"The elevation file uses EPSG:{cog.Epsg}; EPSG:3979 was expected.");
        var level = 0;
        for (var i = 0; i < cog.Images.Count; i++)
            if (cog.PixelSizeOf(i) <= pixelMetres * 1.01)
                level = i;
        var im = cog.Images[level];
        var px = cog.PixelSizeOf(level);
        var (minX, minY, maxX, maxY) = ProjectBounds(bounds, 3000);
        int c0 = Math.Max(0, (int)((minX - cog.OriginX) / px / im.TileWidth)), c1 = Math.Min(im.TilesAcross - 1, (int)((maxX - cog.OriginX) / px / im.TileWidth));
        int r0 = Math.Max(0, (int)((cog.OriginY - maxY) / px / im.TileHeight)), r1 = Math.Min(im.TilesDown - 1, (int)((cog.OriginY - minY) / px / im.TileHeight));
        if (c1 < c0 || r1 < r0)
            throw new InvalidDataException("The region is outside the elevation model.");
        var (offsets, counts) = await cog.TileIndexAsync(level, cancel);

        var temp = outputPath + ".partial";
        if (File.Exists(temp))
            File.Delete(temp);
        long bytes = 0;
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            db.Open();
            using (var c = db.CreateCommand())
            {
                c.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT); " +
                                "CREATE TABLE tiles (col INTEGER, row INTEGER, data BLOB, PRIMARY KEY (col, row));";
                c.ExecuteNonQuery();
            }
            using var tx = db.BeginTransaction();
            using var insert = db.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO tiles VALUES ($c, $r, $d)";
            insert.Parameters.Add(new SqliteParameter("$c", 0));
            insert.Parameters.Add(new SqliteParameter("$r", 0));
            insert.Parameters.Add(new SqliteParameter("$d", Array.Empty<byte>()));

            // Requests: runs of tiles that sit next to each other in the file.
            var wanted = new List<(int Col, int Row, long Offset, long Count)>();
            for (var r = r0; r <= r1; r++)
                for (var c = c0; c <= c1; c++)
                {
                    var i = r * im.TilesAcross + c;
                    if (counts[i] > 0)
                        wanted.Add((c, r, offsets[i], counts[i]));
                }
            wanted.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            var done = 0;
            for (var i = 0; i < wanted.Count;)
            {
                var start = wanted[i].Offset;
                var end = start + wanted[i].Count;
                var j = i + 1;
                while (j < wanted.Count && wanted[j].Offset - end < 256 * 1024 && wanted[j].Offset + wanted[j].Count - start <= 16 << 20)
                {
                    end = Math.Max(end, wanted[j].Offset + wanted[j].Count);
                    j++;
                }
                var chunk = await cog.ReadAsync(start, (int)(end - start), cancel);
                for (var k = i; k < j; k++)
                {
                    var t = wanted[k];
                    var data = new byte[t.Count];
                    Array.Copy(chunk, t.Offset - start, data, 0, t.Count);
                    insert.Parameters["$c"].Value = t.Col;
                    insert.Parameters["$r"].Value = t.Row;
                    insert.Parameters["$d"].Value = data;
                    insert.ExecuteNonQuery();
                    bytes += t.Count;
                }
                done += j - i;
                progress?.Report((double)done / wanted.Count);
                i = j;
            }

            using var meta = db.CreateCommand();
            meta.Transaction = tx;
            meta.CommandText = "INSERT INTO meta VALUES ($k, $v)";
            meta.Parameters.Add(new SqliteParameter("$k", ""));
            meta.Parameters.Add(new SqliteParameter("$v", ""));
            foreach (var (k, v) in new Dictionary<string, string>
                     {
                         ["epsg"] = "3979",
                         ["origin_x"] = cog.OriginX.ToString("R", CultureInfo.InvariantCulture),
                         ["origin_y"] = cog.OriginY.ToString("R", CultureInfo.InvariantCulture),
                         ["pixel_size"] = px.ToString("R", CultureInfo.InvariantCulture),
                         ["width"] = im.Width.ToString(CultureInfo.InvariantCulture),
                         ["height"] = im.Height.ToString(CultureInfo.InvariantCulture),
                         ["tile_size"] = im.TileWidth.ToString(CultureInfo.InvariantCulture),
                         ["compression"] = im.Compression.ToString(CultureInfo.InvariantCulture),
                         ["nodata"] = (cog.NoData ?? short.MinValue).ToString(CultureInfo.InvariantCulture),
                         ["source"] = Attribution,
                         ["built"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                     })
            {
                meta.Parameters["$k"].Value = k;
                meta.Parameters["$v"].Value = v;
                meta.ExecuteNonQuery();
            }
            tx.Commit();
        }
        SqliteConnection.ClearAllPools();
        File.Move(temp, outputPath, overwrite: true);
        return bytes;
    }
}
