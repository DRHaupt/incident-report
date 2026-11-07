namespace FieldMap.Web;

/// <summary>Settings from appsettings.json, /etc/fieldmap-web/appsettings.json or FIELDMAP_ environment variables.</summary>
public sealed class WebOptions
{
    /// <summary>The Field Map data folder: the one MapBuilder's <c>--data</c> points at (contains regions/).</summary>
    public string DataDir { get; set; } = "/var/lib/fieldmap";

    /// <summary>Folder of KML/KMZ/GPX/GeoJSON/CSV event files every phone sees. Default: &lt;DataDir&gt;/events.</summary>
    public string? EventsDir { get; set; }

    /// <summary>Only serve these regions (e.g. ["bc"]). Empty: every installed region.</summary>
    public List<string> Regions { get; set; } = new();

    /// <summary>When set, phones must enter this code once before they can see the map or event files.</summary>
    public string? AccessCode { get; set; }

    /// <summary>Largest event file a phone may upload for conversion, in megabytes.</summary>
    public int MaxUploadMb { get; set; } = 25;

    public string EventsFolder => string.IsNullOrWhiteSpace(EventsDir) ? Path.Combine(DataDir, "events") : EventsDir;
}
