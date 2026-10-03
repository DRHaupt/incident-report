using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shared.Core.Update;

/// <summary>Fixed facts about the Field Suite installer that the updater needs.</summary>
public static class SuiteInfo
{
    public const string ProductName = "Field Suite";

    /// <summary>The MSI UpgradeCode (installer/Package.wxs); identifies Field Suite whatever its version.</summary>
    public const string UpgradeCode = "{D444599E-086F-4FD3-AF42-D9C3FC062904}";

    /// <summary>Process names of the suite's programs (closed before an update installs).</summary>
    public static readonly string[] ProcessNames = { "IncidentReporting", "FieldToolkit", "FieldMap", "MapBuilder" };

    /// <summary>Folder name looked for at the root of USB sticks and inside update folders.</summary>
    public const string UpdateFolderName = "FieldSuite-Updates";

    /// <summary>Parses "0.1.57" (or "v0.1.57") into a three-part version; null if it isn't one.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var v) || v.Major < 0)
            return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }
}

/// <summary>
/// Describes one installer release: published next to the MSI as <c>fieldsuite-update.json</c> (on a USB stick, a
/// network share, a web server or a GitHub release). The SHA-256 ties it to exactly one MSI; the optional signature
/// (ECDSA P-256, made by the release build) proves it came from whoever holds the suite's signing key.
/// </summary>
public sealed class UpdateManifest
{
    public const string FileName = "fieldsuite-update.json";
    private const int MaxJsonBytes = 64 * 1024;

    public string Product { get; set; } = SuiteInfo.ProductName;
    public string Version { get; set; } = "";
    /// <summary>Installer file name, in the same folder/release as the manifest.</summary>
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTime PublishedUtc { get; set; }
    public string Notes { get; set; } = "";
    /// <summary>Base64 ECDSA P-256 signature over <see cref="SignedBytes"/>; null when the release isn't signed.</summary>
    public string? Signature { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Version ParsedVersion => SuiteInfo.ParseVersion(Version) ?? new Version(0, 0, 0);

    /// <summary>Exactly what the signature covers: every field except the signature, in a fixed text form.</summary>
    public byte[] SignedBytes() => Encoding.UTF8.GetBytes(string.Join("\n",
        "FieldSuiteUpdate/1", Product, Version, File, Sha256.ToLowerInvariant(), Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PublishedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"), Notes.Replace("\r\n", "\n")));

    public string ToJson() => JsonSerializer.Serialize(this, Json.Options);

    /// <summary>Reads and checks a manifest; throws <see cref="InvalidDataException"/> with a plain reason if it's unusable.</summary>
    public static UpdateManifest Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            throw new InvalidDataException("The update description is too large.");
        UpdateManifest? m;
        try
        {
            m = JsonSerializer.Deserialize<UpdateManifest>(json, Json.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The update description is not valid: " + ex.Message);
        }
        if (m == null)
            throw new InvalidDataException("The update description is empty.");
        m.Validate();
        return m;
    }

    public void Validate()
    {
        if (!string.Equals(Product, SuiteInfo.ProductName, StringComparison.Ordinal))
            throw new InvalidDataException($"The update is for \"{Product}\", not {SuiteInfo.ProductName}.");
        if (SuiteInfo.ParseVersion(Version) == null)
            throw new InvalidDataException($"The update version \"{Version}\" is not valid.");
        // Only a bare .msi file name: never a path that could point somewhere else.
        if (!Regex.IsMatch(File, @"^[A-Za-z0-9._-]{1,120}\.msi$", RegexOptions.CultureInvariant) || File.Contains(".."))
            throw new InvalidDataException($"The installer name \"{File}\" is not allowed.");
        if (!Regex.IsMatch(Sha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The installer checksum is missing or not valid.");
        if (Size <= 0 || Size > 4L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The installer size is not valid.");
    }

    /// <summary>Builds a manifest for an installer file (used by the release build).</summary>
    public static UpdateManifest ForFile(string msiPath, string version, string notes, DateTime publishedUtc)
    {
        using var stream = System.IO.File.OpenRead(msiPath);
        var m = new UpdateManifest
        {
            Version = SuiteInfo.ParseVersion(version)?.ToString(3) ?? throw new ArgumentException("Not a version: " + version),
            File = Path.GetFileName(msiPath),
            Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            Size = new FileInfo(msiPath).Length,
            PublishedUtc = DateTime.SpecifyKind(publishedUtc.ToUniversalTime().AddTicks(-(publishedUtc.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Utc),
            Notes = notes.Trim()
        };
        m.Validate();
        return m;
    }
}

/// <summary>
/// Release signing keys. The private key (PKCS#8, base64) lives only in the build's secret store; the public key
/// (SubjectPublicKeyInfo, base64) is written into <c>update-keys.txt</c> next to each program when the release is built.
/// </summary>
public static class UpdateSigning
{
    public static (string PrivateKey, string PublicKey) CreateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportPkcs8PrivateKey()), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    public static string PublicKeyOf(string privateKey)
    {
        using var key = Import(privateKey);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public static void Sign(UpdateManifest manifest, string privateKey)
    {
        using var key = Import(privateKey);
        manifest.Signature = Convert.ToBase64String(key.SignData(manifest.SignedBytes(), HashAlgorithmName.SHA256));
    }

    public static bool Verify(UpdateManifest manifest, string publicKey)
    {
        if (string.IsNullOrEmpty(manifest.Signature))
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
            return key.VerifyData(manifest.SignedBytes(), Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static ECDsa Import(string privateKey)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey.Trim()), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            key.Dispose();
            throw new InvalidDataException("The signing key is not a base64 PKCS#8 EC private key.", ex);
        }
        if (key.KeySize != 256)
        {
            key.Dispose();
            throw new InvalidDataException("The signing key must be an ECDSA P-256 key.");
        }
        return key;
    }
}

/// <summary>Whether an update may be installed.</summary>
public enum UpdateTrustResult
{
    /// <summary>Signed with one of this installation's release keys.</summary>
    Signed,
    /// <summary>Not signed, and this installation has no release key, so integrity is checked by SHA-256 only.</summary>
    Unsigned,
    /// <summary>This installation requires signed updates and the signature is missing or wrong.</summary>
    Rejected
}

/// <summary>
/// The release keys this installation trusts (<c>update-keys.txt</c> next to the program: one base64 public key per
/// line, # for comments). With at least one key, only updates signed by one of them are accepted.
/// </summary>
public sealed class UpdateTrust
{
    public const string FileName = "update-keys.txt";

    public UpdateTrust(IEnumerable<string> publicKeys) => PublicKeys = publicKeys.Select(k => k.Trim()).Where(k => k.Length > 0).ToList();

    public IReadOnlyList<string> PublicKeys { get; }
    public bool RequiresSignature => PublicKeys.Count > 0;

    public static UpdateTrust Load(string path)
    {
        if (!System.IO.File.Exists(path))
            return new UpdateTrust(Array.Empty<string>());
        return new UpdateTrust(System.IO.File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith('#')));
    }

    public UpdateTrustResult Check(UpdateManifest manifest)
    {
        if (PublicKeys.Any(k => UpdateSigning.Verify(manifest, k)))
            return UpdateTrustResult.Signed;
        return RequiresSignature ? UpdateTrustResult.Rejected : UpdateTrustResult.Unsigned;
    }
}
