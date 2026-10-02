using System.Text.RegularExpressions;

namespace FieldToolkit.Core.Hazmat;

/// <summary>One page of text extracted from the ERG PDF.</summary>
/// <param name="Number">1-based PDF page number (recorded on every entry for verification).</param>
/// <param name="Lines">Lines across the full page width, top to bottom.</param>
/// <param name="ColumnLines">The same page read column by column (left column, then right), for the two-column ID and name lists. Null if not extracted.</param>
public sealed record ErgPage(int Number, IReadOnlyList<string> Lines, IReadOnlyList<string>? ColumnLines = null);

public enum ErgPageKind
{
    Other,
    IdList,       // yellow pages: ID No. – Guide No. – Name
    NameList,     // blue pages: Name – Guide No. – ID No.
    Guide,        // orange pages
    Table1,       // green pages: initial isolation and protective action distances
    Table2,       // green pages: water-reactive materials producing toxic gases
    Table3        // green pages: large-spill distances for six common TIH gases (not imported; entries say "Refer to Table 3")
}

public sealed class ErgParseResult
{
    public ErgDataset Data { get; } = new();
    public List<string> Warnings { get; } = new();
    public Dictionary<int, ErgPageKind> PageKinds { get; } = new();

    /// <summary>Entries read from the blue (name-ordered) pages, used to cross-check the yellow pages.</summary>
    public List<ErgMaterial> NameListEntries { get; } = new();

    internal HashSet<string> PendingTable2 { get; } = new();
}

/// <summary>
/// Converts text extracted from the official ERG PDF into an <see cref="ErgDataset"/>. Text is kept exactly as printed;
/// the parser only splits it into fields. Anything it cannot place is reported as a warning rather than guessed.
/// </summary>
public static class ErgTextParser
{
    private const string GuideNo = @"(?<guide>\d{3})(?<p>P)?";

    private static readonly Regex IdRow = new(@"^(?<id>\d{4})\s+" + GuideNo + @"\s+(?<name>\S.*)$", RegexOptions.Compiled);
    private static readonly Regex NameRow = new(@"^(?<name>\S.*?)\s+" + GuideNo + @"\s+(?<id>\d{4})$", RegexOptions.Compiled);
    private static readonly Regex GuideHeader = new(@"^GUIDE\s+(?<n>\d{3})\b\s*(?<title>.*)$", RegexOptions.Compiled);
    private static readonly Regex Table1Start = new(@"^(?<id>\d{4})\s+" + GuideNo + @"\s+(?<rest>.*)$", RegexOptions.Compiled);

    private const string Metres = @"\d[\d,]*\s*m\s*\(\s*\d[\d,]*\s*ft\s*\)";
    private const string Km = @"\d[\d.,]*\+?\s*km\s*\(\s*\d[\d.,]*\+?\s*mi\s*\)";
    private static readonly Regex Triple = new($@"(?<iso>{Metres})\s+(?<day>{Km})\s+(?<night>{Km})", RegexOptions.Compiled);
    private static readonly Regex ReferTable3 = new(@"Refer\s+to\s+Table\s+3", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] MajorHeadings = { "POTENTIAL HAZARDS", "PUBLIC SAFETY", "EMERGENCY RESPONSE" };
    private static readonly string[] SubHeadings = { "FIRE OR EXPLOSION", "HEALTH", "PROTECTIVE CLOTHING", "EVACUATION", "FIRE", "SPILL OR LEAK", "FIRST AID" };

    public static ErgParseResult Parse(IReadOnlyList<ErgPage> pages)
    {
        var result = new ErgParseResult();
        var guides = new Dictionary<string, ErgGuide>();
        foreach (var page in pages)
        {
            var kind = Classify(page);
            result.PageKinds[page.Number] = kind;
            switch (kind)
            {
                case ErgPageKind.IdList:
                    ParseList(page, result.Data.Materials, IdRow, result.Warnings);
                    break;
                case ErgPageKind.NameList:
                    ParseList(page, result.NameListEntries, NameRow, result.Warnings);
                    break;
                case ErgPageKind.Guide:
                    ParseGuidePage(page, guides, result.Warnings);
                    break;
                case ErgPageKind.Table1:
                    ParseTable1(page, result.Data.IsolationTable, result.Warnings);
                    break;
                case ErgPageKind.Table2:
                    ParseTable2(page, result);
                    break;
            }
        }

        result.Data.Guides = guides.Values.OrderBy(g => g.Number, StringComparer.Ordinal).ToList();
        var tih = new HashSet<string>(result.Data.IsolationTable.Select(e => e.Id));
        foreach (var m in result.Data.Materials)
            m.Highlighted = tih.Contains(m.Id);
        CrossCheck(result);
        return result;
    }

    // ------------------------------------------------------------------ classification

