using System.IO.Compression;
using System.Text;
using FieldMap.Core.Data;

namespace FieldMap.Tests;

[Collection("sample")]
public class AedImportTests
{
    private readonly SampleFixture _f;

    public AedImportTests(SampleFixture f) => _f = f;

    private static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "fieldmap-aed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A minimal .xlsx: shared strings, an inline string, numbers, a gap between columns, two sheets.</summary>
    private static string WriteXlsx(string dir)
    {
        var path = Path.Combine(dir, "aeds.xlsx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Entry(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Entry("xl/workbook.xml", $"""<workbook xmlns="{ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="All AEDs" sheetId="1" r:id="rId1"/><sheet name="Notes" sheetId="2" r:id="rId2"/></sheets></workbook>""");
        Entry("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="worksheet" Target="/xl/worksheets/sheet2.xml"/></Relationships>""");
        Entry("xl/sharedStrings.xml", $"""
            <sst xmlns="{ns}">
              <si><t>Venue / AED site</t></si><si><t>Address</t></si><si><t>City</t></si><si><t># AEDs</t></si><si><t>AED location in building</t></si>
              <si><t>Latitude</t></si><si><t>Longitude</t></si><si><t>Source</t></si>
              <si><r><t>Arena </t></r><r><t>North</t></r></si><si><t>Main lobby</t></si><si><t>Alberta Heart Safe</t></si>
              <si><t>Curling Club</t></si><si><t>#2 150 MAIN ST</t></si><si><t>Sampleton</t></si><si><t>BC PAD Program</t></si>
            </sst>
            """);
        Entry("xl/worksheets/sheet1.xml", $"""
            <worksheet xmlns="{ns}"><sheetData>
              <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c><c r="D1" t="s"><v>3</v></c><c r="E1" t="s"><v>4</v></c><c r="F1" t="s"><v>5</v></c><c r="G1" t="s"><v>6</v></c><c r="H1" t="s"><v>7</v></c></row>
              <row r="2"><c r="A2" t="s"><v>8</v></c><c r="D2"><v>3</v></c><c r="E2" t="s"><v>9</v></c><c r="F2"><v>51.044733</v></c><c r="G2"><v>-114.071883</v></c><c r="H2" t="s"><v>10</v></c></row>
              <row r="3"><c r="A3" t="s"><v>11</v></c><c r="B3" t="s"><v>12</v></c><c r="C3" t="s"><v>13</v></c><c r="H3" t="s"><v>14</v></c></row>
              <row r="4"><c r="A4" t="inlineStr"><is><t>Ruxton Island</t></is></c><c r="B4" t="inlineStr"><is><t>RESIDENTIAL ISLAND. LATITUDE 49 DEGREES 4 MINUTES 46.7 SECONDS NORTH, LONGITUDE: 123 DEGREES 42 MINUTES 24.0 SECONDS WEST.</t></is></c></row>
              <row r="5"><c r="A5" t="inlineStr"><is><t>Street only</t></is></c><c r="B5" t="inlineStr"><is><t>6th Street NE</t></is></c><c r="C5" t="inlineStr"><is><t>Calgary</t></is></c></row>
              <row r="6"></row>
            </sheetData></worksheet>
            """);
        Entry("xl/worksheets/sheet2.xml", $"""<worksheet xmlns="{ns}"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Source</t></is></c></row></sheetData></worksheet>""");
        return path;
    }

