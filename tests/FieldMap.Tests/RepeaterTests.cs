using FieldMap.Core.Build;
using FieldMap.Core.Geo;
using FieldMap.Core.Radio;
using FieldMap.Core.Tiles;

namespace FieldMap.Tests;

public sealed class RepeaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-rpt-" + Guid.NewGuid().ToString("N"));

    public RepeaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string File(string name, string text)
    {
        var p = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(p, text);
        return p;
    }

    [Theory]
    [InlineData("49.2827", true, 49.2827)]
    [InlineData("-123.1207", false, -123.1207)]
    [InlineData("123.1207W", false, -123.1207)]
    [InlineData("49 16 58 N", true, 49.282778)]
    [InlineData("49°16'58\"N", true, 49.282778)]
    [InlineData("491658N", true, 49.282778)]
    [InlineData("1230715W", false, -123.120833)]
    [InlineData("", true, null)]
    [InlineData("abc", true, null)]
    [InlineData("95", true, null)]
    public void Coordinates_parse_in_the_usual_formats(string text, bool lat, double? expected)
    {
        var v = RepeaterImport.ParseCoordinate(text, lat);
        if (expected == null)
            Assert.Null(v);
        else
            Assert.Equal(expected.Value, v!.Value, 5);
    }

    [Theory]
    [InlineData("146.940", 146.94)]
    [InlineData("146.94 MHz", 146.94)]
    [InlineData("146940 kHz", 146.94)]
    [InlineData("146940000", 146.94)]
    [InlineData("443,725", 443.725)]
    [InlineData("", null)]
    public void Frequencies_parse_in_mhz_khz_or_hz(string text, double? expected)
    {
        var v = RepeaterImport.ParseMHz(text);
        if (expected == null)
            Assert.Null(v);
        else
            Assert.Equal(expected.Value, v!.Value, 5);
    }

    [Theory]
    [InlineData("100", "CTCSS 100.0")]
    [InlineData("PL 141.3", "CTCSS 141.3")]
    [InlineData("D023N", "DCS 023")]
    [InlineData("023 DCS", "DCS 023")]
    [InlineData("NAC 293", "NAC 293")]
    [InlineData("CSQ", "")]
    public void Tones_are_normalised(string text, string expected) => Assert.Equal(expected, RepeaterImport.NormalizeTone(text));

    [Fact]
    public void Generic_csv_with_offset_or_input_columns_imports()
    {
        var path = File("club.csv",
            "Call Sign,Output,Offset,Tone,Location,Latitude,Longitude,Mode,Antenna height (m),ERP W,Notes\n" +
            "VE7RAG,147.000,+,CTCSS 100.0,Mt Seymour,49.3667,-122.9500,FM,15,60,Linked\n" +
            "VE6WRE,444.900,+5,D023,Calgary,51.05,114.07,FM,,,\n" +
            "BAD,,,,,,,,,,\n");
        var warnings = new List<string>();
        var list = RepeaterImport.ReadCsv(path, warnings);
        Assert.Equal(2, list.Count);
        Assert.Single(warnings);
        var a = list[0];
        Assert.Equal("VE7RAG", a.CallSign);
        Assert.Equal(147.6, a.InputMHz!.Value, 4); // "+" = standard 2 m offset
        Assert.Equal("CTCSS 100.0", a.Tone);
        Assert.Equal(15, a.AntennaHeightM);
        Assert.Equal(60, a.ErpWatts);
        Assert.Equal("147.000 MHz (+0.6) CTCSS 100.0", a.FrequencyText);
        var b = list[1];
        Assert.Equal(-114.07, b.Lon, 4); // missing minus sign fixed
        Assert.Equal(449.9, b.InputMHz!.Value, 4);
        Assert.Equal("DCS 023", b.Tone);
    }

    [Fact]
    public void RepeaterBook_json_imports_on_air_repeaters_with_modes()
    {
        var path = File("rb.json", """
            {"count":3,"results":[
              {"Callsign":"VE7RHS","Frequency":"145.27000","Input Freq":"144.67000","PL":"100.0","Lat":"49.2606","Long":"-123.2460",
               "Nearest City":"Vancouver","Landmark":"UBC","Use":"OPEN","Operational Status":"On-air","FM Analog":"Yes","DMR":"No"},
              {"Callsign":"VA7DMR","Frequency":"443.47500","Input Freq":"448.47500","PL":"","Lat":"49.30","Long":"-123.00",
               "Nearest City":"Burnaby","Operational Status":"On-air","DMR":"Yes","DMR Color Code":"1"},
              {"Callsign":"VE7OFF","Frequency":"147.18","Input Freq":"147.78","Lat":"50.0","Long":"-120.0","Operational Status":"Off-air"}
            ]}
            """);
        var list = RepeaterImport.ReadRepeaterBookJson(path);
        Assert.Equal(2, list.Count);
        Assert.Equal("CTCSS 100.0", list[0].Tone);
        Assert.Equal("Vancouver, UBC", list[0].Name);
        Assert.Equal(-0.6, list[0].OffsetMHz!.Value, 4);
        Assert.Equal("DMR", list[1].Mode);
        Assert.Equal("CC 1", list[1].Tone);
    }

    [Fact]
    public void Ised_land_mobile_rows_with_split_frequencies_become_repeaters_inside_the_area()
    {
        var path = File("ised.csv",
            "Licensee Name,Station Location,TX Frequency (MHz),RX Frequency (MHz),Latitude,Longitude,Antenna Height (m),ERP (W)\n" +
            "Valley Ambulance,Hill Site,155.3400,159.3400,501500N,1201000W,30,100\n" +
            "Valley Ambulance,Hill Site,155.3400,159.3400,501500N,1201000W,30,100\n" +   // duplicate licence line
            "Taxi Co,Downtown,152.4800,152.4800,50.67,-120.33,10,20\n" +                 // simplex base
            "Far Away Ltd,Toronto,451.000,456.000,43.65,-79.38,40,50\n");                // outside BC/AB
        var list = RepeaterImport.ReadIsedCsv(path, new GeoBounds(48, -140, 60, -110));
        var r = Assert.Single(list);
        Assert.Equal("Valley Ambulance", r.Owner);
        Assert.Equal(50.25, r.Lat, 4);
        Assert.Equal(-120.166667, r.Lon, 4);
        Assert.Equal(4.0, r.OffsetMHz!.Value, 4);
        Assert.Equal(30, r.AntennaHeightM);
        Assert.Throws<InvalidDataException>(() => RepeaterImport.ReadIsedCsv(File("other.csv", "a,b\n1,2\n"), null));
    }

    [Fact]
    public void Store_saves_and_reimporting_a_file_replaces_only_its_entries()
    {
        var path = Path.Combine(_dir, "repeaters.json");
        var store = new RepeaterStore(path);
        store.Save(new Repeater { Name = "Event portable", Source = RepeaterSource.Event, Lat = 50, Lon = -120, OutputMHz = 155.5 });
        store.ReplaceImport(RepeaterSource.Amateur, "club.csv", new[] { new Repeater { CallSign = "A", Lat = 50, Lon = -120, OutputMHz = 146.94 } });
        store.ReplaceImport(RepeaterSource.Amateur, "club.csv", new[]
        {
            new Repeater { CallSign = "B", Lat = 50, Lon = -120, OutputMHz = 146.76 }, new Repeater { CallSign = "C", Lat = 50, Lon = -120, OutputMHz = 147.12 }
        });
        var reloaded = new RepeaterStore(path);
        Assert.Equal(new[] { "B", "C", "Event portable" }, reloaded.All.Select(r => r.Label).Order().ToArray());
        Assert.Equal(2, reloaded.RemoveSource(RepeaterSource.Amateur));
        Assert.Single(new RepeaterStore(path).All);
    }

    [Fact]
    public void Cell_coverage_files_build_a_tileset_by_technology_clipped_to_the_map()
    {
        var kml = File("coverage.kml", """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document>
              <Folder><name>LTE</name>
                <Placemark><name>Area 1</name><Polygon><outerBoundaryIs><LinearRing><coordinates>
                  -121,50 -119,50 -119,51.5 -121,51.5 -121,50</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>
              </Folder>
              <Folder><name>5G NR</name>
                <Placemark><Polygon><outerBoundaryIs><LinearRing><coordinates>
                  -120.4,50.6 -120.2,50.6 -120.2,50.75 -120.4,50.75 -120.4,50.6</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>
              </Folder>
              <Placemark><name>Ontario LTE</name><Polygon><outerBoundaryIs><LinearRing><coordinates>
                  -80,43 -79,43 -79,44 -80,44 -80,43</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark>
            </Document></kml>
            """);
        var output = Path.Combine(_dir, CellCoverage.FileName);
        var summary = CellCoverage.Build(new[] { kml }, output, new GeoBounds(48, -140, 60, -110));
        Assert.Equal(2, summary.Areas);
        Assert.Equal(1, summary.ByKind["lte"]);
        Assert.Equal(1, summary.ByKind["5g"]);
        Assert.True(summary.Tiles > 10);

        using var tiles = new MbTilesReader(output);
        Assert.Equal(11, tiles.MaxZoom);
        var (x, y) = WebMercator.Tile(50.67, -120.33, 11);
        var layer = tiles.Get(11, x, y)!.Layer(TileSchema.Coverage)!;
        Assert.Contains(layer.Features, f => f.Str("kind") == "5g");
        Assert.Contains(layer.Features, f => f.Str("kind") == "lte");
        // No z12+ tiles: the map stretches z11.
        Assert.Null(tiles.Get(12, x * 2, y * 2));

        Assert.Throws<InvalidDataException>(() => CellCoverage.Build(new[] { kml }, output, new GeoBounds(0, 0, 1, 1)));
    }

    [Fact]
    public void Atlas_of_canada_coverage_keeps_only_the_newest_year_per_technology()
    {
        static string Square(string speed, int year, double lon)
        {
            var ms = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var ring = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{lon},50],[{lon + 0.5},50],[{lon + 0.5},50.5],[{lon},50.5],[{lon},50]");
            return "{\"type\":\"Feature\",\"properties\":{\"Speed\":\"" + speed + "\",\"Year\":" + ms + "}," +
                   "\"geometry\":{\"type\":\"MultiPolygon\",\"coordinates\":[[[" + ring + "]]]}}";
        }
        var path = File("coverage.geojson", "{\"type\":\"FeatureCollection\",\"features\":[" + string.Join(",",
            Square("LTE", 2022, -121), Square("LTE", 2024, -120), Square("LTE", 2024, -119), Square("5G", 2023, -121), Square("5G", 2024, -120)) + "]}");
        var summary = CellCoverage.Build(new[] { path }, Path.Combine(_dir, CellCoverage.FileName), new GeoBounds(48, -140, 60, -110));
        Assert.Equal(3, summary.Areas);
        Assert.Equal(2, summary.ByKind["lte"]);
        Assert.Equal(1, summary.ByKind["5g"]);
        Assert.Equal("5G 2024, LTE (4G) 2024", summary.Description);
        Assert.Equal(2, summary.Warnings.Count(w => w.Contains("older years left out")));
    }
}
