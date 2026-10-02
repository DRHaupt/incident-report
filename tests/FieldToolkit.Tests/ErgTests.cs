using FieldToolkit.Core.Hazmat;

namespace FieldToolkit.Tests;

/// <summary>
/// Parser tests use synthetic pages shaped like the ERG layout. Material names, numbers and text are fictitious –
/// they exercise the parser, they are not hazmat data.
/// </summary>
public class ErgParserTests
{
    private static ErgPage IdPage(int n, params string[] columnLines) =>
        new(n, new[] { "ID Guide Name of Material ID Guide Name of Material" }, new[] { "ID Guide Name of Material" }.Concat(columnLines).Append("Page 42").ToList());

    [Fact]
    public void Id_list_rows_wrapped_names_and_polymerization_suffix()
    {
        var page = IdPage(30,
            "9001 901 Testium chloride",
            "9002 902P Testium vinyl monomer, stabilized,",
            "with extra words on a second line",
            "9003 903 Placeholderine");
        var r = ErgTextParser.Parse(new[] { page });
        Assert.Equal(ErgPageKind.IdList, r.PageKinds[30]);
        Assert.Equal(3, r.Data.Materials.Count);
        var m = r.Data.Materials[1];
        Assert.Equal("9002", m.Id);
        Assert.Equal("902", m.Guide);
        Assert.True(m.Polymerizable);
        Assert.Equal("Testium vinyl monomer, stabilized, with extra words on a second line", m.Name);
        Assert.Equal("30", m.Page);
    }

    [Fact]
    public void Name_list_is_cross_checked_against_id_list()
    {
        var ids = IdPage(30, "9001 901 Testium chloride", "9002 902 Testium oxide", "9003 903 Placeholderine", "9004 901 Fakeol", "9005 902 Mockane");
        var names = new ErgPage(80, new[] { "Name of Material Guide ID" }, new[]
        {
            "Name of Material Guide ID", "Fakeol 901 9004", "Mockane 902 9005", "Placeholderine 903 9003", "Testium chloride 901 9001",
            "Testium oxide 902 9002", "Unlisted thing 903 9009"
        });
        var r = ErgTextParser.Parse(new[] { ids, names });
        Assert.Equal(ErgPageKind.NameList, r.PageKinds[80]);
        Assert.Equal(6, r.NameListEntries.Count);
        Assert.Contains(r.Warnings, w => w.Contains("9009"));
        Assert.Contains(r.Warnings, w => w.Contains("ID list has 5 entries but the name list has 6"));
    }

