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

/// <summary>
/// Tests against the shipped dataset imported from the official ERG2024 PDF. Expected values were checked against the
/// printed guidebook (see docs/erg/ERG2024-verification.md).
/// </summary>
public class ErgDatasetTests
{
    private static readonly Lazy<ErgDataset> Data = new(() => ErgRepository.Load(TestPaths.Content("erg", "erg.json")));
    private static ErgSearch Search => new(Data.Value);

    [Fact]
    public void Shipped_dataset_is_the_official_erg2024_import()
    {
        var d = Data.Value;
        Assert.False(d.IsSample);
        Assert.Equal("ERG2024", d.Edition);
        Assert.Equal("ERG2024-Eng-Web-a.pdf", d.Source);
        Assert.Equal(64, d.SourceSha256.Length);
        Assert.Empty(d.Problems);
        Assert.Equal(2771, d.Materials.Count);
        Assert.Equal(62, d.Guides.Count);
        Assert.Equal(new[] { "121", "167" }, d.BlankGuides);
        Assert.Equal(537, d.IsolationTable.Count);
        Assert.Equal(108, d.Materials.Count(m => m.WaterReactive));
    }

    [Fact]
    public void Every_guide_from_111_to_174_is_present_or_printed_blank()
    {
        var numbers = Data.Value.Guides.Select(g => int.Parse(g.Number)).Concat(Data.Value.BlankGuides.Select(int.Parse)).Order();
        Assert.Equal(Enumerable.Range(111, 64), numbers);
        Assert.All(Data.Value.Guides, g => Assert.Equal(new[] { "POTENTIAL HAZARDS", "PUBLIC SAFETY", "EMERGENCY RESPONSE" }, g.Sections.Select(s => s.Heading)));
    }

    [Fact]
    public void Guide_128_matches_the_printed_page()
    {
        var g = Search.Guide("128")!;
        Assert.Equal("Flammable Liquids (Water-Immiscible)", g.Title);
        var firstAid = g.Sections[2].Subsections.Single(s => s.Heading == "FIRST AID");
        Assert.Equal("Refer to the “General First Aid” section.", firstAid.Items[0]);
        Assert.Contains(g.Notes, n => n.StartsWith("In Canada, an Emergency Response Assistance Plan (ERAP)"));
        var fire = g.Sections[2].Subsections.Single(s => s.Heading == "FIRE");
        Assert.Contains("Small Fire", fire.Items);
        Assert.Contains("Dry chemical, CO2, water spray or regular foam.", string.Join(" ", fire.Items));
    }

    [Fact]
    public void General_first_aid_section_is_imported()
    {
        var items = Data.Value.GeneralFirstAid!.Subsections.SelectMany(s => s.Items).ToList();
        Assert.Equal("Call 911 or emergency medical service.", items[0]);
        Assert.Contains(items, i => i.StartsWith("– DO NOT perform mouth-to-mouth resuscitation"));
    }

    [Theory]
    [InlineData("1203", "128")]
    [InlineData("UN1005", "125")]
    [InlineData("un 1017", "124")]
    [InlineData("NA1993", "128")]
    [InlineData("1830", "137")]
    public void Lookup_by_number(string query, string guide)
    {
        var hits = Search.Search(query);
        Assert.NotEmpty(hits);
        Assert.Equal(query.Replace("UN", "", StringComparison.OrdinalIgnoreCase).Replace("NA", "").Replace("un", "").Trim(), hits[0].Id);
        Assert.Equal(guide, hits[0].Guide);
    }

    [Theory]
    [InlineData("chlorine", "1017")]
    [InlineData("gasoline", "1203")]
    [InlineData("anhydrous ammonia", "1005")]
    [InlineData("propane", "1075")]
    public void Lookup_by_name(string query, string id) => Assert.Equal(id, Search.Search(query)[0].Id);

    [Fact]
    public void Partial_numbers_and_guides_list_matches()
    {
        Assert.All(Search.Search("120"), m => Assert.StartsWith("120", m.Id));
        Assert.Contains(Search.Search("guide 124"), m => m.Id == "1017");
    }

