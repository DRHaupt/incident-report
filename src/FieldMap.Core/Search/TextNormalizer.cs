using System.Globalization;
using System.Text;

namespace FieldMap.Core.Search;

/// <summary>
/// Normalises names and queries so "Lougheed Hwy", "LOUGHEED HIGHWAY" and "lougheed highway" compare equal: lower case,
/// no accents or punctuation, and common Canadian street/facility abbreviations expanded to one canonical form.
/// </summary>
public static class TextNormalizer
{
    private static readonly Dictionary<string, string> Abbreviations = new()
    {
        ["st"] = "street", ["str"] = "street", ["ave"] = "avenue", ["av"] = "avenue", ["rd"] = "road", ["hwy"] = "highway", ["hiway"] = "highway",
        ["blvd"] = "boulevard", ["dr"] = "drive", ["cres"] = "crescent", ["cr"] = "crescent", ["pl"] = "place", ["ct"] = "court", ["crt"] = "court",
        ["ln"] = "lane", ["pkwy"] = "parkway", ["pky"] = "parkway", ["trl"] = "trail", ["tr"] = "trail", ["terr"] = "terrace", ["ter"] = "terrace",
        ["sq"] = "square", ["cir"] = "circle", ["way"] = "way", ["gr"] = "grove", ["hts"] = "heights", ["pt"] = "point", ["rte"] = "route",
        ["mt"] = "mount", ["mtn"] = "mountain", ["ft"] = "fort", ["pk"] = "park", ["lk"] = "lake", ["crk"] = "creek",
        ["n"] = "north", ["s"] = "south", ["e"] = "east", ["w"] = "west", ["ne"] = "northeast", ["nw"] = "northwest", ["se"] = "southeast", ["sw"] = "southwest",
        ["fsr"] = "forest service road", ["gen"] = "general", ["hosp"] = "hospital", ["elem"] = "elementary", ["sec"] = "secondary", ["sch"] = "school",
        ["ctr"] = "centre", ["cntr"] = "centre", ["center"] = "centre", ["theater"] = "theatre", ["intl"] = "international", ["univ"] = "university",
        ["govt"] = "government", ["dept"] = "department", ["stn"] = "station", ["no"] = "number", ["defib"] = "defibrillator",
        ["first"] = "1st", ["second"] = "2nd", ["third"] = "3rd", ["fourth"] = "4th", ["fifth"] = "5th", ["sixth"] = "6th", ["seventh"] = "7th",
        ["eighth"] = "8th", ["ninth"] = "9th", ["tenth"] = "10th"
    };

    /// <summary>Normalised text: tokens separated by single spaces.</summary>
    public static string Normalize(string? text) => string.Join(' ', Tokens(text));

    public static List<string> Tokens(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch == '&')
                sb.Append(" and ");
            else if (ch == '\'' || ch == '’')
                continue; // "St. Paul's" → "pauls"
            else
                sb.Append(' ');
        }
        var raw = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < raw.Length; i++)
        {
            var t = raw[i];
            // "St" before a name is Saint ("St Paul's Hospital"); elsewhere it is Street.
            if (t == "st" && i == 0 && raw.Length > 1 && !char.IsDigit(raw[1][0]) && raw[1] is not ("and" or "at"))
                t = "saint";
            else if (t == "ste" && i == 0 && raw.Length > 1)
                t = "sainte";
            else if (Abbreviations.TryGetValue(t, out var full))
                t = full;
            tokens.AddRange(t.Split(' '));
        }
        return tokens;
    }

    /// <summary>Trigrams of each token of at least three characters (used for the fuzzy full-text query).</summary>
    public static IEnumerable<string> Trigrams(string normalized)
    {
        var seen = new HashSet<string>();
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            for (var i = 0; i + 3 <= token.Length; i++)
                if (seen.Add(token.Substring(i, 3)))
                    yield return token.Substring(i, 3);
    }

    /// <summary>Damerau–Levenshtein (optimal string alignment) distance.</summary>
    public static int EditDistance(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>0–1 similarity of two single tokens; a query token that is a prefix of the name token (≥ 3 letters) counts fully.</summary>
    public static double TokenSimilarity(string query, string name)
    {
        if (query == name) return 1;
        if (query.Length >= 3 && name.StartsWith(query, StringComparison.Ordinal)) return 0.95;
        if (query.All(char.IsDigit) || name.All(char.IsDigit))
            return 0; // numbers must match exactly
        var dist = EditDistance(query, name.Length > query.Length + 2 ? name[..(query.Length + 2)] : name);
        var max = Math.Max(query.Length, Math.Min(name.Length, query.Length + 2));
        var sim = 1 - (double)dist / max;
        // Allow about one typo per four letters.
        return dist <= Math.Max(1, query.Length / 4) ? Math.Max(sim, 0.6) : sim * 0.5;
    }

    /// <summary>How well a query matches a name (0–1): every query token is matched to its best name token.</summary>
    public static double Similarity(string normQuery, string normName)
    {
        var q = normQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var n = normName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (q.Length == 0 || n.Length == 0)
            return 0;
        var total = 0.0;
        foreach (var qt in q)
            total += n.Max(nt => TokenSimilarity(qt, nt));
        var score = total / q.Length;
        // Prefer names without many extra words ("Kamloops" over "Kamloops Indian Band Office").
        var extra = Math.Max(0, n.Length - q.Length);
        return score - 0.03 * extra;
    }
}