    [Fact]
    public void Guide_pages_are_split_into_sections_subsections_and_items()
    {
        var left = new ErgPage(200, new[]
        {
            "GUIDE 901 TEST LIQUIDS", "(PLACEHOLDER)", "ERG 2024",
            "POTENTIAL HAZARDS",
            "FIRE OR EXPLOSION",
            "• First fire placeholder sentence that is long and",
            "wraps onto a second line.",
            "• Second fire placeholder.",
            "HEALTH",
            "• Health placeholder.",
            "PUBLIC SAFETY",
            "• Public safety placeholder.",
            "PROTECTIVE CLOTHING",
            "• Clothing placeholder.",
            "EVACUATION",
            "Large Spill",
            "• Evacuation placeholder.",
            "201"
        });
        var right = new ErgPage(201, new[]
        {
            "GUIDE 901 TEST LIQUIDS (PLACEHOLDER)",
            "EMERGENCY RESPONSE",
            "FIRE",
            "CAUTION: Placeholder caution.",
            "Small Fire",
            "• Small fire placeholder.",
            "SPILL OR LEAK",
            "• Spill placeholder.",
            "FIRST AID",
            "• First aid placeholder."
        });
        var r = ErgTextParser.Parse(new[] { left, right });
        Assert.Equal(ErgPageKind.Guide, r.PageKinds[200]);
        var g = Assert.Single(r.Data.Guides);
        Assert.Equal("901", g.Number);
        Assert.Equal("TEST LIQUIDS (PLACEHOLDER)", g.Title);
        Assert.Equal("200,201", g.Pages);
        Assert.Equal(new[] { "POTENTIAL HAZARDS", "PUBLIC SAFETY", "EMERGENCY RESPONSE" }, g.Sections.Select(s => s.Heading));
        var fire = g.Sections[0].Subsections[0];
        Assert.Equal("FIRE OR EXPLOSION", fire.Heading);
        Assert.Equal("First fire placeholder sentence that is long and wraps onto a second line.", fire.Items[0]);
        Assert.Equal(2, fire.Items.Count);
        var evac = g.Sections[1].Subsections.Single(s => s.Heading == "EVACUATION");
        Assert.Equal(new[] { "Large Spill", "Evacuation placeholder." }, evac.Items);
        var erFire = g.Sections[2].Subsections[0];
        Assert.Equal(new[] { "CAUTION: Placeholder caution.", "Small Fire", "Small fire placeholder." }, erFire.Items);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Table1_rows_with_wrapped_names_and_table3_references()
    {
        var ids = IdPage(30, "9001 901 Testium chloride", "9002 902 Testium oxide", "9003 903 Placeholderine", "9004 901 Fakeol", "9005 902 Mockane");
        var t1 = new ErgPage(300, new[]
        {
            "TABLE 1 - INITIAL ISOLATION AND PROTECTIVE ACTION DISTANCES",
            "SMALL SPILLS LARGE SPILLS",
            "9001 901 Testium chloride 30 m (100 ft) 0.1 km (0.1 mi) 0.3 km (0.2 mi) 100 m (300 ft) 0.5 km (0.3 mi) 1.9 km (1.2 mi)",
            "9002 902 Testium oxide (when spilled",
            "in water) 60 m (200 ft) 0.2 km (0.1 mi) 0.9 km (0.6 mi) 300 m (1000 ft) 2.1 km (1.3 mi) 11.0+ km (7.0+ mi)",
            "9004 901 Fakeol 30 m (100 ft) 0.1 km (0.1 mi) 0.2 km (0.1 mi) Refer to Table 3",
            "ERG 2024"
        });
        var t2 = new ErgPage(320, new[] { "TABLE 2 - WATER-REACTIVE MATERIALS WHICH PRODUCE TOXIC GASES", "9005 902 Mockane HCl" });
        var r = ErgTextParser.Parse(new[] { ids, t1, t2 });
        Assert.Equal(ErgPageKind.Table1, r.PageKinds[300]);
        Assert.Equal(3, r.Data.IsolationTable.Count);
        var a = r.Data.IsolationTable[0];
        Assert.Equal("Testium chloride", a.Name);
        Assert.Equal("30 m (100 ft)", a.Small.Isolate);
        Assert.Equal("0.3 km (0.2 mi)", a.Small.ProtectNight);
        Assert.Equal("1.9 km (1.2 mi)", a.Large.ProtectNight);
        var b = r.Data.IsolationTable[1];
        Assert.Equal("Testium oxide (when spilled in water)", b.Name);
        Assert.Equal("11.0+ km (7.0+ mi)", b.Large.ProtectNight);
        var c = r.Data.IsolationTable[2];
        Assert.Equal("Refer to Table 3", c.Large.Isolate);

        Assert.True(r.Data.Materials.Single(m => m.Id == "9001").Highlighted);
        Assert.False(r.Data.Materials.Single(m => m.Id == "9003").Highlighted);
        Assert.True(r.Data.Materials.Single(m => m.Id == "9005").WaterReactive);
    }

    [Fact]
    public void Unreadable_table1_rows_are_reported_not_guessed()
    {
        var t1 = new ErgPage(300, new[]
        {
            "TABLE 1 - INITIAL ISOLATION AND PROTECTIVE ACTION DISTANCES",
            "9001 901 Testium 30 m (100 ft) 0.1 km (0.1 mi) 0.3 km (0.2 mi) 100 m (300 ft) 0.5 km (0.3 mi) 1.9 km (1.2 mi)",
            "9002 902 Garbled 30 m 0.1 km",
            "9003 903 Other 30 m (100 ft) 0.1 km (0.1 mi) 0.3 km (0.2 mi) 100 m (300 ft) 0.5 km (0.3 mi) 1.9 km (1.2 mi)"
        });
        var r = ErgTextParser.Parse(new[] { t1 });
        Assert.Equal(2, r.Data.IsolationTable.Count);
        Assert.Contains(r.Warnings, w => w.Contains("9002"));
    }

    [Fact]
    public void Placard_guides_come_only_from_csv()
    {
        var placards = ErgPlacards.StandardClasses();
        Assert.All(placards, p => Assert.Equal("", p.Guide));
        var problems = ErgPlacards.ApplyGuides(placards, new[] { "# comment", "code,guide", "3,901", "8,9x1" });
        Assert.Equal("901", placards.Single(p => p.Code == "3").Guide);
        Assert.Single(problems);
    }
}

public class ErgDatasetTests
{
    private static readonly Lazy<ErgDataset> Sample = new(() => ErgRepository.Load(TestPaths.Content("erg", "erg.json")));

