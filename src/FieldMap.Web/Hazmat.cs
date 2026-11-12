using System.Security.Cryptography;

namespace FieldMap.Web;

/// <summary>
/// The ERG 2024 hazmat data (the Field Toolkit's erg.json: guides, materials, Table 1 isolation and protective-action
/// distances, placards). Phones download it once, keep it for offline use and search it themselves.
/// </summary>
public static class HazmatApi
{
    public static void Map(WebApplication app)
    {
        var env = app.Services.GetRequiredService<IWebHostEnvironment>();
        var path = new[] { Path.Combine(env.ContentRootPath, "Content", "erg.json"), Path.Combine(AppContext.BaseDirectory, "Content", "erg.json") }.FirstOrDefault(File.Exists);
        byte[]? bytes = path == null ? null : File.ReadAllBytes(path);
        var etag = bytes == null ? "" : "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"";

        app.MapGet("/api/erg", (HttpContext http) =>
        {
            if (bytes == null)
                return Results.Json(new { error = "The hazmat data isn't installed on this server." }, statusCode: 404);
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, no-cache";
            if (http.Request.Headers.IfNoneMatch == etag)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(bytes, "application/json");
        });
    }
}
