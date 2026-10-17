using FieldMap.Core.Geo;

namespace FieldMap.Core.Overlays;

/// <summary>An imported map file kept as a toggleable layer.</summary>
public sealed class OverlayLayerInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>The copy kept in the overlays folder.</summary>
    public string FileName { get; set; } = "";

    public string OriginalPath { get; set; } = "";
    public DateTime ImportedUtc { get; set; }
    public bool Visible { get; set; } = true;

    /// <summary>Opacity of map images (ground overlays), 0.1–1.</summary>
    public float ImageOpacity { get; set; } = 0.8f;

    public string Summary { get; set; } = "";
}

/// <summary>A loaded layer: its settings and what the file contains.</summary>
public sealed record OverlayLayer(OverlayLayerInfo Info, MapFileContent Content);

/// <summary>
/// Event layers: imported map files copied into one folder (so the originals can go away, e.g. a USB stick), listed in
/// layers.json, and loaded at start-up. Each can be shown, hidden, faded or removed.
/// </summary>
public sealed class OverlayLibrary
{
    private readonly JsonListStore<OverlayLayerInfo> _index;
    private readonly Dictionary<Guid, MapFileContent> _loaded = new();

    public OverlayLibrary(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        _index = new JsonListStore<OverlayLayerInfo>(Path.Combine(folder, "layers.json"));
        foreach (var info in _index.Items.ToList())
        {
            try
            {
                _loaded[info.Id] = MapFileImporter.Load(Path.Combine(folder, info.FileName));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                Problems.Add($"{info.Name}: {ex.Message}");
            }
        }
    }

    public string Folder { get; }

    /// <summary>Layers that couldn't be loaded at start-up.</summary>
    public List<string> Problems { get; } = new();

    public event EventHandler? Changed;

    public IReadOnlyList<OverlayLayer> Layers =>
        _index.Items.Where(i => _loaded.ContainsKey(i.Id)).Select(i => new OverlayLayer(i, _loaded[i.Id])).ToList();

    public IReadOnlyList<OverlayLayer> VisibleLayers => Layers.Where(l => l.Info.Visible).ToList();

    /// <summary>Reads a map file and keeps a copy as a new layer. Throws if the file can't be read.</summary>
    public OverlayLayer Import(string path)
    {
        var content = MapFileImporter.Load(path);
        if (content.Features.Count == 0 && content.GroundOverlays.Count == 0)
            throw new InvalidDataException($"{Path.GetFileName(path)}: no points, lines, areas or map images found." +
                                           (content.Warnings.Count > 1 ? " " + string.Join(" ", content.Warnings.Skip(1)) : ""));
        var info = new OverlayLayerInfo
        {
            Name = content.Name,
            OriginalPath = path,
            ImportedUtc = DateTime.UtcNow,
            Summary = content.Summary
        };
        info.FileName = info.Id.ToString("N")[..8] + "-" + Safe(Path.GetFileName(path));
        File.Copy(path, Path.Combine(Folder, info.FileName));
        _loaded[info.Id] = content;
        _index.Items.Add(info);
        Save();
        return new OverlayLayer(info, content);
    }

    public void SetVisible(Guid id, bool visible) => Update(id, i => i.Visible = visible);

    public void SetImageOpacity(Guid id, float opacity) => Update(id, i => i.ImageOpacity = Math.Clamp(opacity, 0.1f, 1f));

    public void Rename(Guid id, string name) => Update(id, i => i.Name = name.Trim().Length > 0 ? name.Trim() : i.Name);

    private void Update(Guid id, Action<OverlayLayerInfo> change)
    {
        if (_index.Items.FirstOrDefault(i => i.Id == id) is { } info)
        {
            change(info);
            Save();
        }
    }

    public void Remove(Guid id)
    {
        var info = _index.Items.FirstOrDefault(i => i.Id == id);
        if (info == null)
            return;
        _index.Items.Remove(info);
        _loaded.Remove(id);
        try
        {
            File.Delete(Path.Combine(Folder, info.FileName));
        }
        catch (IOException)
        {
            // Left behind; harmless.
        }
        Save();
    }

    private void Save()
    {
        _index.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

/// <summary>A feature of an imported event layer that matched a search.</summary>
public sealed record OverlayMatch(OverlayLayer Layer, OverlayFeature Feature, double Score, string MatchedIn);

/// <summary>Searching imported event layers by name, folder, description and attribute values.</summary>
public static class OverlaySearch
{
    /// <summary>
    /// Features whose name matches best (exact, starts with, contains, or close spelling), then those matching in their
    /// folder, description or attributes. Hidden layers are searched too. Ties go to the nearer feature.
    /// </summary>
    public static List<OverlayMatch> Search(IEnumerable<OverlayLayer> layers, string query, GeoPoint? near = null, int limit = 30)
    {
        var q = FieldMap.Core.Search.TextNormalizer.Normalize(query);
        var results = new List<OverlayMatch>();
        if (q.Length == 0)
            return results;
        foreach (var layer in layers)
            foreach (var f in layer.Content.Features)
            {
                if (f.Parts.Count == 0 || f.Parts[0].Count == 0)
                    continue;
                var name = FieldMap.Core.Search.TextNormalizer.Normalize(f.Name);
                double score;
                string where;
                if (name == q)
                    (score, where) = (1.0, "name");
                else if (name.StartsWith(q + " ", StringComparison.Ordinal) || name.StartsWith(q, StringComparison.Ordinal) && q.Length >= 3)
                    (score, where) = (0.95, "name");
                else if (q.Length >= 2 && (" " + name + " ").Contains(" " + q + " "))
                    (score, where) = (0.92, "name");
                else if (q.Length >= 3 && name.Contains(q))
                    (score, where) = (0.88, "name");
                else if (name.Length > 0 && FieldMap.Core.Search.TextNormalizer.Similarity(q, name) is var sim and >= 0.72)
                    (score, where) = (0.6 + sim * 0.3, "name");
                else if (q.Length >= 3 && FieldMap.Core.Search.TextNormalizer.Normalize(f.Folder).Contains(q))
                    (score, where) = (0.7, "folder " + f.Folder);
                else if (q.Length >= 3 && f.Properties.FirstOrDefault(p => FieldMap.Core.Search.TextNormalizer.Normalize(p.Value).Contains(q)) is { Key: { } key })
                    (score, where) = (0.65, key);
                else if (q.Length >= 3 && FieldMap.Core.Search.TextNormalizer.Normalize(f.Description).Contains(q))
                    (score, where) = (0.6, "description");
                else
                    continue;
                if (near is { } n && n != default)
                    score -= Math.Min(0.05, n.DistanceTo(f.Anchor) / 1_000_000);
                results.Add(new OverlayMatch(layer, f, score, where));
            }
        return results.OrderByDescending(r => r.Score).Take(limit).ToList();
    }
}
