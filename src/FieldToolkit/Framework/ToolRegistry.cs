using FieldToolkit.Tools.Checklists;
using FieldToolkit.Tools.Cpr;
using FieldToolkit.Tools.FirstAid;
using FieldToolkit.Tools.Hazmat;
using FieldToolkit.Tools.Kit;
using FieldToolkit.Tools.Location;
using FieldToolkit.Tools.Timers;

namespace FieldToolkit.Framework;

/// <summary>
/// The tools shown on the launcher, in tile order. To add a tool: write a <see cref="ToolWindow"/> subclass, add icon
/// artwork under assets/fieldtoolkit, and register it here. Arrange, pin and session restore work automatically.
/// </summary>
public static class ToolRegistry
{
    public static readonly IReadOnlyList<ToolDescriptor> All = new[]
    {
        new ToolDescriptor
        {
            Id = FirstAidWindow.ToolId,
            Title = "First Aid Guide",
            Description = "Step-by-step emergency instructions",
            IconName = "firstaid",
            Create = state => new FirstAidWindow(state)
        },
        new ToolDescriptor
        {
            Id = CprWindow.ToolId,
            Title = "CPR Helper",
            Description = "Metronome, compression counter, 30:2 prompts",
            IconName = "cpr",
            Create = state => new CprWindow(state),
            SingleInstance = true,
            DefaultSize = new Size(560, 680)
        },
        new ToolDescriptor
        {
            Id = HazmatWindow.ToolId,
            Title = "Hazmat Lookup",
            Description = "UN/NA number, placard and ERG guide",
            IconName = "hazmat",
            Create = state => new HazmatWindow(state)
        },
        new ToolDescriptor
        {
            Id = LocationWindow.ToolId,
            Title = "Location",
            Description = "GPS position, grid reference, saved points",
            IconName = "location",
            Create = state => new LocationWindow(state),
            DefaultSize = new Size(620, 700)
        },
        new ToolDescriptor
        {
            Id = TimersWindow.ToolId,
            Title = "Timers",
            Description = "Stopwatches and countdowns",
            IconName = "timers",
            Create = _ => new TimersWindow(),
            SingleInstance = true,
            DefaultSize = new Size(560, 640)
        },
        new ToolDescriptor
        {
            Id = ChecklistsWindow.ToolId,
            Title = "Checklists",
            Description = "Pre-trip, kit and handover checks",
            IconName = "checklists",
            Create = state => new ChecklistsWindow(state),
            DefaultSize = new Size(640, 720)
        },
        new ToolDescriptor
        {
            Id = KitWindow.ToolId,
            Title = "Kit Tracker",
            Description = "Expiry dates for kit and extinguishers",
            IconName = "kit",
            Create = state => new KitWindow(state),
            SingleInstance = true,
            DefaultSize = new Size(680, 700)
        }
    };

    public static ToolDescriptor? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}
