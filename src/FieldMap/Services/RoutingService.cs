using FieldMap.Core.Drawings;
using FieldMap.Core.Routing;

namespace FieldMap.Services;

public sealed class RoutingException : Exception
{
    public RoutingException(string message) : base(message)
    {
    }
}

/// <summary>
/// Runs routing requests on a background thread against the right region's graph, with the user's active road closures
/// applied. One router per graph, used by one request at a time.
/// </summary>
public sealed class RoutingService
{
    private readonly MapDataService _data;
    private readonly Func<IEnumerable<Drawing>> _closures;
    private readonly Dictionary<RoadGraph, (Router Router, SemaphoreSlim Lock)> _routers = new();
    private readonly Dictionary<(RoadGraph, Guid, DateTime), List<int>> _closureCache = new();

    public RoutingService(MapDataService data, Func<IEnumerable<Drawing>> closures)
    {
        _data = data;
        _closures = closures;
        data.Changed += (_, _) =>
        {
            lock (_routers)
                _routers.Clear();
            lock (_closureCache)
                _closureCache.Clear();
        };
    }

    public static VehicleProfile ProfileFromSettings(MapSettings s) => s.Profile switch
    {
        "Emergency" => VehicleProfile.Emergency,
        "Truck" => VehicleProfile.Truck(s.TruckHeight, s.TruckWeight, s.TruckLength),
        "Walking" => VehicleProfile.Walking,
        _ => VehicleProfile.Car
    };

    public async Task<T> RunAsync<T>(GeoPoint a, GeoPoint b, Func<Router, T> work, bool applyClosures = true)
    {
        if (!_data.HasData)
            throw new RoutingException("No map data is installed. Open Data & updates to install a region.");
        var region = _data.RegionFor(a, b);
        if (region == null)
        {
            if (_data.RegionAt(a) != null && _data.RegionAt(b) != null)
                throw new RoutingException("The start and destination are in different regions. Install the combined British Columbia + Alberta package to route across the border.");
            throw new RoutingException("This location is outside the installed map data.");
        }
        var graph = await region.Graph.ConfigureAwait(false) ?? throw new RoutingException("The routing data for this region could not be loaded. Run Data → Validate, or reinstall the region.");
        (Router Router, SemaphoreSlim Lock) entry;
        lock (_routers)
        {
            if (!_routers.TryGetValue(graph, out entry))
                _routers[graph] = entry = (new Router(graph), new SemaphoreSlim(1, 1));
        }
        var closures = applyClosures ? _closures().Where(d => d.AffectsRouting && d.Points.Count > 0).Select(d => d.Clone()).ToList() : new List<Drawing>();
        await entry.Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var router = entry.Router;
                router.Closed.Clear();
                foreach (var d in closures)
                    foreach (var s in ClosedSegments(router, graph, d))
                        router.Closed.Add(s);
                return work(router);
            }).ConfigureAwait(false);
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    private List<int> ClosedSegments(Router router, RoadGraph graph, Drawing d)
    {
        var key = (graph, d.Id, d.ModifiedUtc);
        lock (_closureCache)
            if (_closureCache.TryGetValue(key, out var cached))
                return cached;
        var segs = router.SegmentsTouching(d.Points, d.Shape == DrawingShape.Polygon, d.Points.Count == 1 ? 20 : 12);
        lock (_closureCache)
            _closureCache[key] = segs;
        return segs;
    }

    /// <summary>How many road segments the active closures block (shown in the closure editor).</summary>
    public async Task<int> CountClosedAsync(Drawing d)
    {
        if (d.Points.Count == 0)
            return 0;
        try
        {
            return await RunAsync(d.Points[0], d.Points[0], r => r.SegmentsTouching(d.Points, d.Shape == DrawingShape.Polygon, d.Points.Count == 1 ? 20 : 12).Count, applyClosures: false);
        }
        catch (RoutingException)
        {
            return 0;
        }
    }
}
