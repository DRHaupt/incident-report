namespace IncidentReporting;

/// <summary>Product name, colors and logo artwork embedded in the executable.</summary>
internal static class Branding
{
    public const string ProductName = "Incident Report Manager";

    public static readonly Color Navy = Color.FromArgb(0x2B, 0x3A, 0x4F);
    public static readonly Color Amber = Color.FromArgb(0xF2, 0xA9, 0x00);

    private static Icon? _icon;
    private static Image? _logo48;
    private static Image? _logo128;

    public static Icon AppIcon => _icon ??= new Icon(new MemoryStream(Resource("app.ico")));

    public static Image Logo48 => _logo48 ??= new Bitmap(new MemoryStream(Resource("logo-48.png")));

    public static Image Logo128 => _logo128 ??= new Bitmap(new MemoryStream(Resource("logo-128.png")));

    /// <summary>The logo as a data URI for HTML output (preview pane and exported documents).</summary>
    public static string LogoDataUri(int size) =>
        "data:image/png;base64," + Convert.ToBase64String(Resource(size >= 128 ? "logo-128.png" : "logo-48.png"));

    private static readonly Dictionary<string, byte[]> _cache = new();

    private static byte[] Resource(string fileName)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(fileName, out var cached))
                return cached;
            using var stream = typeof(Branding).Assembly.GetManifestResourceStream("IncidentReporting.Resources." + fileName)
                ?? throw new InvalidOperationException($"Embedded resource '{fileName}' is missing.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return _cache[fileName] = ms.ToArray();
        }
    }
}
