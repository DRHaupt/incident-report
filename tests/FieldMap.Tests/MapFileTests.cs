using System.IO.Compression;
using FieldMap.Core.Data;
using FieldMap.Core.Geo;
using FieldMap.Core.Overlays;

namespace FieldMap.Tests;

public class MapFileTests
{
    private static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "fieldmap-mapfiles-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private const string EventKml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2" xmlns:gx="http://www.google.com/kml/ext/2.2">
          <Document>
            <name>Summer Festival 2026</name>
            <Style id="red"><LineStyle><color>ff0000ff</color><width>4</width></LineStyle><PolyStyle><color>7f0000ff</color></PolyStyle></Style>
            <StyleMap id="redMap"><Pair><key>normal</key><styleUrl>#red</styleUrl></Pair><Pair><key>highlight</key><styleUrl>#red</styleUrl></Pair></StyleMap>
            <Folder>
              <name>Medical</name>
              <Folder>
                <name>First aid posts</name>
                <Placemark>
                  <name>First Aid 1</name>
                  <description><![CDATA[<b>Main tent</b><br/>Staffed 08:00&ndash;23:00]]></description>
                  <ExtendedData><Data name="radio"><value>Ch 3</value></Data></ExtendedData>
                  <Point><coordinates>-114.0719,51.0447,0</coordinates></Point>
                </Placemark>
              </Folder>
            </Folder>
            <Placemark>
              <name>Site boundary</name>
              <styleUrl>#redMap</styleUrl>
              <Polygon>
                <outerBoundaryIs><LinearRing><coordinates>-114.08,51.04 -114.06,51.04 -114.06,51.05 -114.08,51.05 -114.08,51.04</coordinates></LinearRing></outerBoundaryIs>
                <innerBoundaryIs><LinearRing><coordinates>-114.075,51.043 -114.072,51.043 -114.072,51.046 -114.075,51.043</coordinates></LinearRing></innerBoundaryIs>
              </Polygon>
            </Placemark>
            <Placemark>
              <name>Gates</name>
              <MultiGeometry>
                <Point><coordinates>-114.08,51.045</coordinates></Point>
                <Point><coordinates>-114.06,51.045</coordinates></Point>
                <LineString><coordinates>-114.08,51.045 -114.06,51.045</coordinates></LineString>
              </MultiGeometry>
            </Placemark>
            <Placemark>
              <name>Run course</name>
              <gx:Track><when>2026-07-01T09:00:00Z</when><when>2026-07-01T09:30:00Z</when>
                <gx:coord>-114.07 51.041 0</gx:coord><gx:coord>-114.065 51.048 0</gx:coord></gx:Track>
            </Placemark>
            <Placemark><name>Broken</name><LineString><coordinates>-114.07,51.04 bad -114.06,51.05</coordinates></LineString></Placemark>
            <NetworkLink><name>Live traffic</name><Link><href>https://example.test/live.kml</href></Link></NetworkLink>
            <GroundOverlay>
              <name>Site plan</name>
              <color>b3ffffff</color>
              <Icon><href>files/plan.png</href></Icon>
              <LatLonBox><north>51.05</north><south>51.04</south><east>-114.06</east><west>-114.08</west><rotation>0</rotation></LatLonBox>
            </GroundOverlay>
          </Document>
        </kml>
        """;

    private static readonly byte[] FakePng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

    [Fact]
    public void ReadsKmlPlacemarksFoldersStylesAndWarnings()
    {
        var c = MapFileImporter.ReadKml(EventKml, null);
        Assert.Equal("Summer Festival 2026", c.Name);
        var aid = c.Features.Single(f => f.Name == "First Aid 1");
        Assert.Equal(OverlayGeometry.Point, aid.Geometry);
        Assert.Equal("Medical / First aid posts", aid.Folder);
        Assert.Equal("Main tent\nStaffed 08:00–23:00", aid.Description);
        Assert.Equal("Ch 3", aid.Properties["radio"]);
        Assert.Equal(51.0447, aid.Parts[0][0].Lat, 6);

        var site = c.Features.Single(f => f.Name == "Site boundary");
        Assert.Equal(OverlayGeometry.Polygon, site.Geometry);
        Assert.Equal(2, site.Parts.Count); // outer ring + hole
        Assert.Equal(0xFFFF0000u, site.Stroke); // KML ff0000ff (aabbggrr) = opaque red
        Assert.Equal(0x7FFF0000u, site.Fill);
        Assert.Equal(4f, site.Width);

        Assert.Equal(3, c.Features.Count(f => f.Name == "Gates"));
        var run = c.Features.Single(f => f.Name == "Run course");
        Assert.Equal(OverlayGeometry.Line, run.Geometry);
        Assert.Equal("2026-07-01T09:00:00Z", run.Properties["start"]);

        var broken = c.Features.Single(f => f.Name == "Broken");
        Assert.Equal(2, broken.Parts[0].Count);
        Assert.Contains(c.Warnings, w => w.Contains("Broken") && w.Contains("1 coordinate"));
        Assert.Contains(c.Warnings, w => w.Contains("network link") && w.Contains("Live traffic"));
        // Plain KML can't carry the plan's picture.
        Assert.Empty(c.GroundOverlays);
        Assert.Contains(c.Warnings, w => w.Contains("Site plan") && w.Contains("KMZ"));
    }

    [Fact]
    public void ReadsKmzWithItsGroundOverlayPicture()
    {
        var dir = Dir();
        var kmz = Path.Combine(dir, "festival.kmz");
        using (var zip = ZipFile.Open(kmz, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("doc.kml").Open()))
                w.Write(EventKml);
            using (var s = zip.CreateEntry("files/plan.png").Open())
                s.Write(FakePng);
        }
        var c = MapFileImporter.Load(kmz);
        var plan = Assert.Single(c.GroundOverlays);
        Assert.Equal("Site plan", plan.Name);
        Assert.Equal(FakePng, plan.Image);
        Assert.Equal(0xB3 / 255f, plan.Opacity, 3);
        Assert.Equal(new GeoPoint(51.04, -114.08), plan.Corners[0]);
        Assert.Equal(new GeoPoint(51.05, -114.08), plan.Corners[3]);
        Assert.Contains("1 map image", c.Summary);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void RotatedGroundOverlayStaysRectangularOnTheGround()
    {
        var corners = MapFileImporter.BoxCorners(51.05, 51.04, -114.06, -114.08, 30);
        var center = new GeoPoint(51.045, -114.07);
        var d = corners.Select(c => center.DistanceTo(c)).ToList();
        Assert.All(d, x => Assert.Equal(d[0], x, 0));
        // Counter-clockwise: the lower-left corner moves south-east of where it was.
        Assert.True(corners[0].Lat < 51.04 + 1e-9 || corners[0].Lon > -114.08);
        Assert.Equal(corners[0].DistanceTo(corners[1]), new GeoPoint(51.04, -114.08).DistanceTo(new GeoPoint(51.04, -114.06)), 0);
    }

    [Fact]
    public void ReadsGpxWaypointsRoutesAndTracks()
    {
        const string gpx = """
            <gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1">
              <metadata><name>Marathon</name></metadata>
              <wpt lat="51.05" lon="-114.07"><name>Water station 1</name><desc>km 5</desc><sym>Drinking Water</sym></wpt>
              <wpt lat="91" lon="0"><name>Bad</name></wpt>
              <rte><name>Detour</name><rtept lat="51.04" lon="-114.08"/><rtept lat="51.05" lon="-114.07"/></rte>
              <trk><name>Course</name><trkseg>
                <trkpt lat="51.040" lon="-114.080"><time>2026-07-01T07:00:00Z</time></trkpt>
                <trkpt lat="51.050" lon="-114.080"><time>2026-07-01T07:10:00Z</time></trkpt>
              </trkseg></trk>
            </gpx>
            """;
        var c = MapFileImporter.ReadGpx(gpx);
        Assert.Equal("Marathon", c.Name);
        var wpt = Assert.Single(c.Features, f => f.Geometry == OverlayGeometry.Point);
        Assert.Equal("Water station 1", wpt.Name);
        Assert.Equal("Drinking Water", wpt.Properties["symbol"]);
        Assert.Contains(c.Features, f => f.Name == "Detour" && f.Folder == "Routes");
        var course = c.Features.Single(f => f.Name == "Course");
        Assert.StartsWith("1.11 km", course.Properties["length"]);
        Assert.Equal("2026-07-01T07:00:00Z", course.Properties["start"]);
    }

    [Fact]
    public void ReadsGeoJsonWithSimplestyle()
    {
        const string json = """
            { "type": "FeatureCollection", "name": "Road closures", "features": [
              { "type": "Feature", "properties": { "name": "Closed: 4 St", "stroke": "#ff0000", "stroke-width": 5 },
                "geometry": { "type": "LineString", "coordinates": [[-114.07,51.04],[-114.07,51.05]] } },
              { "type": "Feature", "properties": { "title": "Staging", "fill": "#00f", "fill-opacity": 0.25, "units": 4 },
                "geometry": { "type": "MultiPolygon", "coordinates": [[[[-114.1,51.0],[-114.09,51.0],[-114.09,51.01],[-114.1,51.0]]]] } },
              { "type": "Feature", "properties": {}, "geometry": { "type": "GeometryCollection", "geometries": [
                { "type": "Point", "coordinates": [-114.05, 51.03] }, { "type": "MultiPoint", "coordinates": [[-114.04, 51.03], [-114.03, 51.03]] } ] } }
            ]}
            """;
        var c = MapFileImporter.ReadGeoJson(json);
        Assert.Equal("Road closures", c.Name);
        var line = c.Features.Single(f => f.Geometry == OverlayGeometry.Line);
        Assert.Equal(0xFFFF0000u, line.Stroke);
        Assert.Equal(5f, line.Width);
        var area = c.Features.Single(f => f.Geometry == OverlayGeometry.Polygon);
        Assert.Equal("Staging", area.Name);
        Assert.Equal((uint)(0.25 * 255) << 24 | 0x0000FFu, area.Fill);
        Assert.Equal("4", area.Properties["units"]);
        Assert.Equal(3, c.Features.Count(f => f.Geometry == OverlayGeometry.Point));
    }

    [Fact]
    public void ReadsCsvPointsAndExplainsMissingColumns()
    {
        var dir = Dir();
        var good = Path.Combine(dir, "posts.csv");
        File.WriteAllText(good, "Name,Lat,Long,Type,Radio\nGate A,51.045,-114.08,Gate,Ch 1\n\"Medic, north\",51.049,-114.07,Medical,Ch 3\nNo position,,,Other,\n");
        var c = MapFileImporter.Load(good);
        Assert.Equal(2, c.Features.Count);
        Assert.Equal("Medic, north", c.Features[1].Name);
        Assert.Equal("Medical", c.Features[1].Folder);
        Assert.Equal("Ch 3", c.Features[1].Properties["Radio"]);
        Assert.Contains(c.Warnings, w => w.Contains("1 row"));
        var bad = Path.Combine(dir, "addresses.csv");
        File.WriteAllText(bad, "Name,Address\nA,1 Main St\n");
        Assert.Contains(MapFileImporter.Load(bad).Warnings, w => w.Contains("latitude"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void RejectsUnknownAndBrokenFiles()
    {
        var dir = Dir();
        var pdf = Path.Combine(dir, "map.pdf");
        File.WriteAllText(pdf, "%PDF-1.4");
        Assert.Throws<InvalidDataException>(() => MapFileImporter.Load(pdf));
        var kml = Path.Combine(dir, "broken.kml");
        File.WriteAllText(kml, "<kml><Document>");
        Assert.Throws<System.Xml.XmlException>(() => MapFileImporter.Load(kml));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void LibraryKeepsACopyAndRemembersSettings()
    {
        var dir = Dir();
        var src = Path.Combine(dir, "source.kml");
        File.WriteAllText(src, EventKml);
        var folder = Path.Combine(dir, "overlays");
        var lib = new OverlayLibrary(folder);
        var layer = lib.Import(src);
        Assert.Equal("Summer Festival 2026", layer.Info.Name);
        File.Delete(src); // the original can go away (USB stick removed)
        lib.SetVisible(layer.Info.Id, false);
        lib.SetImageOpacity(layer.Info.Id, 0.5f);
        var again = new OverlayLibrary(folder);
        var reloaded = Assert.Single(again.Layers);
        Assert.False(reloaded.Info.Visible);
        Assert.Equal(0.5f, reloaded.Info.ImageOpacity);
        Assert.Equal(layer.Content.Features.Count, reloaded.Content.Features.Count);
        Assert.Empty(again.VisibleLayers);
        again.Remove(reloaded.Info.Id);
        Assert.Empty(new OverlayLibrary(folder).Layers);
        var empty = Path.Combine(dir, "empty.gpx");
        File.WriteAllText(empty, "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"></gpx>");
        Assert.Throws<InvalidDataException>(() => again.Import(empty));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void AedsCanBeImportedFromKmlAndGpx()
    {
        var dir = Dir();
        var kml = Path.Combine(dir, "aeds.kml");
        File.WriteAllText(kml, """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
              <Placemark><name>Arena AED</name><description>Main lobby by the pro shop</description>
                <ExtendedData><Data name="last_verified"><value>2026-05-01</value></Data></ExtendedData>
                <Point><coordinates>-114.07,51.04</coordinates></Point></Placemark>
              <Placemark><name>Not a point</name><LineString><coordinates>-114.07,51.04 -114.06,51.05</coordinates></LineString></Placemark>
            </Document></kml>
            """);
        var aeds = PoiImport.ReadAeds(kml, "Event AEDs");
        var aed = Assert.Single(aeds);
        Assert.Equal("Arena AED", aed.Name);
        Assert.Equal("Main lobby by the pro shop", aed.Props["defibrillator:location"]);
        Assert.Equal("2026-05-01", aed.LastVerified);
        Assert.Equal("Event AEDs", aed.Source);
        Directory.Delete(dir, true);
    }
}
