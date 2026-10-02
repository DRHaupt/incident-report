using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FieldMap.Core.Tiles;

/// <summary>Writes an MBTiles 1.3 file of gzip'd vector tiles.</summary>
public sealed class MbTilesWriter : IDisposable
{
    private readonly SqliteConnection _db;
    private SqliteTransaction _tx;
    private readonly SqliteCommand _insert;
    private int _pending;

    public MbTilesWriter(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA page_size=4096;");
        Exec("CREATE TABLE metadata (name TEXT, value TEXT);");
        Exec("CREATE TABLE tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB);");
        _tx = _db.BeginTransaction();
        _insert = _db.CreateCommand();
        _insert.Transaction = _tx;
        _insert.CommandText = "INSERT INTO tiles VALUES ($z, $x, $y, $d)";
        _insert.Parameters.Add("$z", SqliteType.Integer);
        _insert.Parameters.Add("$x", SqliteType.Integer);
        _insert.Parameters.Add("$y", SqliteType.Integer);
        _insert.Parameters.Add("$d", SqliteType.Blob);
    }

    public int TileCount { get; private set; }

    private void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    /// <summary>Adds a tile in XYZ numbering (y = 0 at the top); stored in TMS numbering as MBTiles requires.</summary>
    public void Add(int z, int x, int y, byte[] gzippedTile)
    {
        _insert.Parameters["$z"].Value = z;
        _insert.Parameters["$x"].Value = x;
        _insert.Parameters["$y"].Value = (1 << z) - 1 - y;
        _insert.Parameters["$d"].Value = gzippedTile;
        _insert.ExecuteNonQuery();
        TileCount++;
        if (++_pending >= 5000)
        {
            _tx.Commit();
            _tx = _db.BeginTransaction();
            _insert.Transaction = _tx;
            _pending = 0;
        }
    }

    public void SetMetadata(IDictionary<string, string> values)
    {
        foreach (var (k, v) in values)
        {
            using var c = _db.CreateCommand();
            c.Transaction = _tx;
            c.CommandText = "INSERT INTO metadata VALUES ($n, $v)";
            c.Parameters.AddWithValue("$n", k);
            c.Parameters.AddWithValue("$v", v);
            c.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _tx.Commit();
        Exec("CREATE UNIQUE INDEX tile_index ON tiles (zoom_level, tile_column, tile_row);");
        _insert.Dispose();
        _db.Close();
        _db.Dispose();
    }
}

/// <summary>Reads tiles from an MBTiles file (thread-safe; one connection per reader, serialised).</summary>
public sealed class MbTilesReader : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly SqliteCommand _select;
    private readonly object _lock = new();

    public MbTilesReader(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Tile file not found.", path);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        _db.Open();
        _select = _db.CreateCommand();
        _select.CommandText = "SELECT tile_data FROM tiles WHERE zoom_level=$z AND tile_column=$x AND tile_row=$y";
        _select.Parameters.Add("$z", SqliteType.Integer);
        _select.Parameters.Add("$x", SqliteType.Integer);
        _select.Parameters.Add("$y", SqliteType.Integer);
        var meta = new Dictionary<string, string>();
        Metadata = meta;
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT name, value FROM metadata";
        using var r = c.ExecuteReader();
        while (r.Read())
            meta[r.GetString(0)] = r.GetString(1);
        MinZoom = Metadata.TryGetValue("minzoom", out var mn) ? int.Parse(mn, CultureInfo.InvariantCulture) : 0;
        MaxZoom = Metadata.TryGetValue("maxzoom", out var mx) ? int.Parse(mx, CultureInfo.InvariantCulture) : 14;
    }

    public IReadOnlyDictionary<string, string> Metadata { get; }
    public int MinZoom { get; }
    public int MaxZoom { get; }

    /// <summary>Raw (gzip'd) tile bytes for an XYZ tile, or null when there is none.</summary>
    public byte[]? GetRaw(int z, int x, int y)
    {
        lock (_lock)
        {
            _select.Parameters["$z"].Value = z;
            _select.Parameters["$x"].Value = x;
            _select.Parameters["$y"].Value = (1 << z) - 1 - y;
            return _select.ExecuteScalar() as byte[];
        }
    }

    public VectorTile? Get(int z, int x, int y) => GetRaw(z, x, y) is { } raw ? VectorTile.Decode(raw) : null;

    public long CountTiles()
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM tiles";
            return (long)c.ExecuteScalar()!;
        }
    }

    public void Dispose()
    {
        _select.Dispose();
        _db.Close();
        _db.Dispose();
    }
}
