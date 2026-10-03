using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ErgImport;
using FieldToolkit.Core.Hazmat;

// ErgImport – converts the official ERG PDF to erg.json and helps verify it. See tools/ErgImport/README.md.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    return Usage();

try
{
    return args[0] switch
    {
        "pdf" => ImportPdf(args.Skip(1).ToArray()),
        "text" => ImportText(args.Skip(1).ToArray()),
        "verify" => Verify(args.Skip(1).ToArray()),
        _ => Usage()
    };
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

static int Usage()
{
    Console.WriteLine("""
        ErgImport – build the Field Toolkit hazmat dataset from the official ERG

          ErgImport pdf <ERG2024-Eng-Web.pdf> [--out erg.json] [--placards placards.csv] [--dump-text <folder>]
              Extract text from the PDF, parse the ID list, name list, guides and Table 1/2, and write erg.json.
              --dump-text writes the extracted page text so it can be inspected or hand-corrected.

          ErgImport text <folder> --source <ERG.pdf> [--out erg.json] [--placards placards.csv]
              Same as 'pdf', but parse page text previously written with --dump-text (after any corrections).

          ErgImport verify <erg.json> [--sample 25] [--seed 1] [--report verification.md]
              Check the dataset for consistency and write a checklist of randomly chosen entries, with PDF page
              numbers, to compare against the printed guidebook.
        """);
    return 2;
}

static string? Option(string[] a, string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;

static int ImportPdf(string[] a)
{
    if (a.Length == 0 || !File.Exists(a[0]))
        throw new ArgumentException("Give the path of the ERG PDF.");
    Console.WriteLine($"Reading {a[0]} …");
    var pages = PdfText.Extract(a[0], (n, total) => { if (n % 50 == 0 || n == total) Console.WriteLine($"  page {n}/{total}"); });
    if (Option(a, "--dump-text") is { } dump)
    {
        PdfText.Dump(pages, dump);
        Console.WriteLine($"Page text written to {dump}");
    }
    return Build(pages, a[0], a);
}

static int ImportText(string[] a)
{
    if (a.Length == 0 || !Directory.Exists(a[0]))
        throw new ArgumentException("Give the folder written by --dump-text.");
    var source = Option(a, "--source") ?? throw new ArgumentException("--source <ERG.pdf> is required so the dataset records which file it came from.");
    return Build(PdfText.Load(a[0]), source, a);
}

static int Build(List<ErgPage> pages, string sourcePdf, string[] a)
{
    var result = ErgTextParser.Parse(pages);
    var data = result.Data;
    data.Edition = DetectEdition(pages);
    data.IsSample = false;
    data.Source = Path.GetFileName(sourcePdf);
    data.SourceSha256 = File.Exists(sourcePdf) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePdf))) : "";
    data.ImportedOn = DateTime.Now.ToString("yyyy-MM-dd");
    data.ImportTool = "ErgImport " + typeof(ErgTextParser).Assembly.GetName().Version;
    data.Notes = "Converted from the U.S. DOT/PHMSA Emergency Response Guidebook. Text as printed. Table 3 (large-spill distances for six common gases) is not imported; refer to the printed guidebook.";
    data.Placards = ErgPlacards.StandardClasses();
    var placardFile = Option(a, "--placards") ?? Path.Combine(AppContext.BaseDirectory, "placards.csv");
    if (File.Exists(placardFile))
        result.Warnings.AddRange(ErgPlacards.ApplyGuides(data.Placards, File.ReadAllLines(placardFile)));
    else
        result.Warnings.Add("No placards.csv: placard picker will filter by hazard class only.");

    var problems = ErgRepository.Validate(data).ToList();
    var outPath = Option(a, "--out") ?? "erg.json";
    ErgRepository.Save(data, outPath);

    var kinds = result.PageKinds.GroupBy(k => k.Value).ToDictionary(g => g.Key, g => g.Count());
    Console.WriteLine();
    Console.WriteLine($"Edition:           {data.Edition}");
    Console.WriteLine($"Pages by type:     {string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}"))}");
    Console.WriteLine($"Guides:            {data.Guides.Count} ({data.Guides.FirstOrDefault()?.Number}–{data.Guides.LastOrDefault()?.Number})");
    Console.WriteLine($"Materials:         {data.Materials.Count} (ID list), {result.NameListEntries.Count} (name list)");
    Console.WriteLine($"Table 1 entries:   {data.IsolationTable.Count}");
    Console.WriteLine($"Water-reactive:    {data.Materials.Count(m => m.WaterReactive)}");
    Console.WriteLine($"Written:           {Path.GetFullPath(outPath)}");

    var warningsFile = Path.ChangeExtension(outPath, ".import-log.txt");
    File.WriteAllLines(warningsFile, new[] { "# Parser warnings" }.Concat(result.Warnings).Append("").Append("# Validation problems").Concat(problems));
    Console.WriteLine($"Warnings:          {result.Warnings.Count}, validation problems: {problems.Count} (see {warningsFile})");
    Console.WriteLine();
    Console.WriteLine("Next: run 'ErgImport verify' and check the sampled entries against the printed guidebook before use.");
    return problems.Count == 0 ? 0 : 3;
}

