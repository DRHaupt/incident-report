using System.Text.Json;

namespace FieldToolkit.Core.FirstAid;

/// <summary>A problem found while loading content. Problems never stop the guide loading; they are listed for the reviewer.</summary>
public sealed record ContentProblem(string File, string Message)
{
    public override string ToString() => $"{File}: {Message}";
}

/// <summary>The first-aid topics loaded from a folder of JSON files, with validation results.</summary>
public sealed class FirstAidLibrary
{
    public const string GuideFileName = "_guide.json";

    private readonly Dictionary<string, FirstAidTopic> _byId;

    private FirstAidLibrary(string folder, FirstAidGuide guide, List<FirstAidTopic> topics, List<ContentProblem> problems)
    {
        Folder = folder;
        Guide = guide;
        Topics = topics;
        Problems = problems;
        _byId = topics.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public string Folder { get; }
    public FirstAidGuide Guide { get; }

    /// <summary>Display order: pinned topics first, then by <c>order</c>, then title.</summary>
    public IReadOnlyList<FirstAidTopic> Topics { get; }

    public IReadOnlyList<ContentProblem> Problems { get; }

    public IEnumerable<string> Categories => Topics.Select(t => t.Category).Distinct();

    public FirstAidTopic? Find(string id) => _byId.TryGetValue(id, out var t) ? t : null;

    /// <summary>Replaces the {EMERGENCY} and {POISON} placeholders with the numbers set in _guide.json.</summary>
    public string Expand(string text) => text
        .Replace("{EMERGENCY}", Guide.EmergencyNumber, StringComparison.Ordinal)
        .Replace("{POISON}", string.IsNullOrWhiteSpace(Guide.PoisonCentre) ? "your poison centre" : Guide.PoisonCentre, StringComparison.Ordinal);

    public static FirstAidLibrary Load(string folder)
    {
        var problems = new List<ContentProblem>();
        var guide = new FirstAidGuide();
        var topics = new List<FirstAidTopic>();

        if (!Directory.Exists(folder))
        {
            problems.Add(new ContentProblem(folder, "Content folder not found."));
            return new FirstAidLibrary(folder, guide, topics, problems);
        }

        var guidePath = Path.Combine(folder, GuideFileName);
        if (File.Exists(guidePath))
        {
            try
            {
                guide = Json.Deserialize<FirstAidGuide>(File.ReadAllText(guidePath));
            }
            catch (JsonException ex)
            {
                problems.Add(new ContentProblem(GuideFileName, "Invalid JSON: " + ex.Message));
            }
        }
        else
        {
            problems.Add(new ContentProblem(GuideFileName, "Missing. The disclaimer and review record can't be shown."));
        }

        foreach (var path in Directory.GetFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('_'))
                continue;
            try
            {
                var topic = Json.Deserialize<FirstAidTopic>(File.ReadAllText(path));
                topic.SourceFile = name;
                topics.Add(topic);
            }
            catch (JsonException ex)
            {
                problems.Add(new ContentProblem(name, "Invalid JSON, topic skipped: " + ex.Message));
            }
        }

        topics = topics
            .OrderByDescending(t => t.PinnedFirst)
            .ThenBy(t => t.Order)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        problems.AddRange(Validate(guide, topics));
        return new FirstAidLibrary(folder, guide, topics, problems);
    }

    /// <summary>Checks the rules every topic must meet (see docs/CONTENT-GUIDE.md).</summary>
    public static IEnumerable<ContentProblem> Validate(FirstAidGuide guide, IReadOnlyList<FirstAidTopic> topics)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in topics)
        {
            var f = string.IsNullOrEmpty(t.SourceFile) ? t.Id : t.SourceFile;
            if (string.IsNullOrWhiteSpace(t.Id))
                yield return new ContentProblem(f, "Missing id.");
            else if (!ids.Add(t.Id))
                yield return new ContentProblem(f, $"Duplicate id '{t.Id}'.");
            else if (!string.IsNullOrEmpty(t.SourceFile) && !string.Equals(Path.GetFileNameWithoutExtension(t.SourceFile), t.Id, StringComparison.OrdinalIgnoreCase))
                yield return new ContentProblem(f, $"File name should match the id '{t.Id}'.");
            if (string.IsNullOrWhiteSpace(t.Title))
                yield return new ContentProblem(f, "Missing title.");
            if (t.Steps.Count == 0)
                yield return new ContentProblem(f, "No steps.");
            if (t.CallEmergency.When.Count == 0)
                yield return new ContentProblem(f, "No 'callEmergency.when' guidance.");
            if (t.Sources.Count == 0)
                yield return new ContentProblem(f, "No sources recorded. Every topic must cite the guideline it is based on.");
            foreach (var s in t.Sources.Where(s => string.IsNullOrWhiteSpace(s.Body) || string.IsNullOrWhiteSpace(s.Title)))
                yield return new ContentProblem(f, "A source is missing its body or title.");
            for (var i = 0; i < t.Steps.Count; i++)
            {
                var step = t.Steps[i];
                if (string.IsNullOrWhiteSpace(step.Title) && string.IsNullOrWhiteSpace(step.Text))
                    yield return new ContentProblem(f, $"Step {i + 1} is empty.");
                if (step.Action is { } a)
                {
                    if (string.IsNullOrWhiteSpace(a.Label))
                        yield return new ContentProblem(f, $"Step {i + 1}: action has no label.");
                    if (a.Type == StepActionType.Timer)
                    {
                        if (a.Mode is not ("stopwatch" or "countdown"))
                            yield return new ContentProblem(f, $"Step {i + 1}: timer mode must be 'stopwatch' or 'countdown'.");
                        if (a.Mode == "countdown" && a.Minutes <= 0)
                            yield return new ContentProblem(f, $"Step {i + 1}: countdown needs 'minutes'.");
                    }
                }
            }
        }

        foreach (var t in topics)
        {
            var f = string.IsNullOrEmpty(t.SourceFile) ? t.Id : t.SourceFile;
            foreach (var r in t.Related.Where(r => !ids.Contains(r)))
                yield return new ContentProblem(f, $"Related topic '{r}' does not exist.");
            foreach (var a in t.Steps.Select(s => s.Action).OfType<StepAction>().Where(a => a.Type == StepActionType.Topic && !ids.Contains(a.Target)))
                yield return new ContentProblem(f, $"Step action opens topic '{a.Target}', which does not exist.");
        }

        foreach (var g in guide.Synonyms)
            foreach (var id in g.Topics.Where(id => !ids.Contains(id)))
                yield return new ContentProblem(GuideFileName, $"Synonym group '{string.Join(", ", g.Phrases.Take(2))}' points to unknown topic '{id}'.");
    }
}
