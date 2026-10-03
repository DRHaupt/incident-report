using FieldToolkit.Core.Hazmat;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ErgImport;

/// <summary>
/// Extracts page text from the ERG PDF as lines, both full-width and column by column. Works from individual letters:
/// each page's dominant text direction is rotated upright first, so landscape pages (the green Table 1 pages are
/// printed sideways) read the same as portrait ones.
/// </summary>
internal static class PdfText
{
    private readonly record struct Glyph(string Text, double X, double Y, double Right, double Size)
    {
        public bool IsSpace => string.IsNullOrWhiteSpace(Text);
    }

    public static List<ErgPage> Extract(string pdfPath, Action<int, int>? progress = null)
    {
        var pages = new List<ErgPage>();
        using var doc = PdfDocument.Open(pdfPath);
        var total = doc.NumberOfPages;
        foreach (var page in doc.GetPages())
        {
            var glyphs = Upright(page.Letters, out var width);
            var full = ToLines(glyphs, width);
            var mid = width / 2;
            var columns = ToLines(glyphs.Where(g => g.X < mid).ToList()).Concat(ToLines(glyphs.Where(g => g.X >= mid).ToList())).ToList();
            pages.Add(new ErgPage(page.Number, full, columns));
            progress?.Invoke(page.Number, total);
        }
        return pages;
    }

    /// <summary>
    /// Rotates the letters of the page's dominant reading direction so that they run left to right with y increasing
    /// down the page. Letters in other directions (decorative side labels) are dropped.
    /// </summary>
    private static List<Glyph> Upright(IReadOnlyList<Letter> letters, out double width)
    {
        static int Quadrant(Letter l)
        {
            var dx = l.EndBaseLine.X - l.StartBaseLine.X;
            var dy = l.EndBaseLine.Y - l.StartBaseLine.Y;
            if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6)
                return 0;
            var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            return (int)Math.Round(((angle % 360) + 360) % 360 / 90) % 4; // 0 = left→right, 1 = upward, 2 = upside down, 3 = downward
        }

        // Space characters are kept: justified text often has gaps too small to detect, but the PDF still holds the space.
        var visible = letters.Where(l => l.Value.Length > 0).ToList();
        width = 0;
        if (visible.Count == 0)
            return new List<Glyph>();
        var dominant = visible.Where(l => !string.IsNullOrWhiteSpace(l.Value)).GroupBy(Quadrant).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? 0;

        // Map baseline points into a frame where text runs along +x and lines go down along +y.
        (double X, double Y) Map(double x, double y) => dominant switch
        {
            1 => (y, x),     // text runs up the page: along = y, lines advance to the right
            2 => (-x, y),
            3 => (-y, -x),   // text runs down the page: along = -y, lines advance to the left
            _ => (x, -y)
        };

        var result = new List<Glyph>();
        foreach (var l in visible.Where(l => Quadrant(l) == dominant))
        {
            var (x, y) = Map(l.StartBaseLine.X, l.StartBaseLine.Y);
            var (right, _) = Map(l.EndBaseLine.X, l.EndBaseLine.Y);
            var size = Math.Max(1, Math.Max(l.PointSize, l.GlyphRectangle.Height));
            result.Add(new Glyph(l.Value, x, y, Math.Max(right, x), size));
        }
        if (result.Count > 0)
        {
            var minX = result.Min(g => g.X);
            width = result.Max(g => g.Right) - minX;
            result = result.Select(g => g with { X = g.X - minX, Right = g.Right - minX }).ToList();
        }
        return result;
    }

    /// <summary>
    /// Groups letters into lines by baseline, orders them along the line, and inserts spaces at gaps. Lines that start
    /// in the right half of the page get a leading tab (right column of a two-column block).
    /// </summary>
    private static List<string> ToLines(List<Glyph> glyphs, double pageWidth = 0)
    {
        var lines = new List<(double Y, List<Glyph> Glyphs)>();
        // Larger glyphs first, so a line's baseline is set by its body text and sub/superscripts (CO₂) join it.
        foreach (var g in glyphs.OrderByDescending(g => g.Size).ThenBy(g => g.Y))
        {
            var index = lines.FindIndex(l => Math.Abs(l.Y - g.Y) <= Math.Max(l.Glyphs[0].Size, g.Size) * 0.45);
            if (index < 0)
                lines.Add((g.Y, new List<Glyph> { g }));
            else
                lines[index].Glyphs.Add(g);
        }
        var text = new List<string>();
        foreach (var (_, line) in lines.OrderBy(l => l.Y))
        {
            var ordered = line.OrderBy(g => g.X).ToList();
            var sb = new System.Text.StringBuilder();
            Glyph? previous = null;
            foreach (var g in ordered)
            {
                if (g.IsSpace)
                {
                    sb.Append(' ');
                    continue;
                }
                // Duplicate glyphs drawn twice for a bold effect sit on top of each other: keep one.
                if (previous is { } p && Math.Abs(g.X - p.X) < g.Size * 0.05 && g.Text == p.Text)
                    continue;
                // A wide gap separates two columns printed side by side: marked with a tab for the guide parser.
                if (previous is { } c && g.X - c.Right > g.Size * 2.5)
                    sb.Append('\t');
                else if (previous is { } q && g.X - q.Right > g.Size * 0.18)
                    sb.Append(' ');
                sb.Append(g.Text);
                previous = g;
            }
            var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"[ \r\n]+", " ").Trim(' ');
            var firstInk = ordered.FirstOrDefault(g => !g.IsSpace);
            if (pageWidth > 0 && firstInk.Text != null && firstInk.X > pageWidth * 0.45)
                s = "\t" + s;
            s = System.Text.RegularExpressions.Regex.Replace(s, @" ?\t ?", "\t").Trim(' ');
            if (s.Length > 0)
                text.Add(s);
        }
        return text;
    }

    public static void Dump(IEnumerable<ErgPage> pages, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var p in pages)
        {
            File.WriteAllLines(Path.Combine(folder, $"page-{p.Number:000}.txt"), p.Lines);
            if (p.ColumnLines != null)
                File.WriteAllLines(Path.Combine(folder, $"page-{p.Number:000}.columns.txt"), p.ColumnLines);
        }
    }

    /// <summary>Reads pages written by <see cref="Dump"/> (possibly hand-corrected).</summary>
    public static List<ErgPage> Load(string folder)
    {
        var pages = new List<ErgPage>();
        foreach (var file in Directory.GetFiles(folder, "page-*.txt").Where(f => !f.EndsWith(".columns.txt", StringComparison.Ordinal)).Order())
        {
            var number = int.Parse(Path.GetFileNameWithoutExtension(file)["page-".Length..]);
            var columnsFile = Path.ChangeExtension(file, null) + ".columns.txt";
            pages.Add(new ErgPage(number, File.ReadAllLines(file), File.Exists(columnsFile) ? File.ReadAllLines(columnsFile) : null));
        }
        return pages;
    }
}