    [Fact]
    public void ReadsExcelWorkbooks()
    {
        var dir = Dir();
        var path = WriteXlsx(dir);
        Assert.Equal(new[] { "All AEDs", "Notes" }, XlsxReader.SheetNames(path));
        var rows = XlsxReader.Rows(path);
        Assert.Equal(4, rows.Count);
        Assert.Equal("Arena North", rows[0]["venue / aed site"]);
        Assert.Equal("51.044733", rows[0]["latitude"]);
        Assert.Equal("3", rows[0]["# aeds"]);
        Assert.False(rows[0].ContainsKey("address") && rows[0]["address"].Length > 0);
        Assert.Single(XlsxReader.Rows(path, "Notes").DefaultIfEmpty());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ReadsRegistryColumnsAndPlacesWhatItCan()
    {
        var dir = Dir();
        var rows = AedFile.Read(WriteXlsx(dir), "Imported list");
        Assert.Equal(4, rows.Count);
        var arena = rows[0];
        Assert.Equal("Arena North", arena.Name);
        Assert.Equal("Main lobby", arena.Location);
        Assert.Equal(3, arena.Count);
        Assert.Equal("Alberta Heart Safe", arena.Source);
        Assert.True(arena.HasPosition);
        Assert.Equal("Imported list", rows[2].Source); // no source column value

        var report = AedFile.Locate(rows, q => _f.Search.Search(q, null, 8));
        Assert.Equal(new AedLocateReport(1, 1, 1, 1), report);
        var club = rows[1];
        Assert.True(club.HasPosition);
        Assert.Contains("150 Main Street", club.LocationMethod);
        Assert.Equal(49.0796, rows[2].Lat!.Value, 4);
        Assert.Equal(-123.7067, rows[2].Lon!.Value, 4);
        Assert.Equal("coordinates written in the list", rows[2].LocationMethod);
        Assert.False(rows[3].HasPosition);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void AddressesInAnotherTownAreNotUsed()
    {
        var rows = new List<AedRow> { new() { Name = "Club", Address = "150 MAIN ST", City = "Kamloops" } };
        Assert.Equal(1, AedFile.Locate(rows, q => _f.Search.Search(q, null, 8)).NotPlaced);
    }

    [Theory]
    [InlineData("#2 175B WRANGLER WY", "175B WRANGLER WY")]
    [InlineData("1 175B WRANGLER WY", "175B WRANGLER WY")]
    [InlineData("207 2776 BOURQUIN CRESCENT WEST CR W", "2776 BOURQUIN CRESCENT WEST CR W")]
    [InlineData("Unit 4, 120 Main St", "120 Main St")]
    [InlineData("12-450 Main St", "450 Main St")]
    [InlineData("4924 TELQUA DR", "4924 TELQUA DR")]
    public void StripsUnitNumbers(string input, string expected) => Assert.Equal(expected, AedFile.StreetAddress(input));

    [Theory]
    [InlineData("Lat 49.27123, -123.11456 (parking lot)", 49.27123, -123.11456)]
    [InlineData("51.0447 114.0719", 51.0447, -114.0719)]
    [InlineData("49°4'46.7\"N 123°42'24.0\"W", 49.0796, -123.7067)]
    public void FindsCoordinatesInText(string text, double lat, double lon)
    {
        var p = AedFile.CoordinatesInText(text);
        Assert.NotNull(p);
        Assert.Equal(lat, p!.Value.Lat, 4);
        Assert.Equal(lon, p.Value.Lon, 4);
    }

    [Fact]
    public void NormalisedCsvFeedsTheBuildWithEveryField()
    {
        var dir = Dir();
        var rows = AedFile.Read(WriteXlsx(dir), "Imported list");
        AedFile.Locate(rows, q => _f.Search.Search(q, null, 8));
        var csv = Path.Combine(dir, "aeds.csv");
        AedFile.WriteCsv(csv, rows.Where(r => r.HasPosition), new DateTime(2026, 10, 3));
        var pois = PoiImport.ReadAeds(csv, "fallback");
        Assert.Equal(3, pois.Count);
        var arena = pois.Single(p => p.Name == "Arena North");
        Assert.Equal("3", arena.Props["aed_count"]);
        Assert.Equal("Main lobby", arena.Props["defibrillator:location"]);
        Assert.Equal("Alberta Heart Safe", arena.Source);
        Assert.False(arena.Props.ContainsKey("location_method"));
        var club = pois.Single(p => p.Name == "Curling Club");
        Assert.Contains("street address", club.Props["location_method"]);
        Assert.Equal("BC PAD Program", club.Source);
        Assert.Equal("#2 150 MAIN ST, Sampleton", club.Props["address"]);
        // The xlsx itself can also be read at build time.
        Assert.Single(PoiImport.ReadAeds(Path.Combine(dir, "aeds.xlsx"), "x"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void BuildsKeepOnlyAedsOnTheRegionsMap()
    {
        var dir = Dir();
        var inside = FieldMap.Core.Sample.SampleRegion.At(120, 120);
        var csv = Path.Combine(dir, "registry.csv");
        File.WriteAllText(csv, "name,latitude,longitude,source\n" +
            $"Inside AED,{inside.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{inside.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)},Test registry\n" +
            "Regina AED,50.4189,-104.5115,Test registry\n");
        var output = Path.Combine(dir, "out");
        RegionBuilder.BuildSample(output, Path.Combine(dir, "work"), null, default, new List<(string, string)> { (csv, "Test registry") });
        using var search = new FieldMap.Core.Search.SearchEngine(Path.Combine(output, RegionBuilder.SearchFile));
        var aeds = search.LoadPois().Where(p => p.Layer == FieldMap.Core.Layers.LayerCatalog.Aed).ToList();
        Assert.Contains(aeds, a => a.Name == "Inside AED" && a.Source == "Test registry");
        Assert.DoesNotContain(aeds, a => a.Name == "Regina AED");
        Directory.Delete(dir, true);
    }
}

