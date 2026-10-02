using IncidentReporting.Models;

namespace IncidentReporting.Templates;

/// <summary>Registry of every incident report template shipped with the application.</summary>
public static partial class FormCatalog
{
    private static readonly Lazy<IReadOnlyList<FormDefinition>> _all = new(BuildAll);

    public static IReadOnlyList<FormDefinition> All => _all.Value;

    public static IEnumerable<string> Categories => All.Select(f => f.Category).Distinct().OrderBy(c => c);

    public static FormDefinition? Find(string code) =>
        All.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<FormDefinition> BuildAll()
    {
        var list = new List<FormDefinition>();
        AddGeneral(list);
        AddHealthAndSafety(list);
        AddMedical(list);
        AddSecurity(list);
        AddVehicle(list);
        AddEnvironmental(list);
        AddFacilities(list);
        AddInformationTechnology(list);
        AddHumanResources(list);
        AddCareAndEducation(list);
        AddHospitalityAndRetail(list);
        return list.OrderBy(f => f.Category).ThenBy(f => f.Name).ToList();
    }

    /// <summary>Creates a builder pre-populated with the standard header.</summary>
    private static FormBuilder Start(string code, string name, string category, string description) =>
        new FormBuilder(code, name, category, description).Include(CommonSections.Header);
}
