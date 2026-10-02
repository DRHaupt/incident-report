namespace FieldToolkit.Core.Hazmat;

/// <summary>
/// The U.S. DOT hazard classes and divisions shown by the placard picker. The initial response guide for each placard
/// is NOT built in: it comes from the ERG's placard table via the importer (see tools/ErgImport/placards.csv).
/// </summary>
public static class ErgPlacards
{
    public static List<ErgPlacard> StandardClasses() => new()
    {
        new() { Code = "1", Label = "EXPLOSIVES", ClassName = "Class 1 – Explosives" },
        new() { Code = "2.1", Label = "FLAMMABLE GAS", ClassName = "Division 2.1 – Flammable gas" },
        new() { Code = "2.2", Label = "NON-FLAMMABLE GAS", ClassName = "Division 2.2 – Non-flammable, non-poisonous gas" },
        new() { Code = "2.3", Label = "INHALATION HAZARD", ClassName = "Division 2.3 – Poison gas" },
        new() { Code = "3", Label = "FLAMMABLE", ClassName = "Class 3 – Flammable liquid" },
        new() { Code = "4.1", Label = "FLAMMABLE SOLID", ClassName = "Division 4.1 – Flammable solid" },
        new() { Code = "4.2", Label = "SPONTANEOUSLY COMBUSTIBLE", ClassName = "Division 4.2 – Spontaneously combustible" },
        new() { Code = "4.3", Label = "DANGEROUS WHEN WET", ClassName = "Division 4.3 – Dangerous when wet" },
        new() { Code = "5.1", Label = "OXIDIZER", ClassName = "Division 5.1 – Oxidizer" },
        new() { Code = "5.2", Label = "ORGANIC PEROXIDE", ClassName = "Division 5.2 – Organic peroxide" },
        new() { Code = "6.1", Label = "POISON", ClassName = "Division 6.1 – Poison (toxic)" },
        new() { Code = "7", Label = "RADIOACTIVE", ClassName = "Class 7 – Radioactive" },
        new() { Code = "8", Label = "CORROSIVE", ClassName = "Class 8 – Corrosive" },
        new() { Code = "9", Label = "CLASS 9", ClassName = "Class 9 – Miscellaneous" }
    };

    /// <summary>Applies "code,guide" rows (from placards.csv) to the placard list. Returns problems found.</summary>
    public static List<string> ApplyGuides(List<ErgPlacard> placards, IEnumerable<string> csvLines)
    {
        var problems = new List<string>();
        var lineNo = 0;
        foreach (var raw in csvLines)
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("code", StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = line.Split(',');
            if (parts.Length < 2 || parts[1].Trim().Length != 3 || !parts[1].Trim().All(char.IsAsciiDigit))
            {
                problems.Add($"placards.csv line {lineNo}: expected 'code,guide' with a three-digit guide: \"{line}\"");
                continue;
            }
            var code = parts[0].Trim();
            var placard = placards.FirstOrDefault(p => p.Code == code);
            if (placard == null)
                placards.Add(placard = new ErgPlacard { Code = code, Label = parts.Length > 2 ? parts[2].Trim() : code, ClassName = parts.Length > 2 ? parts[2].Trim() : code });
            placard.Guide = parts[1].Trim();
        }
        return problems;
    }
}
