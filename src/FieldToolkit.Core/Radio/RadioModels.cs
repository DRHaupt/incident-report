using System.Text;

namespace FieldToolkit.Core.Radio;

/// <summary>Editable reference text from Content\radio\reference.json.</summary>
public sealed class RadioReference
{
    public string Source { get; set; } = "";
    public List<Proword> Prowords { get; set; } = new();
    public List<ReportFormat> Formats { get; set; } = new();
    public List<string> Tips { get; set; } = new();

    public static RadioReference LoadOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? Json.Deserialize<RadioReference>(File.ReadAllText(path)) : new RadioReference();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return new RadioReference { Tips = { "reference.json could not be read: " + ex.Message } };
        }
    }
}

public sealed class Proword
{
    public string Word { get; set; } = "";
    public string Meaning { get; set; } = "";

    /// <summary>Grouping for display, e.g. "Procedure", "Signal reports", "Emergency".</summary>
    public string Group { get; set; } = "";
}

/// <summary>A structured message such as a major-incident report (each line keyed by a letter).</summary>
public sealed class ReportFormat
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public List<ReportLine> Lines { get; set; } = new();
}

public sealed class ReportLine
{
    public string Key { get; set; } = "";
    public string Meaning { get; set; } = "";
}

/// <summary>One row of the user's channel plan.</summary>
public sealed class RadioChannel
{
    public string Channel { get; set; } = "";
    public string Name { get; set; } = "";
    public string RxFrequency { get; set; } = "";
    public string TxFrequency { get; set; } = "";
    public string Tone { get; set; } = "";
    public string Use { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>One row of the user's call-sign list.</summary>
public sealed class CallSign
{
    public string Sign { get; set; } = "";
    public string Who { get; set; } = "";
    public string Location { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Notes { get; set; } = "";
}

/// <summary>A time-stamped radio log / notepad entry.</summary>
public sealed class LogEntry
{
    public DateTime TimeUtc { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Text { get; set; } = "";

    public string Format() =>
        $"{TimeUtc.ToLocalTime():HH:mm:ss}  " +
        (From.Length > 0 || To.Length > 0 ? $"{(From.Length > 0 ? From : "?")} → {(To.Length > 0 ? To : "?")}: " : "") +
        Text;

    public static string FormatLog(IEnumerable<LogEntry> entries, string title)
    {
        var list = entries.OrderBy(e => e.TimeUtc).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(title);
        if (list.Count > 0)
            sb.AppendLine($"{list[0].TimeUtc.ToLocalTime():dd MMM yyyy HH:mm} – {list[^1].TimeUtc.ToLocalTime():dd MMM yyyy HH:mm}, {list.Count} entries");
        foreach (var e in list)
            sb.AppendLine(e.Format());
        return sb.ToString().TrimEnd();
    }
}
