using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace FieldMap.Core.Data;

/// <summary>
/// Reads the cell values of an Excel workbook (.xlsx, Office Open XML) without Excel or a third-party library: shared
/// strings, inline strings, numbers and booleans. Formulas give their cached value. Enough for importing lists.
/// </summary>
public static class XlsxReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<string> SheetNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return Sheets(zip).Select(s => s.Name).ToList();
    }

    /// <summary>Rows of a sheet (the first if <paramref name="sheet"/> is null) as header → value, like <see cref="CsvStream.Rows"/>.</summary>
    public static List<Dictionary<string, string>> Rows(string path, string? sheet = null)
    {
        using var zip = ZipFile.OpenRead(path);
        var sheets = Sheets(zip);
        if (sheets.Count == 0)
            throw new InvalidDataException("The workbook has no sheets.");
        var target = sheet == null ? sheets[0] : sheets.FirstOrDefault(s => s.Name.Equals(sheet, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"No sheet named '{sheet}'.");
        var shared = SharedStrings(zip);
        var entry = zip.GetEntry(target.Part) ?? throw new InvalidDataException($"Sheet '{target.Name}' is missing from the file.");
        XDocument doc;
        using (var s = entry.Open())
            doc = XDocument.Load(s);

        var records = new List<List<string>>();
        foreach (var row in doc.Descendants(Main + "row"))
        {
            var cells = new List<string>();
            foreach (var c in row.Elements(Main + "c"))
            {
                var col = ColumnIndex(c.Attribute("r")?.Value) ?? cells.Count;
                while (cells.Count < col)
                    cells.Add("");
                cells.Add(CellText(c, shared));
            }
            records.Add(cells);
        }
        var header = records.FirstOrDefault(r => r.Any(v => v.Trim().Length > 0));
        if (header == null)
            return new List<Dictionary<string, string>>();
        var names = header.Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var list = new List<Dictionary<string, string>>();
        foreach (var r in records.SkipWhile(r => r != header).Skip(1))
        {
            if (r.All(string.IsNullOrWhiteSpace))
                continue;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < names.Length && i < r.Count; i++)
                if (names[i].Length > 0)
                    d[names[i]] = r[i];
            list.Add(d);
        }
        return list;
    }

    private sealed record SheetInfo(string Name, string Part);

    private static List<SheetInfo> Sheets(ZipArchive zip)
    {
        var wb = Load(zip, "xl/workbook.xml") ?? throw new InvalidDataException("Not an Excel workbook (.xlsx).");
        var rels = Load(zip, "xl/_rels/workbook.xml.rels");
        var targets = rels?.Root?.Elements(PkgRel + "Relationship").ToDictionary(r => r.Attribute("Id")!.Value, r => r.Attribute("Target")!.Value) ?? new();
        var list = new List<SheetInfo>();
        foreach (var s in wb.Descendants(Main + "sheet"))
        {
            var id = s.Attribute(Rel + "id")?.Value;
            if (id == null || !targets.TryGetValue(id, out var target))
                continue;
            target = target.TrimStart('/');
            var part = target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target;
            list.Add(new SheetInfo(s.Attribute("name")?.Value ?? part, part));
        }
        return list;
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var doc = Load(zip, "xl/sharedStrings.xml");
        if (doc == null)
            return new List<string>();
        // Each <si> is plain <t> or rich text runs <r><t>.
        return doc.Root!.Elements(Main + "si").Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList();
    }

    private static XDocument? Load(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e == null)
            return null;
        using var s = e.Open();
        return XDocument.Load(s);
    }

    private static string CellText(XElement c, List<string> shared)
    {
        var type = c.Attribute("t")?.Value;
        var v = c.Element(Main + "v")?.Value;
        switch (type)
        {
            case "s":
                return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Count ? shared[i] : "";
            case "inlineStr":
                return string.Concat(c.Descendants(Main + "t").Select(t => t.Value));
            case "b":
                return v == "1" ? "TRUE" : "FALSE";
            case "str" or "e":
                return v ?? "";
            default:
                // Numbers keep full precision (coordinates); whole numbers lose the ".0".
                if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d.ToString("R", CultureInfo.InvariantCulture);
                return v ?? "";
        }
    }

    /// <summary>"C12" → 2.</summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return null;
        var n = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetter(ch))
                break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n > 0 ? n - 1 : null;
    }
}
