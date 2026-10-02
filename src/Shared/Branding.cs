using System.Reflection;

namespace Shared;

/// <summary>The palette shared by every app in the suite (navy and amber shield family).</summary>
public static class Brand
{
    public static readonly Color Navy = Color.FromArgb(0x2B, 0x3A, 0x4F);
    public static readonly Color Amber = Color.FromArgb(0xF2, 0xA9, 0x00);
    public static readonly Color Panel = Color.FromArgb(0xEE, 0xF1, 0xF5);
    public static readonly Color Danger = Color.FromArgb(0xB3, 0x1B, 0x1B);
    public static readonly Color Success = Color.FromArgb(0x1E, 0x7B, 0x3C);
    public static readonly Color Muted = Color.FromArgb(0x5C, 0x67, 0x75);
}

/// <summary>Loads an app's logo and icon files that are embedded in its executable.</summary>
public sealed class BrandAssets
{
    private readonly Assembly _assembly;
    private readonly string _prefix;
    private readonly Dictionary<string, byte[]> _bytes = new();
    private readonly Dictionary<string, Image> _images = new();
    private readonly Dictionary<string, Icon> _icons = new();

    /// <param name="assembly">Assembly containing the embedded files.</param>
    /// <param name="resourcePrefix">Manifest resource name prefix, e.g. "IncidentReporting.Resources.".</param>
    public BrandAssets(Assembly assembly, string resourcePrefix)
    {
        _assembly = assembly;
        _prefix = resourcePrefix;
    }

    public Icon Icon(string fileName)
    {
        lock (_icons)
            return _icons.TryGetValue(fileName, out var i) ? i : _icons[fileName] = new Icon(new MemoryStream(Bytes(fileName)));
    }

    public Image Image(string fileName)
    {
        lock (_images)
            return _images.TryGetValue(fileName, out var i) ? i : _images[fileName] = new Bitmap(new MemoryStream(Bytes(fileName)));
    }

    public bool Exists(string fileName) => _assembly.GetManifestResourceInfo(_prefix + fileName) != null;

    public string DataUri(string fileName) => "data:image/png;base64," + Convert.ToBase64String(Bytes(fileName));

    public byte[] Bytes(string fileName)
    {
        lock (_bytes)
        {
            if (_bytes.TryGetValue(fileName, out var cached))
                return cached;
            using var stream = _assembly.GetManifestResourceStream(_prefix + fileName)
                ?? throw new InvalidOperationException($"Embedded resource '{fileName}' is missing.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return _bytes[fileName] = ms.ToArray();
        }
    }
}
