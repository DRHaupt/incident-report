using FieldMap.Web;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;

// Started from another folder (e.g. by hand): serve the app files that sit next to the program.
if (!Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")) && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("/etc/fieldmap-web/appsettings.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("FIELDMAP_");

var options = builder.Configuration.GetSection("FieldMap").Get<WebOptions>() ?? new WebOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<MapHost>();
builder.Services.AddSingleton<EventFiles>();
builder.Services.AddSingleton<AccessGate>();
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = new[] { "application/json", "application/geo+json", "text/html", "text/css", "text/javascript", "application/javascript", "application/manifest+json", "image/svg+xml" };
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Behind Caddy or nginx on the same machine.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals);

var app = builder.Build();
app.UseForwardedHeaders();
app.UseResponseCompression();

var gate = app.Services.GetRequiredService<AccessGate>();
app.Use(async (http, next) =>
{
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "same-origin";
    if (AccessGate.IsProtectedPath(http.Request.Path) && !gate.IsAllowed(http))
    {
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await http.Response.WriteAsJsonAsync(new { error = "code" });
        return;
    }
    await next();
});

var types = new FileExtensionContentTypeProvider();
types.Mappings[".webmanifest"] = "application/manifest+json";
types.Mappings[".pbf"] = "application/x-protobuf";
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = types,
    OnPrepareResponse = ctx =>
    {
        // The service worker keeps its own offline copy; always check for a newer app with the server.
        ctx.Context.Response.Headers.CacheControl = ctx.File.Name.EndsWith(".pbf.gz") ? "public, max-age=2592000" : "no-cache";
    }
});

Api.Map(app);
app.Services.GetRequiredService<MapHost>(); // load the data at start-up, not on the first request
app.Run();

public partial class Program;
