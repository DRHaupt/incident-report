using FieldMap.Core.Data;

namespace FieldMap.Tests;

public sealed class DownloadMeterTests
{
    private sealed class Capture : IProgress<(string Stage, double Fraction)>
    {
        public readonly List<(string Stage, double Fraction)> Reports = new();
        public void Report((string Stage, double Fraction) value) { lock (Reports) Reports.Add(value); }
    }

    [Fact]
    public void Downloads_side_by_side_share_one_progress_line()
    {
        var capture = new Capture();
        var meter = new DownloadMeter(capture);
        var osm = meter.For("OpenStreetMap");
        var oda = meter.For("Addresses");
        osm.Report(("ignored", 0.5));
        meter.Finished("Elevation-not-started"); // a finished entry counts as complete
        Thread.Sleep(300);
        oda.Report(("ignored", 0.25));
        var last = capture.Reports[^1];
        Assert.Contains("OpenStreetMap 50", last.Stage);
        Assert.Contains("Addresses 25", last.Stage);
        Assert.Equal((0.5 + 0.25 + 1) / 3, last.Fraction, 3);

        osm.AddBytes(1_000_000);
        meter.Finished("OpenStreetMap");
        last = capture.Reports[^1];
        Assert.DoesNotContain("OpenStreetMap", last.Stage);
        Assert.StartsWith("Downloading Addresses", last.Stage);
        meter.Finished("Addresses");
        Assert.Equal("Downloads finished", capture.Reports[^1].Stage);
        Assert.Equal(1, capture.Reports[^1].Fraction, 3);
    }
}