    public static ErgPageKind Classify(ErgPage page)
    {
        var text = string.Join("\n", page.Lines);
        var upper = text.ToUpperInvariant();
        if (upper.Contains("TABLE 2") && upper.Contains("WATER-REACTIVE"))
            return ErgPageKind.Table2;
        if (upper.Contains("TABLE 3") && !upper.Contains("REFER TO TABLE 3"))
            return ErgPageKind.Table3;
        if (page.Lines.Count(l => Triple.IsMatch(l)) >= 2 || (upper.Contains("TABLE 1") && upper.Contains("INITIAL ISOLATION") && page.Lines.Any(l => Triple.IsMatch(l))))
            return ErgPageKind.Table1;
        if (page.Lines.Take(6).Any(l => GuideHeader.IsMatch(l.Trim())) && page.Lines.Any(l => MajorHeadings.Contains(l.Trim())))
            return ErgPageKind.Guide;

        var lines = page.ColumnLines ?? page.Lines;
        var ids = lines.Count(l => IdRow.IsMatch(l.Trim()));
        var names = lines.Count(l => NameRow.IsMatch(l.Trim()) && !IdRow.IsMatch(l.Trim()));
        if (ids >= 3 && ids > names)
            return ErgPageKind.IdList;
        if (names >= 3 && names > ids)
            return ErgPageKind.NameList;
        return ErgPageKind.Other;
    }

    // ------------------------------------------------------------------ yellow / blue lists

    private static readonly string[] ListHeaderWords = { "ID", "No.", "Guide", "Name of Material" };

