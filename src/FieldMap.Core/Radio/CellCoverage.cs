using System.Globalization;
using FieldMap.Core.Build;
using FieldMap.Core.Geo;
using FieldMap.Core.Overlays;

namespace FieldMap.Core.Radio;

/// <summary>What one cell-coverage import contained.</summary>
public sealed record CellCoverageSummary(int Areas, IReadOnlyDictionary<string, int> ByKind, int Tiles, IReadOnlyList<string> Warnings);

/// <summary>
/// Mobile (cell) coverage from the Government of Canada's published coverage maps (CRTC / ISED – LTE and 5G, all
/// carriers combined), imported from the KML/KMZ/GeoJSON files and turned into a small tileset so even national
/// polygons draw quickly. The maps are what carriers report: coverage on the map is not a guarantee of service.
/// </summary>
public static class CellCoverage
{
    public const string FileName = "cell-coverage.mbtiles";
    public const string Folder = "coverage";

    public static readonly string[] Kinds = { "5g", "lte", "3g", "other" };

    public static string KindLabel(string kind) => kind switch
    {
        "5g" => "5G",
        "lte" => "LTE (4G)",
        "3g" => "3G / HSPA",
        _ => "Other / unknown"
    };

    /// <summary>The technology a coverage area is for, from its name, folder, description, properties or the file name.</summary>
    public static string Classify(OverlayFeature f, string fileName)
    {
        string? Match(string text)
        {
            var t = text.ToUpperInvariant();
            if (t.Contains("5G") || t.Contains("NR ") || t.EndsWith(" NR", StringComparison.Ordinal))
                return "5g";
            if (t.Contains("LTE") || t.Contains("4G"))
                return "lte";
            if (t.Contains("HSPA") || t.Contains("3G") || t.Contains("UMTS"))
                return "3g";
            return null;
        }
        return Match(f.Name) ?? Match(f.Folder) ?? Match(string.Join(" ", f.Properties.Values)) ?? Match(f.Description) ?? Match(Path.GetFileNameWithoutExtension(fileName)) ?? "other";
    }

    /// <summary>
    /// Reads the coverage files and writes the coverage tileset to <paramref name="outputPath"/>. Areas outside
    /// <paramref name="clip"/> (the installed map's area) are left out so a national file stays small.
    /// </summary>
    public static CellCoverageSummary Build(IEnumerable<string> files, string outputPath, GeoBounds? clip, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var areas = new List<MapArea>();
        var warnings = new List<string>();
        var byKind = new Dictionary<string, int>();
        foreach (var file in files)
        {
            cancel.ThrowIfCancellationRequested();
            var content = MapFileImporter.LoadLarge(file, 50_000_000);
            warnings.AddRange(content.Warnings.Select(w => $"{Path.GetFileName(file)}: {w}"));
            foreach (var f in content.Features.Where(f => f.Geometry == OverlayGeometry.Polygon && f.Parts.Count > 0 && f.Parts[0].Count >= 4))
            {
                if (clip is { } c && !f.Bounds.Intersects(c))
                    continue;
                var kind = Classify(f, file);
                var area = new MapArea { Kind = kind, Name = null };
                area.Outers.Add(f.Parts[0].ToArray());
                foreach (var hole in f.Parts.Skip(1))
                    area.Inners.Add(hole.ToArray());
                areas.Add(area);
                byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
            }
        }
        if (areas.Count == 0)
            throw new InvalidDataException("No coverage areas (polygons) were found" + (clip != null ? " over the installed map area." : "."));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temp = outputPath + ".partial";
        if (File.Exists(temp))
            File.Delete(temp);
        var tiles = TileBuilder.BuildCoverageMbTiles(areas, temp, new Dictionary<string, string>
        {
            ["name"] = "Cell coverage",
            ["description"] = string.Join(", ", files.Select(Path.GetFileName)),
            ["built"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        }, progress: progress, cancel: cancel);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Move(temp, outputPath, overwrite: true);
        return new CellCoverageSummary(areas.Count, byKind, tiles, warnings);
    }
}
