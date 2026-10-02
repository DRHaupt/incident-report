using Shared;

namespace IncidentReporting;

/// <summary>Product name and logo artwork embedded in the executable. Colours come from the shared <see cref="Brand"/> palette.</summary>
internal static class Branding
{
    public const string ProductName = "Incident Report Manager";

    public static Color Navy => Brand.Navy;
    public static Color Amber => Brand.Amber;

    private static readonly BrandAssets Assets = new(typeof(Branding).Assembly, "IncidentReporting.Resources.");

    public static Icon AppIcon => Assets.Icon("app.ico");

    public static Image Logo48 => Assets.Image("logo-48.png");

    public static Image Logo128 => Assets.Image("logo-128.png");

    /// <summary>The logo as a data URI for HTML output (preview pane and exported documents).</summary>
    public static string LogoDataUri(int size) => Assets.DataUri(size >= 128 ? "logo-128.png" : "logo-48.png");
}
