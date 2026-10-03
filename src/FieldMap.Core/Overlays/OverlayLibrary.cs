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
