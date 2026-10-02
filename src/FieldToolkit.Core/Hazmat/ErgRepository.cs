using System.Text.Json;

namespace FieldToolkit.Core.Hazmat;

public static class ErgRepository
{
    public static ErgDataset Load(string path)
    {
        var data = Json.Deserialize<ErgDataset>(File.ReadAllText(path));
        data.FilePath = path;
        data.Problems.AddRange(Validate(data));
        return data;
    }

    /// <summary>Loads the dataset; a missing or unreadable file gives an empty dataset with the problem recorded.</summary>
    public static ErgDataset LoadOrEmpty(string path)
    {
        try
        {
            if (File.Exists(path))
                return Load(path);
            return new ErgDataset { FilePath = path, Problems = { $"Hazmat data file not found: {path}" } };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ErgDataset { FilePath = path, Problems = { $"Hazmat data file could not be read: {ex.Message}" } };
        }
    }

    public static void Save(ErgDataset data, string path) => Json.WriteFileAtomic(path, Json.Serialize(data));

    /// <summary>Consistency checks run on load and by the importer.</summary>
    public static IEnumerable<string> Validate(ErgDataset d)
    {
        var guides = new HashSet<string>(d.Guides.Select(g => g.Number));
        foreach (var dup in d.Guides.GroupBy(g => g.Number).Where(g => g.Count() > 1))
            yield return $"Guide {dup.Key} appears {dup.Count()} times.";
        foreach (var g in d.Guides.Where(g => g.Sections.Count == 0))
            yield return $"Guide {g.Number} has no sections.";
        foreach (var m in d.Materials)
        {
            if (!IsIdNumber(m.Id))
                yield return $"Material '{m.Name}' has an invalid ID number '{m.Id}'.";
            if (!guides.Contains(m.Guide))
                yield return $"Material {m.Id} '{m.Name}' refers to guide {m.Guide}, which is not in the dataset.";
        }
        var ids = new HashSet<string>(d.Materials.Select(m => m.Id));
        foreach (var e in d.IsolationTable)
        {
            if (!ids.Contains(e.Id))
                yield return $"Table 1 entry {e.Id} '{e.Name}' has no matching material.";
            if (string.IsNullOrWhiteSpace(e.Small.Isolate) || string.IsNullOrWhiteSpace(e.Large.Isolate))
                yield return $"Table 1 entry {e.Id} '{e.Name}' is missing distances.";
        }
        foreach (var p in d.Placards.Where(p => p.Guide.Length > 0 && !guides.Contains(p.Guide)))
            yield return $"Placard {p.Code} refers to guide {p.Guide}, which is not in the dataset.";
    }

    /// <summary>A four-digit identification number (UN or NA numbers are both four digits).</summary>
    public static bool IsIdNumber(string id) => id.Length == 4 && id.All(char.IsAsciiDigit);
}
