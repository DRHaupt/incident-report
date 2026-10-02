using System.Globalization;
using IncidentReporting.Models;

namespace IncidentReporting.Services;

/// <summary>Turns stored field values into display text and validates required fields.</summary>
public static class ReportFormatter
{
    public static string Display(FieldDefinition field, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var inv = CultureInfo.InvariantCulture;
        return field.Type switch
        {
            FieldType.Date when DateTime.TryParseExact(raw, FieldFormats.Date, inv, DateTimeStyles.None, out var d)
                => d.ToShortDateString(),
            FieldType.Time when DateTime.TryParseExact(raw, FieldFormats.Time, inv, DateTimeStyles.None, out var t)
                => t.ToShortTimeString(),
            FieldType.DateTime when DateTime.TryParseExact(raw, FieldFormats.DateTime, inv, DateTimeStyles.None, out var dt)
                => $"{dt.ToShortDateString()} {dt.ToShortTimeString()}",
            FieldType.CheckBox => raw == "Yes" ? "Yes" : "",
            _ => raw
        };
    }

    /// <summary>Returns the labels of required fields that are empty.</summary>
    public static List<string> MissingRequired(FormDefinition form, IncidentReport report) =>
        form.Sections
            .SelectMany(s => s.Fields.Select(f => (Section: s, Field: f)))
            .Where(x => x.Field.Required && string.IsNullOrWhiteSpace(report.Get(x.Field.Key)))
            .Select(x => $"{x.Section.Title}: {x.Field.Label}")
            .ToList();

    public static string FormName(string code) => Templates.FormCatalog.Find(code)?.Name ?? code;

    public static string Category(string code) => Templates.FormCatalog.Find(code)?.Category ?? "";
}
