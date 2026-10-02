namespace IncidentReporting.Models;

public enum FieldType
{
    Text,
    MultiLine,
    Number,
    Date,
    Time,
    DateTime,
    Choice,
    MultiChoice,
    YesNo,
    CheckBox,
    Phone,
    Email,
    Signature,
    ReportLink
}

public sealed class FieldDefinition
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public FieldType Type { get; init; } = FieldType.Text;
    public bool Required { get; init; }
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    public string? Hint { get; init; }
}

public sealed class SectionDefinition
{
    public required string Title { get; init; }
    public List<FieldDefinition> Fields { get; } = new();
}

public sealed class FormDefinition
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string Description { get; init; } = "";
    public List<SectionDefinition> Sections { get; } = new();

    public IEnumerable<FieldDefinition> AllFields => Sections.SelectMany(s => s.Fields);
}