static string DetectEdition(List<ErgPage> pages)
{
    foreach (var line in pages.Take(10).SelectMany(p => p.Lines))
    {
        var m = Regex.Match(line, @"\bERG\s*(?<y>20\d\d)\b|\b(?<y>20\d\d)\s+EMERGENCY RESPONSE GUIDEBOOK", RegexOptions.IgnoreCase);
        if (m.Success)
            return "ERG" + m.Groups["y"].Value;
    }
    return "ERG (edition not detected)";
}

static int Verify(string[] a)
{
    if (a.Length == 0 || !File.Exists(a[0]))
        throw new ArgumentException("Give the path of erg.json.");
    var data = ErgRepository.Load(a[0]);
    var sample = int.TryParse(Option(a, "--sample"), out var n) ? n : 25;
    var rng = new Random(int.TryParse(Option(a, "--seed"), out var s) ? s : Environment.TickCount);
    var report = Option(a, "--report") ?? Path.ChangeExtension(a[0], ".verification.md");

    var checks = new List<string>();
    void Check(bool ok, string text) => checks.Add((ok ? "PASS  " : "FAIL  ") + text);

    var numbers = data.Guides.Select(g => g.Number).Concat(data.BlankGuides).Select(n => int.TryParse(n, out var x) ? x : 0).ToList();
    Check(data.Guides.Count > 0 && numbers.Max() - numbers.Min() + 1 == numbers.Distinct().Count(),
        $"Guide numbers are continuous ({data.Guides.Count} guides + {data.BlankGuides.Count} printed blank, {numbers.DefaultIfEmpty().Min()}–{numbers.DefaultIfEmpty().Max()})");
    Check(data.GeneralFirstAid?.Subsections.Sum(s => s.Items.Count) > 0, "General First Aid section present");
    Check(data.Guides.All(g => g.Sections.Select(x => x.Heading).SequenceEqual(new[] { "POTENTIAL HAZARDS", "PUBLIC SAFETY", "EMERGENCY RESPONSE" })),
        "Every guide has POTENTIAL HAZARDS, PUBLIC SAFETY and EMERGENCY RESPONSE in that order");
    Check(data.Guides.All(g => g.Title.Length > 0), "Every guide has a title");
    Check(data.Materials.Count > 0, $"Materials present ({data.Materials.Count})");
    Check(data.IsolationTable.Count > 0, $"Table 1 entries present ({data.IsolationTable.Count})");
    Check(data.Placards.All(p => p.Guide.Length > 0), "Every placard has an initial response guide");
    foreach (var p in data.Problems)
        checks.Add("FAIL  " + p);

    var sb = new StringBuilder();
    sb.AppendLine($"# ERG import verification – {data.Edition}");
    sb.AppendLine();
    sb.AppendLine($"Source: {data.Source} (SHA-256 {data.SourceSha256})  ");
    sb.AppendLine($"Imported: {data.ImportedOn} with {data.ImportTool}  ");
    sb.AppendLine($"Checked: {DateTime.Now:yyyy-MM-dd HH:mm}");
    sb.AppendLine();
    sb.AppendLine("## Automatic checks");
    sb.AppendLine();
    foreach (var c in checks)
        sb.AppendLine("- " + c);
    sb.AppendLine();
    sb.AppendLine("## Manual comparison against the printed guidebook");
    sb.AppendLine();
    sb.AppendLine("Open the PDF at each page number and tick each entry that matches exactly.");
    sb.AppendLine();
    sb.AppendLine("### Materials (ID list)");
    sb.AppendLine();
    foreach (var m in data.Materials.OrderBy(_ => rng.Next()).Take(sample).OrderBy(m => m.Id))
        sb.AppendLine($"- [ ] p.{m.Page}: {m.Id} – guide {m.GuideLabel} – {m.Name}{(m.Highlighted ? " (highlighted)" : "")}");
    sb.AppendLine();
    sb.AppendLine("### Table 1 entries");
    sb.AppendLine();
    foreach (var e in data.IsolationTable.OrderBy(_ => rng.Next()).Take(Math.Max(5, sample / 2)).OrderBy(e => e.Id))
        sb.AppendLine($"- [ ] p.{e.Page}: {e.Id} {e.Name} – small: {e.Small.Isolate} / {e.Small.ProtectDay} / {e.Small.ProtectNight}; large: {e.Large.Isolate} / {e.Large.ProtectDay} / {e.Large.ProtectNight}{(e.Note.Length > 0 ? " – " + e.Note : "")}");
    sb.AppendLine();
    sb.AppendLine("### Guides (check every line of each)");
    sb.AppendLine();
    foreach (var g in data.Guides.OrderBy(_ => rng.Next()).Take(Math.Max(3, sample / 8)).OrderBy(g => g.Number))
    {
        sb.AppendLine($"- [ ] Guide {g.Number} – {g.Title} (pages {g.Pages})");
        foreach (var section in g.Sections)
        {
            sb.AppendLine($"  - **{section.Heading}**");
            foreach (var sub in section.Subsections)
            {
                if (sub.Heading.Length > 0)
                    sb.AppendLine($"    - *{sub.Heading}*");
                foreach (var item in sub.Items)
                    sb.AppendLine($"      - {item}");
            }
        }
    }
    File.WriteAllText(report, sb.ToString());

    foreach (var c in checks)
        Console.WriteLine(c);
    Console.WriteLine($"\nChecklist written to {Path.GetFullPath(report)}");
    return checks.Any(c => c.StartsWith("FAIL", StringComparison.Ordinal)) ? 3 : 0;
}
