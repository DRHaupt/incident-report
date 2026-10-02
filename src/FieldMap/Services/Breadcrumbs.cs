namespace FieldMap.Services;

/// <summary>The trail of GPS positions (kept for 24 hours, saved to breadcrumbs.json).</summary>
public sealed class Breadcrumbs
{
    public sealed class Crumb
    {
        public double Lat { get; set; }
        public double Lon { get; set; }
        public DateTime Utc { get; set; }
    }

    private sealed class Envelope
    {
        public List<Crumb> Items { get; set; } = new();
    }

    private readonly string _path;
    private DateTime _lastSave = DateTime.MinValue;

    public Breadcrumbs(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                Items = Json.Deserialize<Envelope>(File.ReadAllText(path)).Items;
        }
        catch (Exception)
        {
            Items = new();
        }
        Prune();
    }

    public List<Crumb> Items { get; private set; } = new();

    public event EventHandler? Changed;

    /// <summary>Adds a fix when it moved at least 10 m (or 60 s passed) since the last one.</summary>
    public void Add(GeoFix fix)
    {
        if (fix.Source.StartsWith("Entered", StringComparison.OrdinalIgnoreCase) || fix.AccuracyMeters > 200)
            return;
        var last = Items.Count > 0 ? Items[^1] : null;
        if (last != null)
        {
            var moved = GeoMath.Distance(last.Lat, last.Lon, fix.Latitude, fix.Longitude);
            if (moved < 10 && (fix.TimestampUtc - last.Utc).TotalSeconds < 60)
                return;
        }
        Items.Add(new Crumb { Lat = fix.Latitude, Lon = fix.Longitude, Utc = fix.TimestampUtc });
        Prune();
        if (DateTime.UtcNow - _lastSave > TimeSpan.FromMinutes(1))
            Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        Items.RemoveAll(c => c.Utc < cutoff);
        if (Items.Count > 20000)
            Items.RemoveRange(0, Items.Count - 20000);
    }

    public void Clear()
    {
        Items.Clear();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        _lastSave = DateTime.UtcNow;
        try
        {
            Json.WriteFileAtomic(_path, Json.Serialize(new Envelope { Items = Items }));
        }
        catch (IOException)
        {
        }
    }
}
