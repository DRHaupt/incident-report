namespace FieldMap.Core.Interop;

/// <summary>A point passed between Field Toolkit and Field Map.</summary>
public sealed class ExchangeMessage
{
    public string Type { get; set; } = "point";
    public string Name { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Note { get; set; } = "";
    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
    public string From { get; set; } = "";
}

/// <summary>
/// The local mechanism the two field apps use to hand points to each other: small JSON files in
/// %LOCALAPPDATA%\FieldSuite\exchange\to-fieldmap and …\to-fieldtoolkit. Writers create "*.tmp" and rename it, so a
/// reader never sees half a message; readers import each "*.json" and delete it. See docs/FIELDMAP-ARCHITECTURE.md.
/// </summary>
public static class Exchange
{
    public const string ToFieldMap = "to-fieldmap";
    public const string ToFieldToolkit = "to-fieldtoolkit";

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FieldSuite", "exchange");

    public static string Inbox(string name, string? root = null) => Path.Combine(root ?? Root, name);

    public static string Send(string inbox, ExchangeMessage message)
    {
        Directory.CreateDirectory(inbox);
        var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}";
        var temp = Path.Combine(inbox, name + ".tmp");
        var path = Path.Combine(inbox, name + ".json");
        File.WriteAllText(temp, Json.Serialize(message));
        File.Move(temp, path);
        return path;
    }

    /// <summary>Reads and removes waiting messages, oldest first. Unreadable files are moved aside as *.bad.</summary>
    public static List<ExchangeMessage> Receive(string inbox)
    {
        var list = new List<ExchangeMessage>();
        if (!Directory.Exists(inbox))
            return list;
        foreach (var file in Directory.GetFiles(inbox, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var msg = Json.Deserialize<ExchangeMessage>(File.ReadAllText(file));
                if (msg.Lat is >= -90 and <= 90 && msg.Lon is >= -180 and <= 180)
                    list.Add(msg);
                File.Delete(file);
            }
            catch (IOException)
            {
                // Still being written or locked: try again next time.
            }
            catch (Exception)
            {
                try { File.Move(file, file + ".bad", overwrite: true); } catch (IOException) { }
            }
        }
        return list;
    }
}
