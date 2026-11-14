using System.Security.Cryptography;
using System.Text.Json;
using IncidentReporting.Templates;

namespace FieldMap.Web;

/// <summary>
/// The incident report forms (the same templates as the Windows Incident Report Manager; its source files are compiled
/// into this server). Phones download the catalogue once, keep it for offline use and fill the forms in themselves.
/// Reports stay on the phone that wrote them: nothing is sent back here.
/// </summary>
public static class FormsApi
{
    public static void Map(WebApplication app)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            forms = FormCatalog.All.Select(f => new
            {
                code = f.Code,
                name = f.Name,
                category = f.Category,
                description = f.Description,
                sections = f.Sections.Select(s => new
                {
                    title = s.Title,
                    fields = s.Fields.Select(x => new
                    {
                        key = x.Key,
                        label = x.Label,
                        type = x.Type.ToString(),
                        required = x.Required,
                        options = x.Options,
                        hint = x.Hint
                    })
                })
            })
        });
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"";

        app.MapGet("/api/forms", (HttpContext http) =>
        {
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "private, no-cache";
            if (http.Request.Headers.IfNoneMatch == etag)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(bytes, "application/json");
        });
    }
}
