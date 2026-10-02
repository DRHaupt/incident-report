namespace FieldToolkit.Core.FirstAid;

/// <summary>CPR helper settings from <c>_cpr.json</c>: rates, ratios and per-age guidance (clinical values are data, not code).</summary>
public sealed class CprGuide
{
    public int MinRate { get; set; } = 100;
    public int MaxRate { get; set; } = 120;
    public int DefaultRate { get; set; } = 110;

    /// <summary>How long the helper waits for rescue breaths before prompting compressions again.</summary>
    public int BreathPauseSeconds { get; set; } = 6;

    /// <summary>Prompt to swap compressors (and follow AED prompts) every N minutes.</summary>
    public int SwitchEveryMinutes { get; set; } = 2;

    public List<CprPreset> Presets { get; set; } = new();
    public List<ContentSource> Sources { get; set; } = new();
    public ReviewRecord Review { get; set; } = new();

    public CprPreset? Preset(string id) => Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class CprPreset
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Depth { get; set; } = "";
    public string HandPosition { get; set; } = "";

    /// <summary>"30:2", "15:2" or "continuous".</summary>
    public List<string> Ratios { get; set; } = new();
    public string DefaultRatio { get; set; } = "30:2";

    /// <summary>Notes per ratio, e.g. "15:2": "Two trained rescuers".</summary>
    public Dictionary<string, string> RatioNotes { get; set; } = new();

    /// <summary>Topic to open for full instructions.</summary>
    public string Topic { get; set; } = "";

    /// <summary>Parses "30:2" → (30, 2); "continuous" → (0, 0).</summary>
    public static (int Compressions, int Breaths) ParseRatio(string ratio)
    {
        var parts = ratio.Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out var c) && int.TryParse(parts[1], out var b) && c > 0 && b > 0
            ? (c, b)
            : (0, 0);
    }
}
