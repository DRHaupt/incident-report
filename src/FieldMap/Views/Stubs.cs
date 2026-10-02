using FieldMap.Framework;
namespace FieldMap.Views;
public sealed class MapWindow : ToolWindow { public const string ToolId = "map"; public const string FollowState = "follow"; public const string IncidentState = "incident"; public MapWindow(string? s) : base(ToolRegistry.Find(ToolId)!) { } }
public sealed class DrawingsWindow : ToolWindow { public const string ToolId = "drawings"; public DrawingsWindow() : base(ToolRegistry.Find(ToolId)!) { } }
public sealed class DataWindow : ToolWindow { public const string ToolId = "data"; public DataWindow() : base(ToolRegistry.Find(ToolId)!) { } }
public sealed class AboutWindow : ToolWindow { public const string ToolId = "about"; public AboutWindow() : base(ToolRegistry.Find(ToolId)!) { } }
