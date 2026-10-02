using System.Text;
using System.Text.Json.Serialization;

namespace FieldToolkit.Core.Checklists;

/// <summary>A reusable checklist (vehicle pre-trip, kit check, shift handover…) defined by the user.</summary>
public sealed class ChecklistTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";

    /// <summary>Asked when a run starts, e.g. "Vehicle / call sign" or "Kit bag number". Empty = not asked.</summary>
    public string ReferencePrompt { get; set; } = "";

    public List<ChecklistLine> Lines { get; set; } = new();

    [JsonIgnore]
    public int ItemCount => Lines.Count(l => !l.Heading);
}

public sealed class ChecklistLine
{
    public string Text { get; set; } = "";

    /// <summary>A section heading rather than an item to check.</summary>
    public bool Heading { get; set; }
}

public enum CheckState
{
    Open,
    Ok,
    Fault,
    NotApplicable
}

/// <summary>One use of a checklist: who, when, against what, and the result of each item with its time.</summary>
public sealed class ChecklistRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public string Name { get; set; } = "";
    public string Reference { get; set; } = "";
    public string CheckedBy { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string Notes { get; set; } = "";
    public List<RunLine> Lines { get; set; } = new();

    [JsonIgnore]
    public IEnumerable<RunLine> Items => Lines.Where(l => !l.Heading);

    [JsonIgnore]
    public int Total => Items.Count();

    [JsonIgnore]
    public int Done => Items.Count(l => l.State != CheckState.Open);

    [JsonIgnore]
    public int Faults => Items.Count(l => l.State == CheckState.Fault);

    [JsonIgnore]
    public bool IsComplete => CompletedUtc.HasValue;

    public static ChecklistRun Start(ChecklistTemplate t, string checkedBy, string reference, DateTime nowUtc) => new()
    {
        TemplateId = t.Id,
        Name = t.Name,
        Reference = reference,
        CheckedBy = checkedBy,
        StartedUtc = nowUtc,
        Lines = t.Lines.Select(l => new RunLine { Text = l.Text, Heading = l.Heading }).ToList()
    };

    /// <summary>Sets an item's result and time. Tapping the current state again clears it.</summary>
    public void Set(RunLine line, CheckState state, DateTime nowUtc, string? note = null)
    {
        if (line.Heading)
            return;
        if (line.State == state && note == null)
        {
            line.State = CheckState.Open;
            line.CheckedUtc = null;
            line.Note = "";
            return;
        }
        line.State = state;
        line.CheckedUtc = nowUtc;
        if (note != null)
            line.Note = note;
        else if (state != CheckState.Fault)
            line.Note = "";
    }

    /// <summary>Plain-text report for copying into a message, log or handover.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine((Name + (Reference.Length > 0 ? " – " + Reference : "")).ToUpperInvariant());
        sb.Append($"Started {StartedUtc.ToLocalTime():HH:mm dd MMM yyyy}");
        if (CompletedUtc is { } done)
            sb.Append($" · completed {done.ToLocalTime():HH:mm}");
        if (CheckedBy.Length > 0)
            sb.Append($" · by {CheckedBy}");
        sb.AppendLine();
        var na = Items.Count(l => l.State == CheckState.NotApplicable);
        var ok = Items.Count(l => l.State == CheckState.Ok);
        var open = Total - Done;
        sb.AppendLine($"Result: {ok} OK, {Faults} fault{(Faults == 1 ? "" : "s")}, {na} N/A" + (open > 0 ? $", {open} NOT CHECKED" : ""));
        foreach (var l in Lines)
        {
            if (l.Heading)
            {
                sb.AppendLine($"--- {l.Text}");
                continue;
            }
            var mark = l.State switch
            {
                CheckState.Ok => "[OK]   ",
                CheckState.Fault => "[FAULT]",
                CheckState.NotApplicable => "[N/A]  ",
                _ => "[ ]    "
            };
            sb.Append($"{mark} {l.Text}");
            if (l.CheckedUtc is { } t)
                sb.Append($"  ({t.ToLocalTime():HH:mm})");
            if (l.Note.Length > 0)
                sb.Append($" – {l.Note}");
            sb.AppendLine();
        }
        if (Notes.Length > 0)
            sb.AppendLine("Notes: " + Notes);
        return sb.ToString().TrimEnd();
    }
}

public sealed class RunLine
{
    public string Text { get; set; } = "";
    public bool Heading { get; set; }
    public CheckState State { get; set; }
    public DateTime? CheckedUtc { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>Text form used by the template editor: one item per line, "# " starts a section heading.</summary>
public static class ChecklistText
{
    public static List<ChecklistLine> Parse(string text) =>
        (text ?? "").Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l != "#")
            .Select(l => l.StartsWith('#') ? new ChecklistLine { Text = l.TrimStart('#').Trim(), Heading = true } : new ChecklistLine { Text = l.TrimStart('-', '•', '*').Trim() })
            .Where(l => l.Text.Length > 0)
            .ToList();

    public static string Format(IEnumerable<ChecklistLine> lines) =>
        string.Join(Environment.NewLine, lines.Select(l => l.Heading ? "# " + l.Text : l.Text));
}
