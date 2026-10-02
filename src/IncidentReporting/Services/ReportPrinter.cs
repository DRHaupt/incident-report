using System.Drawing;
using System.Drawing.Printing;
using IncidentReporting.Models;
using IncidentReporting.Templates;

namespace IncidentReporting.Services;

/// <summary>Renders an incident report as a paginated, form-style printout.</summary>
public sealed class ReportPrinter : IDisposable
{
    private sealed record Row(bool IsSection, string Label, string Value);

    private readonly IncidentReport _report;
    private readonly string _organization;
    private readonly bool _includeBlankFields;
    private readonly List<Row> _rows = new();
    private readonly Font _orgFont = new("Segoe UI", 8.5f);
    private readonly Font _titleFont = new("Segoe UI", 14f, FontStyle.Bold);
    private readonly Font _sectionFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    private readonly Font _labelFont = new("Segoe UI", 8.5f, FontStyle.Bold);
    private readonly Font _valueFont = new("Segoe UI", 8.5f);
    private readonly Font _footFont = new("Segoe UI", 7.5f);
    private int _rowIndex;
    private int _page;

    public PrintDocument Document { get; }

    public ReportPrinter(IncidentReport report, string organization, bool includeBlankFields = true)
    {
        _report = report;
        _organization = organization;
        _includeBlankFields = includeBlankFields;
        Document = new PrintDocument { DocumentName = report.ReportNumber };
        Document.DefaultPageSettings.Margins = new Margins(60, 60, 60, 60);
        Document.BeginPrint += (_, _) => { _rowIndex = 0; _page = 0; };
        Document.PrintPage += OnPrintPage;
        BuildRows();
    }

    private void BuildRows()
    {
        var form = FormCatalog.Find(_report.FormCode);
        if (form == null)
            return;
        foreach (var section in form.Sections)
        {
            var fields = section.Fields
                .Select(f => new Row(false, f.Label, ReportFormatter.Display(f, _report.Get(f.Key))))
                .Where(r => _includeBlankFields || r.Value.Length > 0)
                .ToList();
            if (fields.Count == 0)
                continue;
            _rows.Add(new Row(true, section.Title, ""));
            _rows.AddRange(fields);
        }
    }

    private void OnPrintPage(object? sender, PrintPageEventArgs e)
    {
        var g = e.Graphics!;
        var bounds = e.MarginBounds;
        _page++;
        float y = bounds.Top;
        using var pen = new Pen(Color.FromArgb(140, 140, 140), 0.5f);
        using var shade = new SolidBrush(Color.FromArgb(232, 232, 232));
        using var labelShade = new SolidBrush(Color.FromArgb(246, 246, 246));

        // Page header: logo, organization, form title and report number.
        const float logoSize = 40f;
        g.DrawImage(Branding.Logo48, bounds.Left, y, logoSize, logoSize);
        float textLeft = bounds.Left + logoSize + 10;
        float headerTop = y;
        if (!string.IsNullOrWhiteSpace(_organization))
        {
            g.DrawString(_organization, _orgFont, Brushes.DimGray, textLeft, y);
            y += _orgFont.GetHeight(g) + 1;
        }
        g.DrawString(ReportFormatter.FormName(_report.FormCode), _titleFont, Brushes.Black, textLeft, y);
        var meta = $"Report No. {_report.ReportNumber}    Status: {_report.Status.ToDisplay()}";
        var metaSize = g.MeasureString(meta, _labelFont);
        g.DrawString(meta, _labelFont, Brushes.Black, bounds.Right - metaSize.Width, y + 6);
        y = Math.Max(y + _titleFont.GetHeight(g), headerTop + logoSize) + 4;
        g.DrawLine(Pens.Black, bounds.Left, y, bounds.Right, y);
        y += 8;

        var footerTop = bounds.Bottom - _footFont.GetHeight(g) - 4;
        float labelWidth = bounds.Width * 0.33f;
        float valueWidth = bounds.Width - labelWidth;
        const float pad = 4f;

        while (_rowIndex < _rows.Count)
        {
            var row = _rows[_rowIndex];
            if (row.IsSection)
            {
                var h = _sectionFont.GetHeight(g) + pad * 2;
                // Keep a section heading together with at least one following row.
                if (y + h * 2.2f > footerTop && y > bounds.Top + 60)
                    break;
                y += 6;
                var rect = new RectangleF(bounds.Left, y, bounds.Width, h);
                g.FillRectangle(shade, rect);
                g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                g.DrawString(row.Label, _sectionFont, Brushes.Black, rect.X + pad, rect.Y + pad);
                y += h;
            }
            else
            {
                var labelSize = g.MeasureString(row.Label, _labelFont, (int)(labelWidth - pad * 2));
                var valueSize = g.MeasureString(row.Value.Length == 0 ? " " : row.Value, _valueFont, (int)(valueWidth - pad * 2));
                var h = Math.Max(labelSize.Height, valueSize.Height) + pad * 2;
                if (y + h > footerTop && y > bounds.Top + 60)
                    break;
                var labelRect = new RectangleF(bounds.Left, y, labelWidth, h);
                var valueRect = new RectangleF(bounds.Left + labelWidth, y, valueWidth, h);
                g.FillRectangle(labelShade, labelRect);
                g.DrawRectangle(pen, labelRect.X, labelRect.Y, labelRect.Width, labelRect.Height);
                g.DrawRectangle(pen, valueRect.X, valueRect.Y, valueRect.Width, valueRect.Height);
                g.DrawString(row.Label, _labelFont, Brushes.Black,
                    new RectangleF(labelRect.X + pad, labelRect.Y + pad, labelRect.Width - pad * 2, labelRect.Height - pad));
                g.DrawString(row.Value, _valueFont, Brushes.Black,
                    new RectangleF(valueRect.X + pad, valueRect.Y + pad, valueRect.Width - pad * 2, valueRect.Height - pad));
                y += h;
            }
            _rowIndex++;
        }

        // Page footer
        g.DrawLine(pen, bounds.Left, footerTop - 2, bounds.Right, footerTop - 2);
        g.DrawString($"Printed {DateTime.Now:g}", _footFont, Brushes.DimGray, bounds.Left, footerTop);
        var pageText = $"{_report.ReportNumber}  -  Page {_page}";
        var pageSize = g.MeasureString(pageText, _footFont);
        g.DrawString(pageText, _footFont, Brushes.DimGray, bounds.Right - pageSize.Width, footerTop);

        e.HasMorePages = _rowIndex < _rows.Count;
    }

    public void Dispose()
    {
        Document.Dispose();
        _orgFont.Dispose();
        _titleFont.Dispose();
        _sectionFont.Dispose();
        _labelFont.Dispose();
        _valueFont.Dispose();
        _footFont.Dispose();
    }
}
