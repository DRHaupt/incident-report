using FieldToolkit.Framework;

namespace FieldToolkit.Tools.Hazmat;

public sealed class HazmatWindow : ToolWindow
{
    public const string ToolId = "hazmat";

    public HazmatWindow(string? state) : base(ToolRegistry.Find(ToolId)!)
    {
    }
}
