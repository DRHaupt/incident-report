using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FieldToolkit.Core.FirstAid;

namespace FieldMap.Web;

/// <summary>
/// The Field Toolkit's first aid guide and CPR helper settings, loaded and checked with the toolkit's own loader, with
/// the emergency numbers filled in. Phones download it once as one bundle, keep it for offline use and search it
/// themselves.
/// </summary>
public static class FirstAidApi
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>The bundle phones get: guide settings, CPR settings and every topic in display order.</summary>
    public static byte[]? Bundle(string folder)
    {
        if (!Directory.Exists(folder))
            return null;
        var lib = FirstAidLibrary.Load(folder);
        if (lib.Topics.Count == 0)
            return null;
        var json = JsonSerializer.Serialize(new
        {
            guide = new
            {
                lib.Guide.Title, lib.Guide.ContentVersion, lib.Guide.Disclaimer, lib.Guide.Review, lib.Guide.EmergencyNumber,
                lib.Guide.PoisonCentre, lib.Guide.CrisisLine, lib.Guide.Synonyms
            },
            cpr = lib.Cpr,
            topics = lib.Topics,
            problems = lib.Problems.Select(p => p.ToString())
        }, Json);
        return Encoding.UTF8.GetBytes(lib.Expand(json));
    }

    public static void Map(WebApplication app)
    {
        var env = app.Services.GetRequiredService<IWebHostEnvironment>();
        var folder = new[] { Path.Combine(env.ContentRootPath, "Content", "firstaid"), Path.Combine(AppContext.BaseDirectory, "Content", "firstaid") }.FirstOrDefault(Directory.Exists);
        var bytes = folder == null ? null : Bundle(folder);
        var etag = bytes == null ? "" : "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"";

        app.MapGet("/api/firstaid", (HttpContext http) =>
        {
            if (bytes == null)
                return Results.Json(new { error = "The first aid guide isn't installed on this server." }, statusCode: 404);
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, no-cache";
            if (http.Request.Headers.IfNoneMatch == etag)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(bytes, "application/json");
        });
    }
}
