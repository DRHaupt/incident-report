using Shared;

namespace FieldToolkit;

/// <summary>Product name and the logo/icon files embedded in the executable (see assets/fieldtoolkit).</summary>
internal static class Branding
{
    public const string ProductName = "Field Toolkit";

    public static readonly BrandAssets Assets = new(typeof(Branding).Assembly, "FieldToolkit.Resources.");

    public static Icon AppIcon => Assets.Icon("app.ico");

    public static Image Logo48 => Assets.Image("toolkit-48.png");

    /// <summary>A tool's window icon; falls back to the app icon.</summary>
    public static Icon ToolIcon(string iconName) => Assets.Exists(iconName + ".ico") ? Assets.Icon(iconName + ".ico") : AppIcon;

    public static Image ToolImage(string iconName, int size) =>
        Assets.Exists($"{iconName}-{size}.png") ? Assets.Image($"{iconName}-{size}.png") : Assets.Image($"toolkit-{size}.png");
}
