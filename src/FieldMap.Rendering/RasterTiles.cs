using SkiaSharp;

namespace FieldMap.Map;

/// <summary>How the base map is drawn.</summary>
public enum BaseMapMode
{
    /// <summary>The vector map.</summary>
    Map,

    /// <summary>Aerial/satellite imagery with facilities, drawings and units on top, no road lines or labels.</summary>
    Imagery,

    /// <summary>Imagery with roads, names and labels on top.</summary>
    Hybrid
}

/// <summary>
/// Decoded imagery tiles (Skia images), kept in a small LRU. Tiles are read synchronously from local stores through
/// <c>read</c>; a tile that isn't there is passed to <c>requestMissing</c> (e.g. to fetch it online), which calls
/// <see cref="Arrived"/> when it's stored.
/// </summary>
public sealed class RasterTileCache : IDisposable
{
    private readonly Func<int, int, int, byte[]?> _read;
    private readonly Action<int, int, int>? _requestMissing;
    private readonly Dictionary<(int, int, int), LinkedListNode<((int, int, int) Key, SKImage? Image)>> _map = new();
    private readonly LinkedList<((int, int, int) Key, SKImage? Image)> _lru = new();
    private readonly HashSet<(int, int, int)> _requested = new();
    private readonly object _lock = new();

    public RasterTileCache(int minZoom, int maxZoom, Func<int, int, int, byte[]?> read, Action<int, int, int>? requestMissing = null, int capacity = 300)
    {
        MinZoom = minZoom;
        MaxZoom = maxZoom;
        _read = read;
        _requestMissing = requestMissing;
        Capacity = capacity;
    }

    public int MinZoom { get; }
    public int MaxZoom { get; }
    public int Capacity { get; }

    /// <summary>A requested tile arrived (redraw).</summary>
    public event EventHandler? TileArrived;

    /// <summary>The image for a tile, or null. With <paramref name="request"/>, a missing tile is asked for once.</summary>
    public SKImage? Get(int z, int x, int y, bool request)
    {
        var key = (z, x, y);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                if (node.Value.Image != null || !request || _requested.Contains(key))
                    return node.Value.Image;
            }
        }
        var bytes = _read(z, x, y);
        SKImage? image = null;
        if (bytes != null)
        {
            using var data = SKData.CreateCopy(bytes);
            image = SKImage.FromEncodedData(data);
        }
        lock (_lock)
        {
            Store(key, image);
            if (image == null && request && _requestMissing != null && _requested.Add(key))
                _requestMissing(z, x, y);
        }
        return image;
    }

    private void Store((int, int, int) key, SKImage? image)
    {
        if (_map.TryGetValue(key, out var old))
        {
            _lru.Remove(old);
            old.Value.Image?.Dispose();
        }
        var node = _lru.AddFirst((key, image));
        _map[key] = node;
        while (_lru.Count > Capacity)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
            last.Value.Image?.Dispose();
        }
    }

    /// <summary>A missing tile is now available (or the fetch gave up): forget the empty entry and redraw.</summary>
    public void Arrived(int z, int x, int y, bool success)
    {
        lock (_lock)
        {
            if (success && _map.TryGetValue((z, x, y), out var node))
            {
                _lru.Remove(node);
                _map.Remove((z, x, y));
                node.Value.Image?.Dispose();
            }
            if (success)
                _requested.Remove((z, x, y));
        }
        if (success)
            TileArrived?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forget failed requests so they're tried again (e.g. back online).</summary>
    public void RetryMissing()
    {
        lock (_lock)
        {
            foreach (var key in _requested)
                if (_map.TryGetValue(key, out var node) && node.Value.Image == null)
                {
                    _lru.Remove(node);
                    _map.Remove(key);
                }
            _requested.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (_, image) in _lru)
                image?.Dispose();
            _lru.Clear();
            _map.Clear();
        }
    }
}