    private static void ParseList(ErgPage page, List<ErgMaterial> into, Regex row, List<string> warnings)
    {
        ErgMaterial? last = null;
        foreach (var raw in page.ColumnLines ?? page.Lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || IsListHeaderOrFooter(line))
                continue;
            var m = row.Match(line);
            if (m.Success)
            {
                last = new ErgMaterial
                {
                    Id = m.Groups["id"].Value,
                    Guide = m.Groups["guide"].Value,
                    Polymerizable = m.Groups["p"].Success,
                    Name = m.Groups["name"].Value.Trim(),
                    Page = page.Number.ToString()
                };
                into.Add(last);
            }
            else if (last != null && LooksLikeContinuation(line))
            {
                // Long names wrap onto the next line without an ID.
                last.Name = JoinWrapped(last.Name, line);
            }
            else
            {
                warnings.Add($"Page {page.Number}: unrecognised list line \"{line}\"");
            }
        }
    }

    private static bool IsListHeaderOrFooter(string line) =>
        line.StartsWith("ID", StringComparison.Ordinal) && line.Contains("Guide", StringComparison.Ordinal)
        || line.StartsWith("Name of Material", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(line, @"^(Page\s+)?\d{1,3}$")
        || Regex.IsMatch(line, @"^ERG\s*\d{4}$", RegexOptions.IgnoreCase)
        || ListHeaderWords.Contains(line);

    private static bool LooksLikeContinuation(string line) => !Regex.IsMatch(line, @"^\d{4}\b") && !Regex.IsMatch(line, @"\b\d{4}$");

    private static string JoinWrapped(string a, string b) => a.EndsWith('-') ? a + b : a + " " + b;

    // ------------------------------------------------------------------ orange guide pages

    private static void ParseGuidePage(ErgPage page, Dictionary<string, ErgGuide> guides, List<string> warnings)
    {
        ErgGuide? guide = null;
        ErgSection? section = null;
        ErgSubsection? sub = null;
        var titleLines = new List<string>();
        var inTitle = false;

        foreach (var raw in page.Lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || Regex.IsMatch(line, @"^(ERG\s*\d{4}|Page\s+\d+|\d{1,3})$", RegexOptions.IgnoreCase))
                continue;

            var header = GuideHeader.Match(line);
            if (guide == null && header.Success)
            {
                var number = header.Groups["n"].Value;
                if (!guides.TryGetValue(number, out guide))
                    guides[number] = guide = new ErgGuide { Number = number };
                guide.Pages = guide.Pages.Length == 0 ? page.Number.ToString() : guide.Pages + "," + page.Number;
                var rest = header.Groups["title"].Value.Trim();
                if (rest.Length > 0)
                    titleLines.Add(rest);
                inTitle = true;
                continue;
            }
            if (guide == null)
                continue;

            if (MajorHeadings.Contains(line))
            {
                inTitle = false;
                section = guide.Sections.FirstOrDefault(s => s.Heading == line);
                if (section == null)
                    guide.Sections.Add(section = new ErgSection { Heading = line });
                sub = null;
                continue;
            }
            if (inTitle)
            {
                titleLines.Add(line);
                continue;
            }
            if (section == null)
            {
                warnings.Add($"Page {page.Number}: text before the first section heading of guide {guide.Number}: \"{line}\"");
                continue;
            }
            if (SubHeadings.Contains(line))
            {
                section.Subsections.Add(sub = new ErgSubsection { Heading = line });
                continue;
            }

            if (sub == null)
                section.Subsections.Add(sub = new ErgSubsection());
            var bullet = Regex.Match(line, @"^[••\-–]\s*(?<t>.*)$");
            if (bullet.Success)
                sub.Items.Add(bullet.Groups["t"].Value.Trim());
            else if (sub.Items.Count == 0 || StartsNewParagraph(line, sub.Items[^1]))
                sub.Items.Add(line);
            else
                sub.Items[^1] = JoinWrapped(sub.Items[^1], line);
        }

        if (guide != null && titleLines.Count > 0 && guide.Title.Length == 0)
            guide.Title = string.Join(" ", titleLines);
    }

    /// <summary>Paragraph labels ("Small Fire", "Large Spill", "CAUTION: …") start a new item; other lines continue the previous one.</summary>
    private static bool StartsNewParagraph(string line, string previous) =>
        line.StartsWith("CAUTION", StringComparison.Ordinal)
        || (previous.EndsWith('.') || previous.EndsWith(':')) && char.IsUpper(line[0]) && line.Length < 70 && !line.EndsWith(',');

    // ------------------------------------------------------------------ green tables

    private static void ParseTable1(ErgPage page, List<ErgIsolationEntry> into, List<string> warnings)
    {
        ErgIsolationEntry? pending = null;
        var buffer = "";

        void Flush()
        {
            if (pending == null)
                return;
            if (!TryFillDistances(pending, buffer))
                warnings.Add($"Page {page.Number}: could not read distances for Table 1 entry {pending.Id}: \"{buffer}\"");
            else
                into.Add(pending);
            pending = null;
            buffer = "";
        }

        foreach (var raw in page.Lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var start = Table1Start.Match(line);
            if (start.Success && ErgRepository.IsIdNumber(start.Groups["id"].Value))
            {
                Flush();
                pending = new ErgIsolationEntry { Id = start.Groups["id"].Value, Guide = start.Groups["guide"].Value, Page = page.Number.ToString() };
                buffer = start.Groups["rest"].Value;
            }
            else if (pending != null && !IsTableHeading(line))
            {
                buffer += " " + line;
            }
        }
        Flush();
    }

    private static bool IsTableHeading(string line)
    {
        var u = line.ToUpperInvariant();
        return u.StartsWith("TABLE 1") || u.Contains("SMALL SPILLS") || u.Contains("LARGE SPILLS") || u.StartsWith("ID") && u.Contains("GUIDE")
               || u.Contains("FIRST ISOLATE") || u.Contains("THEN PROTECT") || Regex.IsMatch(line, @"^(ERG\s*\d{4}|Page\s+\d+|\d{1,3})$", RegexOptions.IgnoreCase);
    }

    /// <summary>Splits "Name 30 m (100 ft) 0.1 km (0.1 mi) 0.2 km (0.1 mi) 150 m (500 ft) …" into name and distances.</summary>
    internal static bool TryFillDistances(ErgIsolationEntry e, string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var first = Triple.Match(text);
        if (!first.Success)
            return false;
        e.Name = text[..first.Index].Trim();
        e.Small = new ErgDistances { Isolate = Clean(first.Groups["iso"].Value), ProtectDay = Clean(first.Groups["day"].Value), ProtectNight = Clean(first.Groups["night"].Value) };
        var rest = text[(first.Index + first.Length)..];
        var second = Triple.Match(rest);
        if (second.Success)
        {
            e.Large = new ErgDistances { Isolate = Clean(second.Groups["iso"].Value), ProtectDay = Clean(second.Groups["day"].Value), ProtectNight = Clean(second.Groups["night"].Value) };
            var tail = (rest[..second.Index] + " " + rest[(second.Index + second.Length)..]).Trim();
            if (tail.Length > 0)
                e.Note = tail;
        }
        else if (ReferTable3.IsMatch(rest))
        {
            e.Large = new ErgDistances { Isolate = "Refer to Table 3", ProtectDay = "Refer to Table 3", ProtectNight = "Refer to Table 3" };
            var tail = ReferTable3.Replace(rest, "").Trim();
            if (tail.Length > 0)
                e.Note = tail;
        }
        else
        {
            return false;
        }
        if (e.Name.Length == 0)
            return false;
        return true;
    }

    private static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Replace("( ", "(").Replace(" )", ")").Trim();

    private static void ParseTable2(ErgPage page, ErgParseResult result)
    {
        foreach (var raw in page.Lines)
        {
            var m = Table1Start.Match(raw.Trim());
            if (!m.Success)
                continue;
            result.PendingTable2.Add(m.Groups["id"].Value);
        }
    }

    // ------------------------------------------------------------------ checks

    private static void CrossCheck(ErgParseResult r)
    {
        // Table 2 may come before or after the lists in the PDF; apply it once everything is read.
        foreach (var m in r.Data.Materials.Where(m => r.PendingTable2.Contains(m.Id)))
            m.WaterReactive = true;

        var yellow = new HashSet<(string, string)>(r.Data.Materials.Select(m => (m.Id, m.Guide)));
        foreach (var b in r.NameListEntries.Where(b => !yellow.Contains((b.Id, b.Guide))))
            r.Warnings.Add($"Name list (page {b.Page}) has {b.Id} / guide {b.GuideLabel} \"{b.Name}\" but the ID list does not.");
        if (r.NameListEntries.Count > 0 && r.NameListEntries.Count != r.Data.Materials.Count)
            r.Warnings.Add($"ID list has {r.Data.Materials.Count} entries but the name list has {r.NameListEntries.Count}.");
    }
}
