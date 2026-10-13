using FieldMap.Core.Build;
using FieldMap.Core.Tiles;
using SkiaSharp;

namespace FieldMap.Map;

public sealed class LineLabel
{
    public required string Text { get; init; }
    public bool IsRef { get; init; }
    public required string Class { get; init; }
    public required SKPoint[] Points { get; init; }
}

public sealed class PointLabel
{
    public required string Name { get; init; }
    public int Rank { get; init; }
    public long Population { get; init; }
    public SKPoint Position { get; init; }
}

/// <summary>A decoded tile turned into ready-to-draw Skia paths (in tile units, 0–4096), grouped by style.</summary>
public sealed class PreparedTile : IDisposable
{
    public int Z { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public Dictionary<string, SKPath> Fills { get; } = new();
    public Dictionary<string, SKPath> Lines { get; } = new();
    public List<LineLabel> RoadLabels { get; } = new();
    public List<PointLabel> Places { get; } = new();

    public void Dispose()
    {
        foreach (var p in Fills.Values) p.Dispose();
        foreach (var p in Lines.Values) p.Dispose();
    }

    private static SKPath Get(Dictionary<string, SKPath> d, string key)
    {
        if (!d.TryGetValue(key, out var p))
            d[key] = p = new SKPath { FillType = SKPathFillType.EvenOdd };
        return p;
    }

    public static PreparedTile Prepare(VectorTile tile, int z, int x, int y)
    {
        var t = new PreparedTile { Z = z, X = x, Y = y };
        foreach (var layer in tile.Layers)
        {
            var scale = 4096f / layer.Extent;
            foreach (var f in layer.Features)
            {
                string? fillKey = null, lineKey = null;
                switch (layer.Name)
                {
                    case TileSchema.Water: fillKey = "water"; break;
                    case TileSchema.Landuse: fillKey = "landuse:" + (f.Str("kind") ?? ""); break;
                    case TileSchema.Hazards: fillKey = "hazard"; lineKey = "hazard"; break;
                    case TileSchema.Coverage: fillKey = "coverage:" + (f.Str("kind") ?? "other"); break;
                    case TileSchema.Waterways: lineKey = "waterway:" + (f.Str("kind") ?? "stream"); break;
                    case TileSchema.Boundaries: lineKey = "boundary"; break;
                    case TileSchema.Rail: lineKey = "rail:" + (f.Str("class") ?? "rail"); break;
                    case TileSchema.Roads:
                    {
                        var cls = f.Str("class") ?? "minor";
                        var flags = (f.Flag("tunnel") ? "t" : "") + (f.Flag("bridge") ? "b" : "") + (f.Flag("unpaved") ? "u" : "") + (f.Flag("seasonal") ? "s" : "");
                        lineKey = $"road:{cls}:{flags}";
                        var name = f.Str("name");
                        var rf = f.Str("ref");
                        foreach (var part in f.Parts)
                        {
                            var pts = part.Select(p => new SKPoint((float)p.X * scale, (float)p.Y * scale)).ToArray();
                            if (pts.Length < 2)
                                continue;
                            if (!string.IsNullOrEmpty(rf) && cls is "motorway" or "trunk" or "primary" or "secondary")
                                t.RoadLabels.Add(new LineLabel { Text = rf.Split(';')[0], IsRef = true, Class = cls, Points = pts });
                            if (!string.IsNullOrEmpty(name))
                                t.RoadLabels.Add(new LineLabel { Text = name, Class = cls, Points = pts });
                        }
                        break;
                    }
                    case TileSchema.Places:
                        foreach (var part in f.Parts)
                            t.Places.Add(new PointLabel
                            {
                                Name = f.Str("name") ?? "",
                                Rank = (int)f.Int("rank", 5),
                                Population = f.Int("population"),
                                Position = new SKPoint((float)part[0].X * scale, (float)part[0].Y * scale)
                            });
                        continue;
                }
                if (fillKey != null && f.Type == GeomType.Polygon)
                {
                    var path = Get(t.Fills, fillKey);
                    foreach (var ring in f.Parts)
                        AddPolyline(path, ring, scale, close: true);
                }
                if (lineKey != null)
                {
                    var path = Get(t.Lines, lineKey);
                    foreach (var part in f.Parts)
                        AddPolyline(path, part, scale, close: f.Type == GeomType.Polygon);
                }
            }
        }
        return t;
    }

    private static void AddPolyline(SKPath path, FieldMap.Core.Geo.Vec[] pts, float scale, bool close)
    {
        if (pts.Length < 2)
            return;
        path.MoveTo((float)pts[0].X * scale, (float)pts[0].Y * scale);
        for (var i = 1; i < pts.Length; i++)
            path.LineTo((float)pts[i].X * scale, (float)pts[i].Y * scale);
        if (close)
            path.Close();
    }
}

/// <summary>
/// Decoded tiles shared by all map windows. Tiles are decoded on background threads; inserting and evicting happens on
/// the UI thread (where drawing happens), so a path is never disposed while it is being drawn.
/// </summary>
public sealed class TileCache
{
    public readonly record struct Key(int Region, int Generation, int Z, int X, int Y);

