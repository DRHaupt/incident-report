using FieldToolkit.Core.FirstAid;

namespace FieldToolkit.Core.Hazmat;

/// <summary>Lookup by ID number ("1203", "UN1203", "NA 1993"), guide ("guide 128") or material name words.</summary>
public sealed class ErgSearch
{
    private readonly ErgDataset _data;
    private readonly Dictionary<string, ErgGuide> _guides;
    private readonly List<(ErgMaterial Material, string Name)> _names;

    public ErgSearch(ErgDataset data)
    {
        _data = data;
        _guides = data.Guides.GroupBy(g => g.Number).ToDictionary(g => g.Key, g => g.First());
        _names = data.Materials.Select(m => (m, FirstAidSearch.Normalize(m.Name))).ToList();
    }

    public ErgGuide? Guide(string number) => _guides.TryGetValue(StripSuffix(number), out var g) ? g : null;

    /// <summary>All Table 1 rows for a material (some materials have several, e.g. "when spilled in water").</summary>
    public IReadOnlyList<ErgIsolationEntry> Isolation(ErgMaterial m) =>
        _data.IsolationTable.Where(e => e.Id == m.Id && (NamesMatch(e.Name, m.Name) || _data.Materials.Count(x => x.Id == m.Id) == 1)).ToList();

    private static bool NamesMatch(string a, string b)
    {
        var na = FirstAidSearch.Normalize(a);
        var nb = FirstAidSearch.Normalize(b);
        return na.StartsWith(nb, StringComparison.Ordinal) || nb.StartsWith(na, StringComparison.Ordinal);
    }

    public IReadOnlyList<ErgMaterial> Search(string query, int max = 200)
    {
        var raw = (query ?? "").Trim();
        if (raw.Length == 0)
            return Array.Empty<ErgMaterial>();

        var q = FirstAidSearch.Normalize(raw);
        var compact = q.Replace(" ", "");

        // "guide 128" / "g128"
        var guideMatch = System.Text.RegularExpressions.Regex.Match(q, @"^(?:guide|g)\s*(\d{3})p?$");
        if (guideMatch.Success)
            return ByGuide(guideMatch.Groups[1].Value).Take(max).ToList();

        // "UN1203", "na 1993", "1203", partial "120"
        var idMatch = System.Text.RegularExpressions.Regex.Match(compact, @"^(?:un|na|id)?(\d{1,4})$");
        if (idMatch.Success)
        {
            var digits = idMatch.Groups[1].Value;
            return _data.Materials
                .Where(m => m.Id.StartsWith(digits, StringComparison.Ordinal))
                .OrderBy(m => m.Id != digits)
                .ThenBy(m => m.Id, StringComparer.Ordinal)
                .Take(max)
                .ToList();
        }

        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _names
            .Select(x => (x.Material, Score: NameScore(x.Name, q, words)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Material.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Material)
            .ToList();
    }

    private static int NameScore(string name, string q, string[] words)
    {
        if (name == q) return 1000;
        if (name.StartsWith(q, StringComparison.Ordinal)) return 500;
        var nameWords = name.Split(' ');
        if (!words.All(w => nameWords.Any(n => n.StartsWith(w, StringComparison.Ordinal)) || name.Contains(w, StringComparison.Ordinal)))
            return 0;
        return 100 + (nameWords.Any(n => n == words[0]) ? 50 : 0) - Math.Min(name.Length, 90);
    }

    public IEnumerable<ErgMaterial> ByGuide(string guide) =>
        _data.Materials.Where(m => m.Guide == StripSuffix(guide)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Materials for a placard: those whose hazard class matches it, else those using the placard's guide.</summary>
    public IReadOnlyList<ErgMaterial> ByPlacard(ErgPlacard p)
    {
        var byClass = _data.Materials.Where(m => m.HazardClass.Length > 0 && ClassMatches(m.HazardClass, p.Code)).ToList();
        if (byClass.Count > 0)
            return byClass.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return p.Guide.Length > 0 ? ByGuide(p.Guide).ToList() : Array.Empty<ErgMaterial>();
    }

    /// <summary>Class "2" matches divisions 2.1/2.2/2.3; division "2.1" matches only itself.</summary>
    public static bool ClassMatches(string materialClass, string placardCode) =>
        placardCode.Contains('.')
            ? materialClass == placardCode
            : materialClass == placardCode || materialClass.StartsWith(placardCode + ".", StringComparison.Ordinal);

    private static string StripSuffix(string guide) => guide.TrimEnd('P', 'p');
}
