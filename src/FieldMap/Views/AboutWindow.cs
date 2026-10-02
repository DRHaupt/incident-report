using FieldMap.Framework;
using Shared;
using Shared.UI;

namespace FieldMap.Views;

/// <summary>Version, disclaimer, the data sources of every installed layer with licences and dates, and component licences.</summary>
public sealed class AboutWindow : ToolWindow
{
    public const string ToolId = "about";

    private readonly FlowLayoutPanel _stack = Ui.Stack(new Padding(14));

    public AboutWindow() : base(ToolRegistry.Find(ToolId)!)
    {
        Body.Controls.Add(_stack);
        Build();
    }

    protected override void OnMapDataChanged() => Build();

    private void Add(string text, FontRole font = FontRole.Normal, Color? color = null) => _stack.Controls.Add(Ui.Text(text, font, color ?? Brand.Navy, new Padding(0, 3, 0, 3)));

    private void Build()
    {
        _stack.SuspendLayout();
        foreach (Control c in _stack.Controls.Cast<Control>().ToList())
            c.Dispose();
        _stack.Controls.Clear();
        var version = typeof(AboutWindow).Assembly.GetName().Version;
        Add($"{Branding.ProductName} {version?.ToString(3)}", FontRole.Title);
        Add("Offline CAD-style map for emergency response in British Columbia and Alberta.");
        Add(Branding.Disclaimer, FontRole.Bold, Brand.Danger);

        var data = AppServices.Data;
        Add("Map data", FontRole.Heading);
        Add($"Data folder: {AppServices.Store.Root}");
        if (!data.HasData)
            Add("No map data is installed.", FontRole.Bold, Brand.Danger);
        foreach (var r in data.Regions)
        {
            var m = r.Manifest;
            Add($"{m.RegionName} – version {m.Version}, built {m.BuiltUtc.ToLocalTime():yyyy-MM-dd HH:mm} by {m.BuiltBy}" + (m.IsSample ? " – SAMPLE DATA (fictitious)" : ""), FontRole.Bold, m.IsSample ? Brand.Danger : Brand.Navy);
            foreach (var s in m.Sources)
                Add($"• {s.Name}: {s.Licence}. {s.Attribution}. Downloaded {s.DownloadedUtc?.ToLocalTime():yyyy-MM-dd}, data date {s.DataDateUtc?.ToLocalTime():yyyy-MM-dd}. {s.Url}");
            var layers = string.Join("; ", m.Layers.Where(l => l.Value.Count > 0).Select(l => $"{l.Key} {l.Value.Count:N0} ({string.Join(", ", l.Value.Sources)})"));
            Add("Layers: " + layers, FontRole.Normal, Brand.Muted);
        }
        Add("Required attribution: " + data.Attribution, FontRole.Bold);
        Add("OpenStreetMap data is © OpenStreetMap contributors and available under the Open Database Licence (ODbL 1.0). " +
            "Information licensed under the Open Government Licence – Canada, – British Columbia and – Alberta is used where those sources are installed.");

        Add("Live data (only when connected)", FontRole.Heading);
        foreach (var f in AppServices.Live.Catalog.Feeds)
            Add($"• {f.Name}: {f.Licence}. Terms: {f.Terms}. Status: {f.Verified}.");

        Add("Software components", FontRole.Heading);
        foreach (var line in new[]
                 {
                     ".NET 8 and Windows Forms – MIT licence (Microsoft / .NET Foundation)",
                     "SkiaSharp 3 – MIT licence; Skia graphics library – BSD 3-Clause licence",
                     "Microsoft.Data.Sqlite – MIT licence; SQLitePCLRaw – Apache 2.0; SQLite – public domain",
                     "System.Speech (Windows voices) and System.IO.Ports – MIT licence",
                     "All components run fully offline; nothing is downloaded at run time except the optional live data and map updates."
                 })
            Add("• " + line);
        Add("Design notes: docs/FIELDMAP-ARCHITECTURE.md; sources and licences: docs/FIELDMAP-DATA-SOURCES.md.", FontRole.Normal, Brand.Muted);
        UiMode.Apply(_stack);
        _stack.ResumeLayout(true);
    }
}