    private readonly Dictionary<Key, (PreparedTile? Tile, LinkedListNode<Key> Node)> _tiles = new();
    private readonly LinkedList<Key> _lru = new();
    private readonly HashSet<Key> _loading = new();
    private readonly SemaphoreSlim _workers = new(Math.Max(2, Environment.ProcessorCount - 1));
    private readonly SynchronizationContext? _ui;

    public TileCache() => _ui = SynchronizationContext.Current;

    public int MaxTiles { get; set; } = 400;

    /// <summary>Bumped when the map data changes; old tiles are dropped.</summary>
    public int Generation { get; private set; }

    public event EventHandler? TileLoaded;

    public void Clear()
    {
        Generation++;
        foreach (var (tile, _) in _tiles.Values)
            tile?.Dispose();
        _tiles.Clear();
        _lru.Clear();
        _loading.Clear();
    }

    /// <summary>The tile if ready; otherwise starts loading it (when <paramref name="load"/>) and returns null.</summary>
    public bool TryGet(int region, MbTilesReader reader, int z, int x, int y, bool load, out PreparedTile? tile)
    {
        var key = new Key(region, Generation, z, x, y);
        if (_tiles.TryGetValue(key, out var entry))
        {
            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
            tile = entry.Tile;
            return true;
        }
        tile = null;
        if (load && _loading.Add(key))
            _ = LoadAsync(key, reader);
        return false;
    }

    /// <summary>Decodes a tile synchronously (snapshot export, tests).</summary>
    public PreparedTile? LoadNow(int region, MbTilesReader reader, int z, int x, int y)
    {
        if (TryGet(region, reader, z, x, y, false, out var t))
            return t;
        var tile = reader.Get(z, x, y) is { } vt ? PreparedTile.Prepare(vt, z, x, y) : null;
        Insert(new Key(region, Generation, z, x, y), tile);
        return tile;
    }

    private async Task LoadAsync(Key key, MbTilesReader reader)
    {
        await _workers.WaitAsync().ConfigureAwait(false);
        PreparedTile? tile = null;
        try
        {
            if (key.Generation == Generation)
                tile = reader.Get(key.Z, key.X, key.Y) is { } vt ? PreparedTile.Prepare(vt, key.Z, key.X, key.Y) : null;
        }
        catch (Exception)
        {
            tile = null; // unreadable tile: drawn as empty
        }
        finally
        {
            _workers.Release();
        }
        void Done(object? _)
        {
            _loading.Remove(key);
            if (key.Generation != Generation)
            {
                tile?.Dispose();
                return;
            }
            Insert(key, tile);
            TileLoaded?.Invoke(this, EventArgs.Empty);
        }
        if (_ui != null)
            _ui.Post(Done, null);
        else
            Done(null);
    }

    private void Insert(Key key, PreparedTile? tile)
    {
        if (_tiles.ContainsKey(key))
        {
            tile?.Dispose();
            return;
        }
        var node = _lru.AddFirst(key);
        _tiles[key] = (tile, node);
        while (_tiles.Count > MaxTiles && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            if (_tiles.Remove(last.Value, out var old))
                old.Tile?.Dispose();
        }
    }
}
