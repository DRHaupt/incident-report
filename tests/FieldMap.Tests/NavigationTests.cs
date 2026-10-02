using FieldMap.Core.Geo;
using FieldMap.Core.Routing;
using FieldMap.Core.Sample;

namespace FieldMap.Tests;

[Collection("sample")]
public class NavigationTests
{
    private readonly SampleFixture _f;

    public NavigationTests(SampleFixture f) => _f = f;

    [Fact]
    public void Tracks_progress_prompts_and_arrival()
    {
        var router = new Router(_f.Graph);
        var route = router.Route(SampleRegion.At(SampleRegion.StreetX(0), 20), SampleRegion.Intersection(4, 4), VehicleProfile.Car)!;
        var nav = new NavigationTracker(route);
        var prompts = new List<string>();
        var cum = route.CumulativeDistance();
        // Drive along the route geometry in 20 m steps.
        for (var d = 0.0; d <= route.DistanceMeters + 1; d += 20)
        {
            var i = Array.FindIndex(cum, c => c >= d);
            if (i <= 0) i = 1;
            var t = cum[i] - cum[i - 1] <= 0 ? 0 : (d - cum[i - 1]) / (cum[i] - cum[i - 1]);
            var p = Geometry.Lerp(route.Geometry[i - 1], route.Geometry[i], Math.Clamp(t, 0, 1));
            if (nav.Update(p, 5, 12) is { } prompt)
                prompts.Add(prompt);
            Assert.False(nav.OffRoute);
        }
        Assert.True(nav.Arrived);
        Assert.Contains(prompts, p => p.StartsWith("In ", StringComparison.Ordinal));
        Assert.Contains(prompts, p => p.Contains("Turn", StringComparison.Ordinal));
        Assert.Equal("You have arrived at the destination.", prompts[^1]);
        Assert.Equal(prompts.Count, prompts.Distinct().Count()); // nothing repeated
        Assert.InRange(nav.RemainingMeters, 0, 30);
    }

    [Fact]
    public void Leaving_the_route_is_detected_after_three_fixes()
    {
        var router = new Router(_f.Graph);
        var route = router.Route(SampleRegion.Intersection(0, 0), SampleRegion.Intersection(0, 7), VehicleProfile.Car)!;
        var nav = new NavigationTracker(route);
        nav.Update(SampleRegion.Intersection(0, 1), 5);
        var away = SampleRegion.At(SampleRegion.StreetX(1), 300);
        nav.Update(away, 5);
        Assert.False(nav.OffRoute);
        nav.Update(away, 5);
        nav.Update(away, 5);
        Assert.True(nav.OffRoute);
        // Poor accuracy widens the tolerance.
        var lenient = new NavigationTracker(route);
        for (var i = 0; i < 3; i++)
            lenient.Update(SampleRegion.At(SampleRegion.StreetX(1), 60), 60);
        Assert.False(lenient.OffRoute);
        Assert.Equal("1.5 kilometres", NavigationTracker.Spoken(1480));
        Assert.Equal("200 metres", NavigationTracker.Spoken(204));
    }
}
