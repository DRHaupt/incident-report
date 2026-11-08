using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace FieldMap.Web;

/// <summary>
/// Optional shared access code. Phones enter it once; the server answers with a long-lived HttpOnly cookie derived from
/// the code, so changing the code signs every phone out. Repeated wrong codes from one address are slowed down.
/// </summary>
public sealed class AccessGate
{
    public const string CookieName = "fm_access";
    private const int MaxFailures = 8;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly string? _code;
    private readonly string? _token;
    private readonly ConcurrentDictionary<string, (int Count, DateTime Since)> _failures = new();

    public AccessGate(WebOptions options)
    {
        _code = string.IsNullOrWhiteSpace(options.AccessCode) ? null : options.AccessCode.Trim();
        if (_code != null)
            _token = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_code), Encoding.UTF8.GetBytes("fieldmap-web-access-v1")))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public bool Enabled => _code != null;

    public bool IsAllowed(HttpContext http) =>
        !Enabled || (http.Request.Cookies.TryGetValue(CookieName, out var c) && FixedEquals(c, _token!));

    public static bool IsProtectedPath(PathString path) =>
        (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/login") && !path.StartsWithSegments("/api/status")) ||
        path.StartsWithSegments("/tiles") || path.StartsWithSegments("/imagery");

    /// <summary>Checks a code. Returns null when accepted, or the reason it wasn't.</summary>
    public string? TryLogin(HttpContext http, string? code)
    {
        if (!Enabled)
            return null;
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "?";
        var now = DateTime.UtcNow;
        if (_failures.TryGetValue(ip, out var f) && now - f.Since < Window && f.Count >= MaxFailures)
            return "Too many wrong codes. Wait 10 minutes and try again.";
        if (code != null && FixedEquals(code.Trim(), _code!))
        {
            _failures.TryRemove(ip, out _);
            http.Response.Cookies.Append(CookieName, _token!, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                MaxAge = TimeSpan.FromDays(400),
                IsEssential = true
            });
            return null;
        }
        _failures.AddOrUpdate(ip, _ => (1, now), (_, old) => now - old.Since > Window ? (1, now) : (old.Count + 1, old.Since));
        return "That code isn't right.";
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
