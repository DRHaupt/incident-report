using FieldMap.Views;

namespace FieldMap.Framework;

/// <summary>The windows of Field Map, in launcher tile order.</summary>
public static class ToolRegistry
{
    public static readonly IReadOnlyList<ToolDescriptor> All = new[]
    {
        new ToolDescriptor
        {
            Id = MapWindow.ToolId,
            Title = "Map",
            Description = "Offline map, search, directions, incident tools",
            IconName = "map",
            Create = state => new MapWindow(state)
        },
        new ToolDescriptor
        {
            Id = DrawingsWindow.ToolId,
            Title = "Incidents & drawings",
            Description = "Pins, cordons, closures; export KML/GeoJSON/GPX",
            IconName = "drawings",
            Create = _ => new DrawingsWindow(),
            SingleInstance = true,
            DefaultSize = new Size(620, 720)
        },
        new ToolDescriptor
        {
            Id = DataWindow.ToolId,
            Title = "Data & updates",
            Description = "Regions, update map data, USB packages, AEDs",
            IconName = "data",
            Create = _ => new DataWindow(),
            SingleInstance = true,
            DefaultSize = new Size(820, 720)
        },
        new ToolDescriptor
        {
            Id = AboutWindow.ToolId,
            Title = "About & data sources",
            Description = "Sources, licences, dates, disclaimer",
            IconName = "about",
            Create = _ => new AboutWindow(),
            SingleInstance = true,
            DefaultSize = new Size(760, 720)
        }
    };

    public static ToolDescriptor? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    public sealed record Tile(string Title, string Description, string IconName, string ToolId, string? State);

    /// <summary>Launcher tiles: two ready-made map windows (one following the GPS, one for the incident) and the other tools.</summary>
    public static readonly IReadOnlyList<Tile> Tiles = new[]
    {
        new Tile("Map – follow GPS", "Live position, heading-up, breadcrumbs", "map", MapWindow.ToolId, MapWindow.FollowState),
        new Tile("Map – incident", "A second map for the incident area", "incident", MapWindow.ToolId, MapWindow.IncidentState),
        new Tile("Incidents & drawings", "Pins, cordons, closures; KML/GeoJSON/GPX", "drawings", DrawingsWindow.ToolId, null),
        new Tile("Data & updates", "Regions, update map data, USB, AEDs", "data", DataWindow.ToolId, null),
        new Tile("About & data sources", "Sources, licences, dates, disclaimer", "about", AboutWindow.ToolId, null)
    };
}
