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
    IdList,           // yellow pages: ID No. – Guide No. – Name
    NameList,         // blue pages: Name – Guide No. – ID No.
    Guide,            // orange pages
    GeneralFirstAid,  // the General First Aid page every guide refers to
    Table1,           // green pages: initial isolation and protective action distances
    Table2,           // green pages: water-reactive materials producing toxic gases
    Table3            // green pages: large-spill distances for six common TIH gases (not imported; entries say "Refer to Table 3")
}

public sealed class ErgParseResult
{
    public ErgDataset Data { get; } = new();
    public List<string> Warnings { get; } = new();
    public Dictionary<int, ErgPageKind> PageKinds { get; } = new();

    /// <summary>Entries read from the blue (name-ordered) pages, used to cross-check the yellow pages.</summary>
    public List<ErgMaterial> NameListEntries { get; } = new();

    internal Dictionary<string, string> Table2Gases { get; } = new();
}

/// <summary>
/// Converts text extracted from the official ERG PDF into an <see cref="ErgDataset"/>. Text is kept exactly as printed;
/// the parser only splits it into fields. Anything it cannot place is reported as a warning rather than guessed.
/// </summary>
public static class ErgTextParser
{
    private const string GuideNo = @"(?<guide>\d{3})(?<p>P)?";
    private const string IdNo = @"(?<id>\d{4}|—\s*—|-\s*-)";

    private static readonly Regex IdRow = new(@"^" + IdNo + @"\s+" + GuideNo + @"\s+(?<name>\S.*)$", RegexOptions.Compiled);
    private static readonly Regex NameRow = new(@"^(?<name>\S.*?)\s+" + GuideNo + @"\s+" + IdNo + @"$", RegexOptions.Compiled);
    /// <summary>Fallback for names printed touching their guide number ("…diamines153 2327").</summary>
    private static readonly Regex NameRowTight = new(@"^(?<name>\S.*?\D)" + GuideNo + @"\s+" + IdNo + @"$", RegexOptions.Compiled);
    private static readonly Regex Table1Start = new(@"^(?<id>\d{4})\s+" + GuideNo + @"\s+(?<rest>.*)$", RegexOptions.Compiled);

