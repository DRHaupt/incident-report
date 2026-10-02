using System.Net;
using System.Text;
using IncidentReporting.Models;
using IncidentReporting.Templates;

namespace IncidentReporting.Services;

public static class ExportService
{
    public static void ExportListToCsv(IEnumerable<IncidentReport> reports, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[]
        {
            "Report Number", "Form", "Category", "Status", "Title", "Incident Date", "Severity", "Location",
            "Department", "Reported By", "Created", "Last Modified"
        }.Select(Csv)));

        foreach (var r in reports)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                r.ReportNumber, ReportFormatter.FormName(r.FormCode), ReportFormatter.Category(r.FormCode), r.Status.ToDisplay(),
                r.Title, r.IncidentDate?.ToShortDateString() ?? "", r.Severity, r.Location, r.Department, r.ReportedBy,
                r.CreatedUtc.ToLocalTime().ToString("g"), r.ModifiedUtc.ToLocalTime().ToString("g")
            }.Select(Csv)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Exports every field of a single report as a two-column CSV.</summary>
    public static void ExportReportToCsv(IncidentReport report, string path)
    {
        var form = FormCatalog.Find(report.FormCode);
        var sb = new StringBuilder();
        sb.AppendLine("Section,Field,Value");
        sb.AppendLine($"Report,Report Number,{Csv(report.ReportNumber)}");
        sb.AppendLine($"Report,Form,{Csv(ReportFormatter.FormName(report.FormCode))}");
        sb.AppendLine($"Report,Status,{Csv(report.Status.ToDisplay())}");
        if (form != null)
        {
            foreach (var s in form.Sections)
                foreach (var f in s.Fields)
                    sb.AppendLine($"{Csv(s.Title)},{Csv(f.Label)},{Csv(ReportFormatter.Display(f, report.Get(f.Key)))}");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static string BuildHtml(IncidentReport report, string organization)
    {
        var form = FormCatalog.Find(report.FormCode);
        var sb = new StringBuilder();
        string H(string s) => WebUtility.HtmlEncode(s);

        sb.Append("""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><meta http-equiv="X-UA-Compatible" content="IE=edge">
            <title>{{TITLE}}</title>
            <style>
            body{font-family:Segoe UI,Arial,sans-serif;font-size:10pt;color:#000;margin:24px;}
            .hdr{border-bottom:2px solid #000;padding-bottom:6px;margin-bottom:12px;display:flex;justify-content:space-between;}
            .org{font-size:9pt;color:#444;}
            .logo{float:left;margin:0 10px 0 0;}
            h1{font-size:15pt;margin:0;}
            h2{font-size:10.5pt;background:#e6e6e6;border:1px solid #999;padding:3px 6px;margin:14px 0 0 0;}
            table{border-collapse:collapse;width:100%;}
            td{border:1px solid #999;padding:3px 6px;vertical-align:top;}
            td.l{width:32%;background:#f6f6f6;font-weight:600;}
            .meta td{border:none;padding:1px 6px 1px 0;}
            .foot{margin-top:18px;font-size:8pt;color:#555;border-top:1px solid #999;padding-top:4px;}
            </style></head><body>
            """);
        sb.Replace("{{TITLE}}", H(report.ReportNumber));
        sb.Append($"<div class=\"hdr\"><div><img class=\"logo\" src=\"{Branding.LogoDataUri(48)}\" width=\"40\" height=\"40\" alt=\"\">");
        if (!string.IsNullOrWhiteSpace(organization))
            sb.Append($"<div class=\"org\">{H(organization)}</div>");
        sb.Append($"<h1>{H(ReportFormatter.FormName(report.FormCode))}</h1></div>");
        sb.Append("<table class=\"meta\" style=\"width:auto\">");
        sb.Append($"<tr><td><b>Report No.</b></td><td>{H(report.ReportNumber)}</td></tr>");
        sb.Append($"<tr><td><b>Form</b></td><td>{H(report.FormCode)}</td></tr>");
        sb.Append($"<tr><td><b>Status</b></td><td>{H(report.Status.ToDisplay())}</td></tr>");
        sb.Append("</table></div>");

        if (form != null)
        {
            foreach (var section in form.Sections)
            {
                sb.Append($"<h2>{H(section.Title)}</h2><table>");
                foreach (var f in section.Fields)
                {
                    var value = H(ReportFormatter.Display(f, report.Get(f.Key))).Replace("\r\n", "<br>").Replace("\n", "<br>");
                    sb.Append($"<tr><td class=\"l\">{H(f.Label)}</td><td>{value}</td></tr>");
                }
                sb.Append("</table>");
            }
        }

        sb.Append($"<div class=\"foot\">Created {H(report.CreatedUtc.ToLocalTime().ToString("g"))} &middot; " +
                  $"Last modified {H(report.ModifiedUtc.ToLocalTime().ToString("g"))} &middot; " +
                  $"Printed {H(DateTime.Now.ToString("g"))}</div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Csv(string? value)
    {
        value ??= "";
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