    [Fact]
    public void Chlorine_isolation_distances_and_table3_reference()
    {
        var chlorine = Search.Search("1017").First(m => m.Name == "Chlorine");
        Assert.True(chlorine.Highlighted);
        var e = Assert.Single(Search.Isolation(chlorine));
        Assert.Equal("60 m (200 ft)", e.Small.Isolate);
        Assert.Equal("0.3 km (0.2 mi)", e.Small.ProtectDay);
        Assert.Equal("1.5 km (0.9 mi)", e.Small.ProtectNight);
        Assert.Equal("Refer to Table 3", e.Large.Isolate);
    }

    [Fact]
    public void Shared_distance_groups_and_inhalation_zones()
    {
        var t1 = Data.Value.IsolationTable;
        var sulfate = t1.Single(e => e.Name == "Dimethyl sulfate");
        var sulphate = t1.Single(e => e.Name == "Dimethyl sulphate");
        Assert.Equal(sulfate.Large.ProtectNight, sulphate.Large.ProtectNight);
        Assert.Equal("0.7 km (0.4 mi)", sulfate.Large.ProtectNight);

        var zoneA = t1.Single(e => e.Name == "Bromine, solution (Inhalation Hazard Zone A)");
        var zoneB = t1.Single(e => e.Name == "Bromine, solution (Inhalation Hazard Zone B)");
        Assert.Equal("60 m (200 ft)", zoneA.Small.Isolate);
        Assert.Equal("30 m (100 ft)", zoneB.Small.Isolate);
    }

    [Fact]
    public void Entries_without_id_numbers_are_searchable_by_name()
    {
        var noId = Data.Value.Materials.Where(m => m.Id.Length == 0).ToList();
        Assert.Equal(4, noId.Count);
        Assert.Contains(Search.Search("blasting agent"), m => m.Id == "" && m.Guide == "112");
    }

    [Fact]
    public void Water_reactive_gases_from_table2()
    {
        var m = Data.Value.Materials.First(x => x.Id == "1340");
        Assert.True(m.WaterReactive);
        Assert.Equal("H2S", m.WaterReactiveGases);
    }