    [Fact]
    public void Shipped_dataset_is_the_clearly_marked_sample()
    {
        var d = Sample.Value;
        Assert.True(d.IsSample);
        Assert.Contains("SAMPLE", d.Notes);
        Assert.Empty(d.Problems);
        // Every text field visible to a user is marked as sample/fictitious.
        Assert.All(d.Materials, m => Assert.Contains("SAMPLE", m.Name));
        Assert.All(d.Guides, g => Assert.Contains("SAMPLE", g.Title));
        Assert.All(d.Guides.SelectMany(g => g.Sections).SelectMany(s => s.Subsections).SelectMany(s => s.Items), i => Assert.StartsWith("[SAMPLE", i));
        Assert.All(d.IsolationTable, e => Assert.Contains("SAMPLE", e.Small.Isolate));
        Assert.All(d.Placards, p => Assert.Equal("", p.Guide));
    }

    [Theory]
    [InlineData("9901", "9901")]
    [InlineData("UN9902", "9902")]
    [InlineData("un 9903", "9903")]
    [InlineData("NA9904", "9904")]
    [InlineData("alpha", "9901")]
    [InlineData("sample material bravo", "9902")]
    [InlineData("guide 903", "9903")]
    public void Search_by_number_name_and_guide(string query, string expectedFirst)
    {
        var hits = new ErgSearch(Sample.Value).Search(query);
        Assert.NotEmpty(hits);
        Assert.Equal(expectedFirst, hits[0].Id);
    }

    [Fact]
    public void Partial_number_lists_prefix_matches()
    {
        var hits = new ErgSearch(Sample.Value).Search("990");
        Assert.Equal(6, hits.Count);
    }

    [Fact]
    public void Highlighted_material_has_isolation_distances()
    {
        var search = new ErgSearch(Sample.Value);
        var m = search.Search("9902")[0];
        Assert.True(m.Highlighted);
        Assert.Single(search.Isolation(m));
        Assert.Empty(search.Isolation(search.Search("9901")[0]));
        Assert.NotNull(search.Guide(m.Guide));
    }

    [Theory]
    [InlineData("2", 2)]   // class 2 covers divisions 2.1 and 2.3 in the sample
    [InlineData("2.3", 1)]
    [InlineData("8", 1)]
    [InlineData("7", 0)]
    public void Placard_filter_by_class(string code, int expected)
    {
        var search = new ErgSearch(Sample.Value);
        Assert.Equal(expected, search.ByPlacard(new ErgPlacard { Code = code }).Count);
    }

    [Fact]
    public void Validation_catches_broken_references()
    {
        var d = new ErgDataset
        {
            Guides = { new ErgGuide { Number = "901", Sections = { new ErgSection { Heading = "POTENTIAL HAZARDS" } } } },
            Materials = { new ErgMaterial { Id = "12", Name = "Bad id", Guide = "901" }, new ErgMaterial { Id = "9001", Name = "No guide", Guide = "999" } },
            IsolationTable = { new ErgIsolationEntry { Id = "9002", Name = "Orphan" } }
        };
        var problems = ErgRepository.Validate(d).ToList();
        Assert.Contains(problems, p => p.Contains("invalid ID"));
        Assert.Contains(problems, p => p.Contains("guide 999"));
        Assert.Contains(problems, p => p.Contains("no matching material"));
        Assert.Contains(problems, p => p.Contains("missing distances"));
    }

    [Fact]
    public void Missing_file_gives_empty_dataset_with_problem()
    {
        var d = ErgRepository.LoadOrEmpty(Path.Combine(TestPaths.TempFolder(), "none.json"));
        Assert.True(d.IsEmpty);
        Assert.NotEmpty(d.Problems);
    }
}
