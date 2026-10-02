namespace Shared.Core;

/// <summary>A list of records persisted as one JSON file (written atomically after every change).</summary>
public sealed class JsonListStore<T>
{
    private sealed class Envelope
    {
        public List<T> Items { get; set; } = new();
    }

    private readonly string? _path;

    public JsonListStore(string? path)
    {
        _path = path;
        if (path != null && File.Exists(path))
        {
            try
            {
                Items = Json.Deserialize<Envelope>(File.ReadAllText(path)).Items;
                Loaded = true;
            }
            catch (Exception)
            {
                // Keep a copy of the unreadable file instead of overwriting it on the next save.
                LoadError = $"{Path.GetFileName(path)} could not be read; a copy was kept as {Path.GetFileName(path)}.bad";
                try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { }
            }
        }
    }

    public List<T> Items { get; private set; } = new();

    /// <summary>True when an existing file was read (false on first run).</summary>
    public bool Loaded { get; }

    public string? LoadError { get; }

    public event EventHandler? Changed;

    public void Save()
    {
        if (_path != null)
        {
            try
            {
                Json.WriteFileAtomic(_path, Json.Serialize(new Envelope { Items = Items }));
            }
            catch (IOException)
            {
                // Data stays in memory; the next change retries the save.
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