    [Theory]
    [InlineData("3", "127")]
    [InlineData("2.3", "123")]
    [InlineData("8", "153")]
    [InlineData("DANGEROUS", "111")]
    [InlineData("LITHIUM-ION", "147")]
    public void Placard_initial_guides_from_the_placard_table(string code, string guide)
    {
        var p = Data.Value.Placards.Single(x => x.Code == code);
        Assert.Equal(guide, p.Guide);
        Assert.NotNull(Search.Guide(guide));
        // Guide 111 (mixed load / unidentified cargo) has no materials of its own.
        if (code != "DANGEROUS")
            Assert.NotEmpty(Search.ByPlacard(p));
        Assert.All(Search.ByPlacard(p), m => Assert.Equal(guide, m.Guide));
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

/// <summary>Synthetic pages in the ERG 2024 layout (fictitious content) for the layout features found in the real PDF.</summary>
public class Erg2024LayoutTests
{
    [Fact]
    public void Guide_header_with_number_on_next_line_blank_guides_notes_and_columns()
    {
        var left = new ErgPage(200, new[]
        {
            "GUIDE Test Liquids -", "901 Placeholder", "POTENTIAL HAZARDS", "FIRE OR EXPLOSION", "• Fire placeholder.",
            "PUBLIC SAFETY", "• Safety placeholder.", "Page 198 ERG 2024"
        });
        var right = new ErgPage(201, new[]
        {
            "\tTest Liquids - GUIDE", "\t901 Placeholder", "EMERGENCY RESPONSE", "SPILL OR LEAK", "• Stop leak placeholder.",
            "Small Spill\tLarge Spill", "• Absorb placeholder that\t• Dike placeholder far", "wraps here.\tahead of spill.",
            "FIRST AID", "Refer to the “General First Aid” section.",
            "\tIn Canada, an Emergency Response Assistance Plan (ERAP) may be required for this product.",
            "\tPlease consult the shipping paper.", "\tERG 2024 Page 199"
        });
        var blank = new ErgPage(202, new[] { "GUIDE", "902", "Intentionally left blank", "There are no materials that refer to this guide." });
        var general = new ErgPage(150, new[] { "GENERAL FIRST AID", "• Call placeholder.", "• If not breathing:", "– Sub-step placeholder", "continued.", "Page 150 ERG 2024" });

        var r = ErgTextParser.Parse(new[] { general, left, right, blank });
        var g = Assert.Single(r.Data.Guides);
        Assert.Equal("901", g.Number);
        Assert.Equal("Test Liquids - Placeholder", g.Title);
        Assert.Equal(new[] { "902" }, r.Data.BlankGuides);
        var spill = g.Sections[2].Subsections.Single(s => s.Heading == "SPILL OR LEAK");
        Assert.Equal(new[] { "Stop leak placeholder.", "Small Spill", "Absorb placeholder that wraps here.", "Large Spill", "Dike placeholder far ahead of spill." }, spill.Items);
        Assert.Equal(new[] { "Refer to the “General First Aid” section." }, g.Sections[2].Subsections.Single(s => s.Heading == "FIRST AID").Items);
        Assert.Equal("In Canada, an Emergency Response Assistance Plan (ERAP) may be required for this product. Please consult the shipping paper.", Assert.Single(g.Notes));
        Assert.Equal(new[] { "Call placeholder.", "If not breathing:", "– Sub-step placeholder continued." }, r.Data.GeneralFirstAid!.Subsections[0].Items);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Table1_distances_centred_on_a_block_of_rows()
    {
        var ids = new ErgPage(30, new[] { "x" }, new[]
        {
            "ID Guide Name of Material", "— — 912 Placeholder blasting agent", "9001 901 Testium A", "9001 901 Testium B", "9002 902 Fakeol",
            "9003 903 Mockane", "9003 903 Mockane, solution", "9004 901 Long name"
        });
        var t1 = new ErgPage(300, new[]
        {
            "TABLE 1 - INITIAL ISOLATION AND PROTECTIVE ACTION DISTANCES",
            "No. No. Name of Material Meters (Feet) Kilometers (Miles)",
            "9001 901 Testium A",
            "30 m (100 ft) 0.1 km (0.1 mi) 0.2 km (0.1 mi) 60 m (200 ft) 0.3 km (0.2 mi) 0.7 km (0.4 mi)",
            "9001 901 Testium B",
            "9002 902 Fakeol 30 m (100 ft) 0.1 km (0.1 mi) 0.2 km (0.1 mi) Refer to Table 3",
            "9003 903 Mockane",
            "9003 903 Mockane, solution",
            "100 m (300 ft) 0.5 km (0.3 mi) 1.5 km (0.9 mi) 400 m (1250 ft) 2.0 km (1.2 mi) 4.0 km (2.5 mi)",
            "9003 903 Mockane, solution",
            "(Inhalation Hazard Zone A)",
            "9004 901 Long name continuing",
            "30 m (100 ft) 0.1 km (0.1 mi) 0.1 km (0.1 mi) 30 m (100 ft) 0.2 km (0.1 mi) 0.3 km (0.2 mi)",
            "(when spilled in water)",
            "See Next Page for Table 2 - Water-Reactive Materials"
        });
        var t2 = new ErgPage(337, new[] { "TABLE 2 - WATER-REACTIVE MATERIALS WHICH PRODUCE TOXIC GASES", "9004 901 Long name HCl HF" });
        var r = ErgTextParser.Parse(new[] { ids, t1, t2 });
        Assert.Empty(r.Warnings);
        var t = r.Data.IsolationTable;
        Assert.Equal(7, t.Count);
        Assert.Equal("60 m (200 ft)", t.Single(e => e.Name == "Testium B").Large.Isolate);
        Assert.Equal("Refer to Table 3", t.Single(e => e.Name == "Fakeol").Large.Isolate);
        Assert.Equal(3, t.Count(e => e.Small.Isolate == "100 m (300 ft)"));
        Assert.Contains(t, e => e.Name == "Mockane, solution (Inhalation Hazard Zone A)" && e.Small.Isolate == "100 m (300 ft)");
        Assert.Equal("Long name continuing (when spilled in water)", t.Single(e => e.Id == "9004").Name);
        var noId = r.Data.Materials.Single(m => m.Id == "");
        Assert.Equal("—", noId.IdLabel);
        Assert.Equal("HCl HF", r.Data.Materials.Single(m => m.Id == "9004").WaterReactiveGases);
    }
}
