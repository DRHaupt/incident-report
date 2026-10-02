using FieldToolkit.Core.Hazmat;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Tools.Hazmat;

/// <summary>Where the hazmat data came from, problems found on load, and how to import the official ERG.</summary>
public sealed class ErgInfoDialog : Form
{
    public ErgInfoDialog(ErgDataset d)
    {
        Text = "Hazmat data";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(720, 560);
        Padding = new Padding(12);

        var stack = Ui.Stack(new Padding(0));
        stack.Controls.Add(Ui.Text(d.IsSample ? "SAMPLE DATA – NOT FOR OPERATIONAL USE" : d.Edition, FontRole.Title, d.IsSample ? Brand.Danger : Brand.Navy));
        if (d.Notes.Length > 0)
            stack.Controls.Add(Ui.Text(d.Notes, FontRole.Bold));
        stack.Controls.Add(Ui.Text($"Source: {d.Source}\nSHA-256: {(d.SourceSha256.Length > 0 ? d.SourceSha256 : "—")}\nImported: {(d.ImportedOn.Length > 0 ? d.ImportedOn : "—")} {d.ImportTool}\nFile: {d.FilePath}"));
        stack.Controls.Add(Ui.Text($"{d.Materials.Count} materials · {d.Guides.Count} guides · {d.IsolationTable.Count} Table 1 entries · {d.Placards.Count(p => p.Guide.Length > 0)}/{d.Placards.Count} placards with a guide"));
        stack.Controls.Add(Ui.Text(d.Problems.Count == 0 ? "No data problems found." : $"{d.Problems.Count} data problem(s):", FontRole.Heading,
            d.Problems.Count == 0 ? Brand.Success : Brand.Danger, new Padding(0, 10, 0, 2)));
        foreach (var p in d.Problems.Take(50))
            stack.Controls.Add(Ui.Text("•  " + p, fore: Brand.Danger));

        stack.Controls.Add(Ui.Text("Importing the official ERG", FontRole.Heading, Brand.Navy, new Padding(0, 12, 0, 2)));
        stack.Controls.Add(Ui.Text(
            "1. Download the English ERG PDF from PHMSA (U.S. DOT): phmsa.dot.gov → Hazmat Safety → Emergency Response Guidebook (ERG).\n" +
            "2. Fill in tools\\ErgImport\\placards.csv from the guidebook's placard table (optional).\n" +
            "3. Run:  ErgImport pdf ERG2024-Eng-Web.pdf --out erg.json\n" +
            "4. Run:  ErgImport verify erg.json  and tick the sampled entries against the printed guidebook.\n" +
            "5. Copy erg.json to Content\\erg\\ (next to FieldToolkit.exe, or in your content folder) and reopen the Hazmat window.\n" +
            "See tools/ErgImport/README.md for details."));

        var close = Ui.Button("Close", ButtonStyle.Primary, Close);
        close.Dock = DockStyle.Bottom;
        Controls.Add(stack);
        Controls.Add(close);
        CancelButton = close;
        UiMode.TrackDialog(this);
    }
}
