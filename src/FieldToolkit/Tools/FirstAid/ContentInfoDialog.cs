using FieldToolkit.Core.FirstAid;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Tools.FirstAid;

/// <summary>Disclaimer, review status of every topic, sources folder and any content problems found on load.</summary>
public sealed class ContentInfoDialog : Form
{
    public ContentInfoDialog(FirstAidLibrary lib)
    {
        Text = "About this content";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(720, 600);
        Padding = new Padding(12);

        var stack = Ui.Stack(new Padding(0));
        var g = lib.Guide;
        stack.Controls.Add(Ui.Text(g.Title, FontRole.Title, Brand.Navy));
        stack.Controls.Add(Ui.Text(g.Disclaimer, FontRole.Bold, padding: new Padding(0, 6, 0, 6)));
        stack.Controls.Add(Ui.Text($"Content version: {(g.ContentVersion.Length > 0 ? g.ContentVersion : "—")}"));
        stack.Controls.Add(Ui.Text($"Review status: {g.Review.Status}", FontRole.Bold, g.Review.IsReviewed ? Brand.Success : Brand.Danger));
        stack.Controls.Add(Ui.Text($"Content last reviewed by: {(g.Review.ReviewedBy.Length > 0 ? g.Review.ReviewedBy : "— not yet reviewed —")}"));
        stack.Controls.Add(Ui.Text($"Content last reviewed on: {(g.Review.ReviewedOn.Length > 0 ? g.Review.ReviewedOn : "—")}"));
        if (g.Review.Notes.Length > 0)
            stack.Controls.Add(Ui.Text(g.Review.Notes, fore: Brand.Muted));
        stack.Controls.Add(Ui.Text($"Emergency number shown: {g.EmergencyNumber}"));
        stack.Controls.Add(Ui.Text($"Content folder: {lib.Folder}", fore: Brand.Muted));

        stack.Controls.Add(Ui.Text(lib.Problems.Count == 0 ? "No content problems found." : $"{lib.Problems.Count} content problem(s):",
            FontRole.Heading, lib.Problems.Count == 0 ? Brand.Success : Brand.Danger, new Padding(0, 12, 0, 2)));
        foreach (var p in lib.Problems)
            stack.Controls.Add(Ui.Text("•  " + p, fore: Brand.Danger));

        stack.Controls.Add(Ui.Text("Topics", FontRole.Heading, Brand.Navy, new Padding(0, 12, 0, 2)));
        foreach (var t in lib.Topics)
        {
            var r = t.Review;
            var reviewed = r.IsReviewed ? $"reviewed by {r.ReviewedBy} on {r.ReviewedOn}" : r.Status;
            var sources = string.Join("; ", t.Sources.Select(s => s.Body + (s.Year is { } y ? " " + y : "")).Distinct());
            stack.Controls.Add(Ui.Text($"{t.Title} – {reviewed}\n    Sources: {sources}", fore: r.IsReviewed ? null : Brand.Danger));
        }

        var close = Ui.Button("Close", ButtonStyle.Primary, Close);
        close.Dock = DockStyle.Bottom;
        Controls.Add(stack);
        Controls.Add(close);
        CancelButton = close;
        UiMode.TrackDialog(this);
    }
}
