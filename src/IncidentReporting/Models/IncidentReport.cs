using System.Globalization;

namespace IncidentReporting.Models;

public enum ReportStatus
{
    Draft,
    Submitted,
    UnderReview,
    Closed
}

public static class ReportStatusText
{
    public static string ToDisplay(this ReportStatus status) => status switch
    {
        ReportStatus.UnderReview => "Under Review",
        _ => status.ToString()
    };
}

public sealed class IncidentReport
{
    public long Id { get; set; }
    public string ReportNumber { get; set; } = "";
    public string FormCode { get; set; } = "";
    public ReportStatus Status { get; set; } = ReportStatus.Draft;
    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }

    /// <summary>All field values keyed by FieldDefinition.Key. Multi-choice values are stored "; "-separated.</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Get(string key) => Values.TryGetValue(key, out var v) ? v : "";

    // Common header fields are promoted to columns for listing and searching.
    public string Title => Get(CommonKeys.Title);
    public string Severity => Get(CommonKeys.Severity);
    public string Location => Get(CommonKeys.Location);
    public string ReportedBy => Get(CommonKeys.ReporterName);
    public string Department => Get(CommonKeys.Department);

    public DateTime? IncidentDate =>
        DateTime.TryParseExact(Get(CommonKeys.IncidentDate), FieldFormats.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
}

public static class CommonKeys
{
    public const string Title = "title";
    public const string IncidentDate = "incident_date";
    public const string IncidentTime = "incident_time";
    public const string Severity = "severity";
    public const string Location = "location";
    public const string Department = "department";
    public const string ReporterName = "reporter_name";
}

/// <summary>Invariant storage formats for date/time field values.</summary>
public static class FieldFormats
{
    public const string Date = "yyyy-MM-dd";
    public const string Time = "HH:mm";
    public const string DateTime = "yyyy-MM-dd HH:mm";
    public const string ListSeparator = "; ";
}