    private const string Metres = @"\d[\d,]*\s*m\s*\(\s*\d[\d,]*\s*ft\s*\)";
    private const string Km = @"\d[\d.,]*\+?\s*km\s*\(\s*\d[\d.,]*\+?\s*mi\s*\)";
    private static readonly Regex Triple = new($@"(?<iso>{Metres})\s+(?<day>{Km})\s+(?<night>{Km})", RegexOptions.Compiled);
    private static readonly Regex ReferTable3 = new(@"Refer\s+to\s+Table\s+3", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Footer = new(@"^(Page\s+\d+(\s+ERG\s*\d{4})?|ERG\s*\d{4}(\s+Page\s+\d+)?|\d{1,3})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] MajorHeadings = { "POTENTIAL HAZARDS", "PUBLIC SAFETY", "EMERGENCY RESPONSE" };
    private static readonly string[] SubHeadings = { "FIRE OR EXPLOSION", "HEALTH", "PROTECTIVE CLOTHING", "EVACUATION", "FIRE", "SPILL OR LEAK", "FIRST AID" };

    public static ErgParseResult Parse(IReadOnlyList<ErgPage> rawPages)
    {
        // The extractor marks column gaps with tabs. Only guide pages use them (side-by-side "Small Spill / Large Spill"
        // blocks); everything else reads the text with ordinary spaces.
        var rawByNumber = rawPages.ToDictionary(p => p.Number);
        var pages = rawPages.Select(p => p with { Lines = p.Lines.Select(Flat).ToList(), ColumnLines = p.ColumnLines?.Select(Flat).ToList() }).ToList();
        var result = new ErgParseResult();
        var guides = new Dictionary<string, ErgGuide>();
        var blankGuides = new HashSet<string>();
        var previousKind = ErgPageKind.Other;
        var table1Lines = new List<(int Page, string Line)>();
        foreach (var page in pages)
        {
            var kind = Classify(page);
            // Table 2 continues over several pages that look like the yellow ID list but carry no table heading.
            if (kind == ErgPageKind.IdList && previousKind == ErgPageKind.Table2)
                kind = ErgPageKind.Table2;
            previousKind = kind;
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
                    ParseGuidePage(page, rawByNumber[page.Number], guides, blankGuides, result.Warnings);
                    break;
                case ErgPageKind.GeneralFirstAid:
                    ParseGeneralFirstAid(page, result);
                    break;
                case ErgPageKind.Table1:
                    table1Lines.AddRange(page.Lines.Select(l => (page.Number, l.Trim())).Where(x => x.Item2.Length > 0 && !IsTableHeading(x.Item2)));
                    break;
                case ErgPageKind.Table2:
                    ParseTable2(page, result);
                    break;
            }
        }

        // Table 1 rows (and long names) run across page breaks, so the green pages are read as one stream.
        ParseTable1(table1Lines, result.Data.IsolationTable, result.Warnings);

        result.Data.Guides = guides.Values.OrderBy(g => g.Number, StringComparer.Ordinal).ToList();
        result.Data.BlankGuides = blankGuides.Order(StringComparer.Ordinal).ToList();
        var tih = new HashSet<string>(result.Data.IsolationTable.Select(e => e.Id));
        foreach (var m in result.Data.Materials)
        {
            m.Highlighted = m.Id.Length > 0 && tih.Contains(m.Id);
            if (m.Id.Length > 0 && result.Table2Gases.TryGetValue(m.Id, out var gases))
            {
                m.WaterReactive = true;
                m.WaterReactiveGases = gases;
            }
        }
        CrossCheck(result);
        return result;
    }

    // ------------------------------------------------------------------ classification

    public static ErgPageKind Classify(ErgPage page)
    {
        var text = string.Join("\n", page.Lines);
        var upper = text.ToUpperInvariant();
        var first = page.Lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        if (first.Equals("GENERAL FIRST AID", StringComparison.OrdinalIgnoreCase))
            return ErgPageKind.GeneralFirstAid;
        if (page.Lines.Take(4).Any(l => Regex.IsMatch(l.Trim().ToUpperInvariant(), @"^TABLE\s*2\s*-\s*WATER-REACTIVE")))
            return ErgPageKind.Table2;
        if (Regex.IsMatch(upper, @"TABLE\s*3\b") && !upper.Contains("REFER TO TABLE 3") && !page.Lines.Any(l => Table1Start.IsMatch(l.Trim()) && Triple.IsMatch(l)))
            return ErgPageKind.Table3;
        if (page.Lines.Count(l => Triple.IsMatch(l)) >= 2 || (upper.Contains("TABLE 1") && upper.Contains("INITIAL ISOLATION") && page.Lines.Any(l => Triple.IsMatch(l))))
            return ErgPageKind.Table1;
        if (TryGuideHeader(page.Lines, out _, out _, out _) &&
            (page.Lines.Any(l => MajorHeadings.Contains(l.Trim())) || upper.Contains("INTENTIONALLY LEFT BLANK")))
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

    private static void ParseList(ErgPage page, List<ErgMaterial> into, Regex row, List<string> warnings)
    {
        ErgMaterial? last = null;
        foreach (var raw in page.ColumnLines ?? page.Lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || IsListHeaderOrFooter(line))
                continue;
            var m = row.Match(line);
            if (!m.Success && row == NameRow)
                m = NameRowTight.Match(line);
            if (m.Success)
            {
                var id = m.Groups["id"].Value;
                last = new ErgMaterial
                {
                    Id = id.All(char.IsAsciiDigit) ? id : "",
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
        || Regex.IsMatch(line, @"^(No\.\s*)+$")
        || Regex.IsMatch(line, @"^(—\s*)+$")
        || Footer.IsMatch(line);

    private static bool LooksLikeContinuation(string line) => !Regex.IsMatch(line, @"^\d{4}\b") && !Regex.IsMatch(line, @"\b\d{4}$");

    private static string JoinWrapped(string a, string b) => a.EndsWith('-') && !a.EndsWith(" -") ? a + b : a + " " + b;

    // ------------------------------------------------------------------ orange guide pages

    /// <summary>
    /// Finds a guide page header in the first few lines. Handles "GUIDE 128 TITLE" on one line and the ERG 2024 layout:
    /// "GUIDE Title part" (or "Title part GUIDE" on right-hand pages), then "128 rest of title".
    /// </summary>
    internal static bool TryGuideHeader(IReadOnlyList<string> lines, out string number, out string title, out int headerLines)
    {
        number = title = "";
        headerLines = 0;
        var top = lines.Select(l => l.Trim()).Take(6).ToList();
        for (var i = 0; i < Math.Min(3, top.Count); i++)
        {
            var words = top[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count == 0)
                continue;
            int guideAt = words[0] == "GUIDE" ? 0 : words[^1] == "GUIDE" ? words.Count - 1 : -1;
            if (guideAt < 0)
                continue;
            words.RemoveAt(guideAt);
            var titleParts = new List<string>();
            // Number on the same line: "GUIDE 128 TITLE".
            if (words.Count > 0 && Regex.IsMatch(words[0], @"^\d{3}$"))
            {
                number = words[0];
                titleParts.Add(string.Join(' ', words.Skip(1)));
                headerLines = i + 1;
                // The title may continue on the following lines, up to the first section heading.
                for (var j = i + 1; j < top.Count && j <= i + 3 && !MajorHeadings.Contains(top[j]); j++)
                {
                    if (!Footer.IsMatch(top[j]))
                        titleParts.Add(top[j]);
                    headerLines = j + 1;
                }
            }
            else
            {
                titleParts.Add(string.Join(' ', words));
                for (var j = i + 1; j < top.Count && j <= i + 3; j++)
                {
                    var w = top[j].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (w.Count == 0)
                        continue;
                    var numAt = Regex.IsMatch(w[0], @"^\d{3}$") ? 0 : Regex.IsMatch(w[^1], @"^\d{3}$") ? w.Count - 1 : -1;
                    if (numAt >= 0)
                    {
                        number = w[numAt];
                        w.RemoveAt(numAt);
                        titleParts.Add(string.Join(' ', w));
                        headerLines = j + 1;
                        break;
                    }
                    if (MajorHeadings.Contains(top[j]))
                        break;
                    titleParts.Add(top[j]);
                }
            }
            if (number.Length == 0)
                return false;
            title = Regex.Replace(string.Join(' ', titleParts.Where(t => t.Length > 0)), @"\s+", " ").Trim();
            return true;
        }
        return false;
    }

    private static string Flat(string line) => Regex.Replace(line.Replace('\t', ' '), @" {2,}", " ").Trim();

    private static void ParseGuidePage(ErgPage page, ErgPage raw, Dictionary<string, ErgGuide> guides, HashSet<string> blank, List<string> warnings)
    {
        if (!TryGuideHeader(page.Lines, out var number, out var title, out var headerLines))
            return;
        if (title.Length == 0 && page.Lines.Skip(headerLines).Any(l => l.Contains("Intentionally left blank", StringComparison.OrdinalIgnoreCase)))
        {
            blank.Add(number);
            return;
        }
        if (!guides.TryGetValue(number, out var guide))
            guides[number] = guide = new ErgGuide { Number = number };
        guide.Pages = guide.Pages.Length == 0 ? page.Number.ToString() : guide.Pages + "," + page.Number;
        if (guide.Title.Length == 0)
            guide.Title = title;

        ErgSection? section = null;
        ErgSubsection? sub = null;
        string? note = null;
        var notes = new List<string>();
        List<string>? left = null, right = null;   // a two-column block being collected

        void FlushColumns()
        {
            if (left == null || right == null || section == null)
                return;
            sub ??= AddSub(section);
            foreach (var l in left.Concat(right))
                AddItemLine(sub, l);
            left = right = null;
        }

        foreach (var rawLine in raw.Lines.Skip(headerLines))
        {
            var line = Flat(rawLine);
            if (line.Length == 0 || Footer.IsMatch(line))
                continue;
            // Boxed notes at the foot of the page (the Canadian ERAP notice) and "*" footnotes are guide-level notes.
            if (line.StartsWith("In Canada, an Emergency Response Assistance Plan", StringComparison.Ordinal) || Regex.IsMatch(line, @"^\*\s*\S"))
            {
                FlushColumns();
                if (note != null)
                    notes.Add(note);
                note = line.StartsWith('*') ? "* " + Regex.Replace(line.TrimStart('*', ' '), @"^(\p{Lu}) (\p{Ll})", "$1$2") : line;
                continue;
            }
            if (note != null)
            {
                note = JoinWrapped(note, line);
                continue;
            }
            if (MajorHeadings.Contains(line) || SubHeadings.Contains(line))
            {
                FlushColumns();
                if (MajorHeadings.Contains(line))
                {
                    section = guide.Sections.FirstOrDefault(s => s.Heading == line);
                    if (section == null)
                        guide.Sections.Add(section = new ErgSection { Heading = line });
                    sub = null;
                }
                else if (section != null)
                {
                    section.Subsections.Add(sub = new ErgSubsection { Heading = line });
                }
                continue;
            }
            if (section == null)
            {
                warnings.Add($"Page {page.Number}: text before the first section heading of guide {guide.Number}: \"{line}\"");
                continue;
            }

            // Two columns side by side: "Small Spill<TAB>Large Spill", then lines split by a tab, right-only lines with a leading tab.
            var trimmed = rawLine.Trim(' ');
            var split = trimmed.TrimStart('\t').IndexOf('\t');
            if (split > 0 || (left != null && trimmed.StartsWith('\t')))
            {
                left ??= new List<string>();
                right ??= new List<string>();
                if (trimmed.StartsWith('\t'))
                {
                    right.Add(Flat(trimmed));
                }
                else
                {
                    left.Add(Flat(trimmed[..split]));
                    right.Add(Flat(trimmed[(split + 1)..]));
                }
                continue;
            }
            if (left != null)
            {
                left.Add(line);   // a left-column line with nothing beside it
                continue;
            }
            sub ??= AddSub(section);
            AddItemLine(sub, line);
        }
        FlushColumns();
        if (note != null)
            notes.Add(note);
        foreach (var n in notes.Where(n => !guide.Notes.Contains(n)))
            guide.Notes.Add(n);
    }

    private static ErgSubsection AddSub(ErgSection section)
    {
        var sub = new ErgSubsection();
        section.Subsections.Add(sub);
        return sub;
    }

    /// <summary>Bullets start items, "–" starts an indented sub-item, labels start a new item, other lines continue the previous one.</summary>
    private static void AddItemLine(ErgSubsection sub, string line)
    {
        var bullet = Regex.Match(line, @"^[••]\s*(?<t>.*)$");
        var dash = Regex.Match(line, @"^[–\-]\s+(?<t>.*)$");
        if (bullet.Success)
            sub.Items.Add(bullet.Groups["t"].Value.Trim());
        else if (dash.Success)
            sub.Items.Add("– " + dash.Groups["t"].Value.Trim());
        else if (sub.Items.Count == 0 || StartsNewParagraph(line, sub.Items[^1]))
            sub.Items.Add(line);
        else
            sub.Items[^1] = JoinWrapped(sub.Items[^1], line);
    }

    /// <summary>Paragraph labels ("Small Fire", "Large Spill", "CAUTION: …") start a new item; other lines continue the previous one.</summary>
    private static bool StartsNewParagraph(string line, string previous) =>
        line.StartsWith("CAUTION", StringComparison.Ordinal)
        || (previous.EndsWith('.') || previous.EndsWith(':')) && char.IsUpper(line[0]) && line.Length < 70 && !line.EndsWith(',');

    private static void ParseGeneralFirstAid(ErgPage page, ErgParseResult result)
    {
        var sub = new ErgSubsection();
        foreach (var raw in page.Lines.Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0 || Footer.IsMatch(line))
                continue;
            AddItemLine(sub, line);
        }
        result.Data.GeneralFirstAid = new ErgSection { Heading = "GENERAL FIRST AID", Subsections = { sub } };
        result.Data.GeneralFirstAidPage = page.Number.ToString();
    }

    // ------------------------------------------------------------------ green tables

    private static bool IsTableHeading(string line)
    {
        var u = line.ToUpperInvariant();
        return u.StartsWith("TABLE 1") || u.EndsWith("TABLE 1") || u.Contains("SMALL SPILLS") || u.Contains("LARGE SPILLS")
               || u.StartsWith("(FROM A") || u.StartsWith("FIRST THEN") || u.StartsWith("ISOLATE") || u.StartsWith("IN ALL DIRECTIONS")
               || u.StartsWith("ID GUIDE") || u.StartsWith("ID ") && u.Contains("GUIDE") || u.StartsWith("NO. NO.") || u.StartsWith("\"+\"") || u.StartsWith("“+”")
               || u.Contains("FIRST ISOLATE") || u.Contains("THEN PROTECT") || u == "—" || u.StartsWith("SEE NEXT PAGE") || Footer.IsMatch(line);
    }

    private sealed class T1Entry
    {
        public string Id = "";
        public string Guide = "";
        public string Name = "";
        public int Page;
        public int Lines = 1;
        public ErgDistances? Small;
        public ErgDistances? Large;
        public string Note = "";
    }

    /// <summary>
    /// Table 1 rows sharing the same distances (synonyms, "when spilled on land/in water", inhalation hazard zones)
    /// are printed as a block with the distances vertically centred on it, so in the text the distance line sits
    /// between the names. Rules: wrapped name lines belong to the entry above them; a distance line takes the
    /// entries waiting above it plus, below it, as many lines again of the same ID number.
    /// </summary>
    private static void ParseTable1(List<(int Page, string Line)> lines, List<ErgIsolationEntry> into, List<string> warnings)
    {
        var pending = new List<T1Entry>();   // entries above the next distance line
        T1Entry? last = null;
        var below = 0;                       // lines still allowed below the current distance line
        var groupId = "";
        List<T1Entry>? group = null;

        void Close(List<T1Entry> entries)
        {
            foreach (var e in entries)
            {
                if (e.Small == null)
                {
                    warnings.Add($"Page {e.Page}: Table 1 entry {e.Id} \"{e.Name}\" has no distances.");
                    continue;
                }
                into.Add(new ErgIsolationEntry
                {
                    Id = e.Id, Guide = e.Guide, Name = e.Name, Small = e.Small, Large = e.Large ?? new ErgDistances(), Note = e.Note, Page = e.Page.ToString()
                });
            }
        }

        void Assign(List<T1Entry> entries, (ErgDistances Small, ErgDistances Large, string Note) d, int page)
        {
            var ids = entries.Select(e => e.Id).Distinct().ToList();
            if (ids.Count > 1)
                warnings.Add($"Page {page}: one set of Table 1 distances was printed next to different ID numbers ({string.Join(", ", ids)}); applied to {ids[^1]} only.");
            foreach (var e in entries.Where(e => e.Id == ids[^1]))
                (e.Small, e.Large, e.Note) = (d.Small, d.Large, d.Note);
            foreach (var e in entries.Where(e => e.Id != ids[^1]))
                warnings.Add($"Page {e.Page}: Table 1 entry {e.Id} \"{e.Name}\" has no distances.");
        }

        foreach (var (page, line) in lines)
        {
            var start = Table1Start.Match(line);
            var triple = Triple.Match(line);
            string beforeDistances = triple.Success ? line[..triple.Index].Trim() : line;

            if (start.Success)
            {
                var entry = new T1Entry
                {
                    Id = start.Groups["id"].Value,
                    Guide = start.Groups["guide"].Value,
                    Page = page,
                    Name = (triple.Success ? line[start.Groups["rest"].Index..triple.Index] : start.Groups["rest"].Value).Trim()
                };
                // A new entry below a closed-off distance line joins that group only if it has the same ID and fits.
                if (group != null && below > 0 && entry.Id == groupId && !triple.Success)
                {
                    group.Add(entry);
                    below--;
                    last = entry;
                    entry.Small = group[0].Small;
                    entry.Large = group[0].Large;
                    entry.Note = group[0].Note;
                    continue;
                }
                if (group != null)
                {
                    Close(group);
                    group = null;
                    below = 0;
                }
                pending.Add(entry);
                last = entry;
                if (!triple.Success)
                    continue;
                beforeDistances = "";
            }
            else if (!triple.Success)
            {
                // Wrapped name: belongs to the entry above (even across the distance line or a page break).
                if (last != null)
                {
                    last.Name = JoinWrapped(last.Name, line);
                    last.Lines++;
                    if (group != null && group.Contains(last))
                        below = Math.Max(0, below - 1);
                }
                else
                {
                    warnings.Add($"Page {page}: unrecognised Table 1 line \"{line}\"");
                }
                continue;
            }

            // A distance line (possibly sharing the line with a wrapped name).
            if (beforeDistances.Length > 0 && last != null)
            {
                last.Name = JoinWrapped(last.Name, beforeDistances);
                last.Lines++;
            }
            if (!TryParseDistances(line[triple.Index..], out var d))
            {
                warnings.Add($"Page {page}: could not read Table 1 distances \"{line}\"");
                continue;
            }
            if (group != null)
            {
                Close(group);
                group = null;
            }
            if (pending.Count == 0)
            {
                warnings.Add($"Page {page}: Table 1 distances with no material name: \"{line}\"");
                continue;
            }
            Assign(pending, d, page);
            group = pending.Where(e => e.Small != null).ToList();
            foreach (var e in pending.Where(e => e.Small == null))
                warnings.Add($"Page {e.Page}: Table 1 entry {e.Id} \"{e.Name}\" has no distances.");
            groupId = group.Count > 0 ? group[0].Id : "";
            var above = pending.Sum(e => e.Lines) - (start.Success ? 1 : 0) - (beforeDistances.Length > 0 ? 1 : 0);
            below = Math.Max(0, above);
            pending = new List<T1Entry>();
        }
        if (group != null)
            Close(group);
        foreach (var e in pending)
            warnings.Add($"Page {e.Page}: Table 1 entry {e.Id} \"{e.Name}\" has no distances.");
    }

    /// <summary>"30 m (100 ft) 0.1 km (0.1 mi) 0.2 km (0.1 mi) 150 m (500 ft) …" or "… Refer to Table 3".</summary>
    private static bool TryParseDistances(string text, out (ErgDistances Small, ErgDistances Large, string Note) d)
    {
        d = default;
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var first = Triple.Match(text);
        if (!first.Success)
            return false;
        var small = new ErgDistances { Isolate = Clean(first.Groups["iso"].Value), ProtectDay = Clean(first.Groups["day"].Value), ProtectNight = Clean(first.Groups["night"].Value) };
        var rest = text[(first.Index + first.Length)..];
        var second = Triple.Match(rest);
        if (second.Success)
        {
            var large = new ErgDistances { Isolate = Clean(second.Groups["iso"].Value), ProtectDay = Clean(second.Groups["day"].Value), ProtectNight = Clean(second.Groups["night"].Value) };
            d = (small, large, (rest[..second.Index] + " " + rest[(second.Index + second.Length)..]).Trim());
            return true;
        }
        if (ReferTable3.IsMatch(rest))
        {
            var t3 = new ErgDistances { Isolate = "Refer to Table 3", ProtectDay = "Refer to Table 3", ProtectNight = "Refer to Table 3" };
            d = (small, t3, ReferTable3.Replace(rest, "").Trim());
            return true;
        }
        return false;
    }

    /// <summary>Splits "Name 30 m (100 ft) 0.1 km (0.1 mi) …" into name and distances (single-row form).</summary>
    internal static bool TryFillDistances(ErgIsolationEntry e, string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var first = Triple.Match(text);
        if (!first.Success || !TryParseDistances(text[first.Index..], out var d))
            return false;
        e.Name = text[..first.Index].Trim();
        (e.Small, e.Large, e.Note) = d;
        return e.Name.Length > 0;
    }

    private static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Replace("( ", "(").Replace(" )", ")").Trim();

    private static readonly Regex Table2Row = new(@"(?:^|\s)(?<id>\d{4})\s+\d{3}P?\s+(?<rest>.+?)(?=\s\d{4}\s+\d{3}P?\s|$)", RegexOptions.Compiled);
    private static readonly Regex Formula = new(@"^(?:[A-Z][a-z]?\d*)+$", RegexOptions.Compiled);

    private static void ParseTable2(ErgPage page, ErgParseResult result)
    {
        foreach (var raw in page.Lines)
        {
            foreach (Match m in Table2Row.Matches(raw.Trim()))
            {
                // The gases produced are printed after the name as chemical symbols, e.g. "HCl", "HCl HF", "NH3".
                var tokens = m.Groups["rest"].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                var gases = tokens.Reverse().TakeWhile(t => Formula.IsMatch(t) && t.Length <= 6 && t.Any(char.IsUpper) && !t.All(char.IsLower)).Reverse().ToList();
                // A single capitalised word like "Lithium" is not a formula: formulas have no run of 3+ lower-case letters.
                gases = gases.Where(g => !Regex.IsMatch(g, "[a-z]{2,}")).ToList();
                var id = m.Groups["id"].Value;
                var text = string.Join(" ", gases);
                if (result.Table2Gases.TryGetValue(id, out var existing) && existing.Length > 0)
                {
                    foreach (var g in gases.Where(g => !existing.Split(' ').Contains(g)))
                        existing += " " + g;
                    result.Table2Gases[id] = existing;
                }
                else
                {
                    result.Table2Gases[id] = text;
                }
            }
        }
    }

    // ------------------------------------------------------------------ checks

    private static void CrossCheck(ErgParseResult r)
    {
        var yellow = new HashSet<(string, string, string)>(r.Data.Materials.Select(m => (m.Id, m.Guide, Norm(m.Name))));
        foreach (var b in r.NameListEntries.Where(b => !yellow.Contains((b.Id, b.Guide, Norm(b.Name)))))
            r.Warnings.Add($"Name list (page {b.Page}) has {b.IdLabel} / guide {b.GuideLabel} \"{b.Name}\" but the ID list does not.");
        var blue = new HashSet<(string, string, string)>(r.NameListEntries.Select(m => (m.Id, m.Guide, Norm(m.Name))));
        if (r.NameListEntries.Count > 0)
            foreach (var y in r.Data.Materials.Where(y => !blue.Contains((y.Id, y.Guide, Norm(y.Name)))))
                r.Warnings.Add($"ID list (page {y.Page}) has {y.IdLabel} / guide {y.GuideLabel} \"{y.Name}\" but the name list does not.");
        if (r.NameListEntries.Count > 0 && r.NameListEntries.Count != r.Data.Materials.Count)
            r.Warnings.Add($"ID list has {r.Data.Materials.Count} entries but the name list has {r.NameListEntries.Count}.");
    }

    /// <summary>Comparison key: the two lists differ in quote style and in spaces left by line wraps ("1, 3-diol").</summary>
    private static string Norm(string s) => Regex.Replace(s, @"[\s""“”'‘’]", "").ToLowerInvariant();
}
