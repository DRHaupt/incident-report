using Shared;

namespace FieldMap;

/// <summary>Product name and the logo/icon files embedded in the executable (see assets/fieldmap).</summary>
internal static class Branding
{
    public const string ProductName = "Field Map";

    public const string Disclaimer =
        "Field Map supports, and does not replace, official CAD and dispatch systems. Map data may be incomplete or out of date: " +
        "confirm locations, road status and facility details with dispatch.";

    public static readonly BrandAssets Assets = new(typeof(Branding).Assembly, "FieldMap.Resources.");

    public static Icon AppIcon => Assets.Icon("app.ico");

    public static Image Logo(int size) => Assets.Exists($"fieldmap-{size}.png") ? Assets.Image($"fieldmap-{size}.png") : AppIcon.ToBitmap();

    public static Icon ToolIcon(string name) => Assets.Exists(name + ".ico") ? Assets.Icon(name + ".ico") : AppIcon;

    public static Image ToolImage(string name, int size) => Assets.Exists($"{name}-{size}.png") ? Assets.Image($"{name}-{size}.png") : Logo(size);
}
