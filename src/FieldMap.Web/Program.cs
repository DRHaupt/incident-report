using FieldMap.Web;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;

// Started from another folder (e.g. by hand): serve the app files that sit next to the program.
if (!Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")) && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);
// One line per live-feed fetch is enough (LiveHub logs failures); the HTTP client would add four.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Configuration.AddJsonFile("/etc/fieldmap-web/appsettings.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("FIELDMAP_");

var options = builder.Configuration.GetSection("FieldMap").Get<WebOptions>() ?? new WebOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<MapHost>();
builder.Services.AddSingleton<EventFiles>();
builder.Services.AddSingleton<AccessGate>();
builder.Services.AddSingleton<ImageryProxy>();
builder.Services.AddSingleton<Team>();
builder.Services.AddSingleton<AprsHub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AprsHub>());
builder.Services.AddHttpClient();
builder.Services.AddSingleton<LiveHub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveHub>());
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = new[] { "application/json", "application/geo+json", "text/html", "text/css", "text/javascript", "application/javascript", "application/manifest+json", "image/svg+xml" };
});
// The installed iOS/Android apps call this server from pages inside the app. They sign in with a token they send
// themselves (never the cookie), so allowing them adds nothing a browser on another site could use.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(options.AppOrigins.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().TrimEnd('/')).ToArray())
    .WithMethods("GET", "POST", "PUT", "DELETE")
    .WithHeaders("Authorization", "Content-Type")
    .SetPreflightMaxAge(TimeSpan.FromHours(24))));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Behind Caddy or nginx on the same machine.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals);

var app = builder.Build();
// Behind a Cloudflare Tunnel, cloudflared connects from this machine and Cloudflare's edge says who the phone is
// (CF-Connecting-IP) and whether it used HTTPS (CF-Visitor). Only trusted from a local connection, where only the
// tunnel or a local proxy can be; the edge always overwrites these headers, so phones can't fake them.
app.Use((http, next) =>
{
    var h = http.Request.Headers;
    if (http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip))
    {
        if (h.TryGetValue("CF-Connecting-IP", out var client) && System.Net.IPAddress.TryParse(client.ToString(), out _))
            h["X-Forwarded-For"] = client.ToString();
        if (h.TryGetValue("CF-Visitor", out var visitor) && visitor.ToString().Contains("\"https\"", StringComparison.Ordinal))
            h["X-Forwarded-Proto"] = "https";
    }
    return next(http);
});
app.UseForwardedHeaders();
app.UseResponseCompression();
app.UseCors(); // before the access gate: the app's browser asks permission (OPTIONS) without the sign-in

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
TeamApi.Map(app);
AprsApi.Map(app);
LiveApi.Map(app);
HazmatApi.Map(app);
FirstAidApi.Map(app);
app.Services.GetRequiredService<MapHost>(); // load the data at start-up, not on the first request
app.Run();

public partial class Program;
