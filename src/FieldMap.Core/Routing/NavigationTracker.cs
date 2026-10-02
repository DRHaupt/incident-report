using FieldMap.Core.Geo;

namespace FieldMap.Core.Routing;

/// <summary>
/// Follows the vehicle along a route: distance travelled, the next maneuver and the distance to it, remaining distance and
/// time, when to speak a prompt, and when the vehicle has left the route (so the app re-routes).
/// </summary>
public sealed class NavigationTracker
{
    private double[] _cumulative = Array.Empty<double>();
    private readonly HashSet<(int Step, int Threshold)> _spoken = new();
    private int _offCount;

    public NavigationTracker(Route route) => Replace(route);

    public Route Route { get; private set; } = null!;
    public double Along { get; private set; }
    public double DistanceFromRoute { get; private set; }
    public int NextStepIndex { get; private set; } = 1;
    public double DistanceToNextStep { get; private set; }
    public double RemainingMeters { get; private set; }
    public double RemainingSeconds { get; private set; }
    public bool OffRoute { get; private set; }
    public bool Arrived { get; private set; }

    /// <summary>Metres off the route before counting a fix as off-route (raised for poor GPS accuracy).</summary>
    public double OffRouteMeters { get; set; } = 45;

    public RouteStep? NextStep => NextStepIndex < Route.Steps.Count ? Route.Steps[NextStepIndex] : null;

    public void Replace(Route route)
    {
        Route = route;
        _cumulative = route.CumulativeDistance();
        _spoken.Clear();
        _offCount = 0;
        Along = 0;
        OffRoute = false;
        Arrived = false;
        NextStepIndex = Math.Min(1, route.Steps.Count - 1);
        RemainingMeters = route.DistanceMeters;
        RemainingSeconds = route.DurationSeconds;
        DistanceToNextStep = NextStep?.AlongMeters ?? route.DistanceMeters;
    }

    /// <summary>Updates with a GPS position; returns a prompt to speak, if one is due.</summary>
    public string? Update(GeoPoint position, double? accuracyMeters = null, double? speedMetersPerSecond = null)
    {
        var g = Route.Geometry;
        if (g.Count < 2)
            return null;
        // Nearest point on the route, not looking far behind the progress already made (loops and U-shaped roads).
        var best = (Distance: double.MaxValue, Along: Along);
        for (var i = 1; i < g.Count; i++)
        {
            if (_cumulative[i] < Along - 150)
                continue;
            var (d, t) = Geometry.DistanceToSegment(position, g[i - 1], g[i]);
            if (d < best.Distance)
                best = (d, _cumulative[i - 1] + (_cumulative[i] - _cumulative[i - 1]) * t);
        }
        DistanceFromRoute = best.Distance;
        var threshold = Math.Max(OffRouteMeters, (accuracyMeters ?? 0) * 1.5);
        if (best.Distance > threshold)
        {
            _offCount++;
            OffRoute = _offCount >= 3;
            return null;
        }
        _offCount = 0;
        OffRoute = false;
        Along = Math.Max(Along, best.Along);
        var total = _cumulative[^1];
        RemainingMeters = Math.Max(0, total - Along);
        RemainingSeconds = total <= 0 ? 0 : Route.DurationSeconds * RemainingMeters / total;
        while (NextStepIndex < Route.Steps.Count - 1 && Route.Steps[NextStepIndex].AlongMeters <= Along + 5)
            NextStepIndex++;
        var next = NextStep;
        DistanceToNextStep = next == null ? RemainingMeters : Math.Max(0, Math.Min(next.AlongMeters, total) - Along);

        if (!Arrived && RemainingMeters < 30)
        {
            Arrived = true;
            return "You have arrived at the destination.";
        }
        if (next == null || next.Maneuver == Maneuver.Arrive && DistanceToNextStep > 200)
            return null;
        var fast = (speedMetersPerSecond ?? 0) > 20;
        int[] thresholds = fast ? new[] { 2000, 800, 250, 40 } : new[] { 600, 200, 40 };
        foreach (var t in thresholds)
        {
            if (DistanceToNextStep > t || !_spoken.Add((NextStepIndex, t)))
                continue;
            // Mark the wider thresholds as done too, so a late fix doesn't produce a burst of prompts.
            foreach (var wider in thresholds.Where(w => w > t))
                _spoken.Add((NextStepIndex, wider));
            return t <= 40 ? next.Instruction + "." : $"In {Spoken(DistanceToNextStep)}, {LowerFirst(next.Instruction)}.";
        }
        return null;
    }

    public static string Spoken(double meters) =>
        meters >= 950 ? $"{Math.Round(meters / 100) / 10:0.#} kilometres" : $"{Math.Max(10, Math.Round(meters / 10) * 10):0} metres";

    private static string LowerFirst(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;
}
