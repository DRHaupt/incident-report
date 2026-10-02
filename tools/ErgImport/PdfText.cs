using FieldToolkit.Core.Hazmat;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ErgImport;

/// <summary>Extracts page text from the ERG PDF as lines, both full-width and column by column.</summary>
internal static class PdfText
{
    public static List<ErgPage> Extract(string pdfPath, Action<int, int>? progress = null)
    {
        var pages = new List<ErgPage>();
        using var doc = PdfDocument.Open(pdfPath);
        var total = doc.NumberOfPages;
        foreach (var page in doc.GetPages())
        {
            var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            var full = ToLines(words);
            var mid = page.Width / 2;
            var columns = ToLines(words.Where(w => w.BoundingBox.Centroid.X < mid).ToList())
                .Concat(ToLines(words.Where(w => w.BoundingBox.Centroid.X >= mid).ToList()))
                .ToList();
            pages.Add(new ErgPage(page.Number, full, columns));
            progress?.Invoke(page.Number, total);
        }
        return pages;
    }

    /// <summary>Groups words into lines by baseline (top to bottom), then orders each line left to right.</summary>
    private static List<string> ToLines(List<Word> words)
    {
        var lines = new List<(double Y, double Height, List<Word> Words)>();
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var height = Math.Max(1, w.BoundingBox.Height);
            var line = lines.FirstOrDefault(l => Math.Abs(l.Y - w.BoundingBox.Bottom) < Math.Min(l.Height, height) * 0.5);
            if (line.Words == null)
                lines.Add((w.BoundingBox.Bottom, height, new List<Word> { w }));
            else
                line.Words.Add(w);
        }
        return lines
            .OrderByDescending(l => l.Y)
            .Select(l => string.Join(" ", l.Words.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
            .ToList();
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
