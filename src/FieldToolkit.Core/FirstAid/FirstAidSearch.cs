using System.Globalization;
using System.Text;

namespace FieldToolkit.Core.FirstAid;

public sealed record SearchHit(FirstAidTopic Topic, double Score, string MatchedOn);

/// <summary>
/// Symptom search in plain words. Matches whole phrases from the synonym list and topic keywords first, then individual
/// (stemmed) words in titles, keywords, summaries and warning signs, so "bleeding a lot", "fitting" or "can't breathe"
/// find the right topics.
/// </summary>
public sealed class FirstAidSearch
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "my", "his", "her", "their", "they", "he", "she", "it",
        "someone", "somebody", "person", "patient", "casualty", "has", "have", "had", "of", "to", "in", "on", "at", "and",
        "or", "with", "very", "really", "lot", "lots", "bit", "just", "got", "get", "getting", "keeps", "keep", "what",
        "do", "does", "i", "me", "we", "you", "after", "from", "for", "by", "them", "him", "this", "that", "there", "some"
    };

    private readonly FirstAidLibrary _library;
    private readonly List<(string Phrase, string[] Topics)> _synonyms;
    private readonly Dictionary<string, TopicIndex> _index;

    private sealed class TopicIndex
    {
        public HashSet<string> Title = new();
        public HashSet<string> Keywords = new();
        public HashSet<string> Body = new();
        public List<string> KeywordPhrases = new();
    }

    public FirstAidSearch(FirstAidLibrary library)
    {
        _library = library;
        _synonyms = library.Guide.Synonyms
            .SelectMany(g => g.Phrases.Select(p => (Phrase: Normalize(p), Topics: g.Topics.ToArray())))
            .Where(x => x.Phrase.Length > 0)
            .ToList();
        _index = library.Topics.ToDictionary(t => t.Id, BuildIndex, StringComparer.OrdinalIgnoreCase);
    }

    private static TopicIndex BuildIndex(FirstAidTopic t)
    {
        var idx = new TopicIndex();
        idx.Title.UnionWith(Terms(t.Title));
        foreach (var k in t.Keywords)
        {
            idx.KeywordPhrases.Add(Normalize(k));
            idx.Keywords.UnionWith(Terms(k));
        }
        idx.Body.UnionWith(Terms(t.Summary));
        foreach (var w in t.WarningSigns.Concat(t.CallEmergency.When))
            idx.Body.UnionWith(Terms(w));
        foreach (var s in t.Steps)
            idx.Body.UnionWith(Terms(s.Title));
        return idx;
    }

    /// <summary>Topics ranked by relevance. An empty query returns every topic in display order.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int max = 50)
    {
        var q = Normalize(query);
        if (q.Length == 0)
            return _library.Topics.Select(t => new SearchHit(t, 0, "")).ToList();

        var padded = " " + q + " ";
        var terms = Terms(query).ToList();
        var scores = new Dictionary<string, (double Score, string Why)>(StringComparer.OrdinalIgnoreCase);

        void Add(string id, double score, string why)
        {
            scores.TryGetValue(id, out var cur);
            scores[id] = (cur.Score + score, string.IsNullOrEmpty(cur.Why) ? why : cur.Why);
        }

        foreach (var (phrase, topics) in _synonyms)
            if (padded.Contains(" " + phrase + " ", StringComparison.Ordinal) || (q.Length >= 4 && phrase.StartsWith(q, StringComparison.Ordinal)))
                foreach (var id in topics)
                    Add(id, 100 + phrase.Length, phrase);

        foreach (var t in _library.Topics)
        {
            var idx = _index[t.Id];
            foreach (var kp in idx.KeywordPhrases)
            {
                if (kp.Length == 0)
                    continue;
                if (padded.Contains(" " + kp + " ", StringComparison.Ordinal))
                    Add(t.Id, 60 + kp.Length, kp);
                else if (q.Length >= 3 && kp.StartsWith(q, StringComparison.Ordinal))
                    Add(t.Id, 40, kp);
            }

            var matched = 0;
            foreach (var term in terms)
            {
                var hit = false;
                if (idx.Title.Contains(term)) { Add(t.Id, 30, t.Title); hit = true; }
                if (idx.Keywords.Contains(term)) { Add(t.Id, 15, term); hit = true; }
                if (idx.Body.Contains(term)) { Add(t.Id, 4, term); hit = true; }
                if (!hit && term.Length >= 3 && idx.Title.Any(w => w.StartsWith(term, StringComparison.Ordinal))) { Add(t.Id, 12, t.Title); hit = true; }
                if (hit)
                    matched++;
            }
            // Reward topics that match every word typed over topics that match only one of them.
            if (terms.Count > 1 && matched == terms.Count)
                Add(t.Id, 20, "");
        }

        return scores
            .Select(kv => (Topic: _library.Find(kv.Key), kv.Value))
            .Where(x => x.Topic != null && x.Value.Score > 0)
            .OrderByDescending(x => x.Value.Score)
            .ThenByDescending(x => x.Topic!.PinnedFirst)
            .ThenBy(x => x.Topic!.Order)
            .Take(max)
            .Select(x => new SearchHit(x.Topic!, x.Value.Score, x.Value.Why))
            .ToList();
    }

    /// <summary>Lower case, accents removed, apostrophes dropped ("can't" → "cant"), punctuation to spaces, single-spaced.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            if (ch is '\'' or '’')
                continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Stemmed, stop-word-free search terms.</summary>
    public static IEnumerable<string> Terms(string text) =>
        Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !StopWords.Contains(w))
            .Select(Stem)
            .Where(w => w.Length > 1)
            .Distinct();

    /// <summary>A deliberately small suffix stripper: "bleeding" → "bleed", "fitting" → "fit", "burns" → "burn", "stings" → "sting".</summary>
    public static string Stem(string w)
    {
        if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal))
            w = UnDouble(w[..^3]);
        else if (w.Length > 4 && w.EndsWith("ied", StringComparison.Ordinal))
            w = w[..^3] + "y";
        else if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal))
            w = UnDouble(w[..^2]);
        else if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal))
            w = w[..^3] + "y";
        else if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) && !w.EndsWith("us", StringComparison.Ordinal) && !w.EndsWith("is", StringComparison.Ordinal))
            w = w[..^1];
        return w;
    }

    private static string UnDouble(string w) =>
        w.Length > 2 && w[^1] == w[^2] && !"aeiouls".Contains(w[^1]) ? w[..^1] : w;
}
