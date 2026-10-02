using System.Text.Json.Serialization;

namespace FieldToolkit.Core.Hazmat;

/// <summary>
/// Hazmat data converted from the U.S. DOT Emergency Response Guidebook (ERG) by tools/ErgImport. Text is stored as
/// printed in the source; the app does not rewrite it.
/// </summary>
public sealed class ErgDataset
{
    /// <summary>e.g. "ERG2024".</summary>
    public string Edition { get; set; } = "";

    /// <summary>True for the built-in placeholder dataset. The app shows a SAMPLE DATA warning on every screen.</summary>
    public bool IsSample { get; set; }

    /// <summary>Where the data came from (file name, URL, SHA-256 of the source file).</summary>
    public string Source { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string ImportedOn { get; set; } = "";
    public string ImportTool { get; set; } = "";
    public string Notes { get; set; } = "";

    public List<ErgGuide> Guides { get; set; } = new();
    public List<ErgMaterial> Materials { get; set; } = new();

    /// <summary>Table 1 – Initial Isolation and Protective Action Distances.</summary>
    public List<ErgIsolationEntry> IsolationTable { get; set; } = new();

    /// <summary>Table of placards and the initial response guide to use on-scene.</summary>
    public List<ErgPlacard> Placards { get; set; } = new();

    [JsonIgnore]
    public string FilePath { get; set; } = "";

    /// <summary>Load or validation problems (shown on the data information screen).</summary>
    [JsonIgnore]
    public List<string> Problems { get; set; } = new();

    [JsonIgnore]
    public bool IsEmpty => Materials.Count == 0;
}

public sealed class ErgGuide
{
    /// <summary>Three-digit guide number, e.g. "128".</summary>
    public string Number { get; set; } = "";

    /// <summary>Guide title as printed, e.g. "FLAMMABLE LIQUIDS (WATER-IMMISCIBLE)".</summary>
    public string Title { get; set; } = "";

    /// <summary>POTENTIAL HAZARDS, PUBLIC SAFETY, EMERGENCY RESPONSE, in printed order.</summary>
    public List<ErgSection> Sections { get; set; } = new();

    /// <summary>Source page(s) in the ERG, for verification.</summary>
    public string Pages { get; set; } = "";
}

public sealed class ErgSection
{
    public string Heading { get; set; } = "";

    /// <summary>e.g. FIRE OR EXPLOSION, HEALTH. A section with text directly under its heading has one subsection with an empty heading.</summary>
    public List<ErgSubsection> Subsections { get; set; } = new();
}

public sealed class ErgSubsection
{
    public string Heading { get; set; } = "";
    public List<string> Items { get; set; } = new();
}

public sealed class ErgMaterial
{
    /// <summary>Four-digit UN/NA identification number, e.g. "1203".</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Guide number without suffix, e.g. "128".</summary>
    public string Guide { get; set; } = "";

    /// <summary>Guide number printed with a "P" (material may polymerize violently).</summary>
    public bool Polymerizable { get; set; }

    /// <summary>Highlighted in the ERG lists: appears in Table 1 (toxic inhalation hazard / isolation distances).</summary>
    public bool Highlighted { get; set; }

    /// <summary>Produces toxic gases when spilled in water (Table 2).</summary>
    public bool WaterReactive { get; set; }

    /// <summary>Hazard class/division if the source provides it (the ERG lists do not; optional extra column).</summary>
    public string HazardClass { get; set; } = "";

    /// <summary>Source page for verification.</summary>
    public string Page { get; set; } = "";

    [JsonIgnore]
    public string GuideLabel => Guide + (Polymerizable ? "P" : "");
}

public sealed class ErgIsolationEntry
{
    public string Id { get; set; } = "";
    public string Guide { get; set; } = "";
    public string Name { get; set; } = "";
    public ErgDistances Small { get; set; } = new();
    public ErgDistances Large { get; set; } = new();

    /// <summary>Footnote or note printed with the entry, e.g. "When spilled in water".</summary>
    public string Note { get; set; } = "";

    public string Page { get; set; } = "";
}

/// <summary>Distances exactly as printed, e.g. "30 m (100 ft)" and "0.2 km (0.1 mi)".</summary>
public sealed class ErgDistances
{
    public string Isolate { get; set; } = "";
    public string ProtectDay { get; set; } = "";
    public string ProtectNight { get; set; } = "";
}

public sealed class ErgPlacard
{
    /// <summary>Placard identifier, e.g. "1.1", "2.3", "3", "DANGEROUS".</summary>
    public string Code { get; set; } = "";

    /// <summary>Text on the placard, e.g. "FLAMMABLE".</summary>
    public string Label { get; set; } = "";

    /// <summary>Hazard class / division name, e.g. "Flammable liquids".</summary>
    public string ClassName { get; set; } = "";

    /// <summary>Initial response guide from the ERG placard table; empty until imported.</summary>
    public string Guide { get; set; } = "";
}
