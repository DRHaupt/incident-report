using FieldToolkit.Core.Checklists;
using CheckState = FieldToolkit.Core.Checklists.CheckState;
using FieldToolkit.Framework;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Tools.Checklists;

/// <summary>
/// User-defined checklists (vehicle pre-trip, kit check, shift handover…). Each run records OK / Fault / N/A with the
/// time for every item and produces a copyable report. Runs in progress survive closing the window or the app.
/// </summary>
public sealed class ChecklistsWindow : ToolWindow
{
    public const string ToolId = "checklists";

    private readonly Panel _page = new() { Dock = DockStyle.Fill };
    private ChecklistRun? _run;
    private bool _managing;

    public ChecklistsWindow(string? state) : base(ToolRegistry.Find(ToolId)!)
    {
        Body.Controls.Add(_page);
        AppServices.ChecklistTemplates.Changed += OnStoreChanged;
        AppServices.ChecklistRuns.Changed += OnStoreChanged;
        FormClosed += (_, _) =>
        {
            AppServices.ChecklistTemplates.Changed -= OnStoreChanged;
            AppServices.ChecklistRuns.Changed -= OnStoreChanged;
        };
        if (state?.StartsWith("run:", StringComparison.Ordinal) == true && Guid.TryParse(state[4..], out var id))
            _run = AppServices.ChecklistRuns.Items.FirstOrDefault(r => r.Id == id);
        Rebuild();
    }

    public override string SaveState() => _run != null ? "run:" + _run.Id : "";

    private bool _saving;

    private void OnStoreChanged(object? s, EventArgs e)
    {
        // Changes made by this window rebuild the page themselves; other windows' changes refresh the home page.
        if (!_saving && _run == null)
            Rebuild();
    }

    private void SaveRuns()
    {
        _saving = true;
        try
        {
            AppServices.ChecklistRuns.Save();
        }
        finally
        {
            _saving = false;
        }
    }

    private FlowLayoutPanel NewPage()
    {
        _page.SuspendLayout();
        foreach (Control c in _page.Controls.Cast<Control>().ToList())
            c.Dispose();
        _page.Controls.Clear();
        var stack = Ui.Stack(new Padding(10, 8, 10, 10));
        _page.Controls.Add(stack);
        return stack;
    }

    private void Finish(FlowLayoutPanel stack)
    {
        UiMode.Apply(_page);
        _page.ResumeLayout(true);
    }

    private void Rebuild()
    {
        if (_run != null)
            BuildRun();
        else if (_managing)
            BuildManage();
        else
            BuildHome();
    }

    // ------------------------------------------------------------------ home

    private void BuildHome()
    {
        SetTitle(Tool.Title);
        var stack = NewPage();
        stack.Controls.Add(Ui.Text("Start a checklist", FontRole.Heading, Brand.Navy));
        foreach (var t in AppServices.ChecklistTemplates.Items)
        {
            var b = Ui.Button($"{t.Name}   ·   {t.ItemCount} items", ButtonStyle.Primary, () => StartRun(t), FontRole.Large);
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.Height = Ui.Scaled(58);
            b.Margin = new Padding(0, 2, 0, 2);
            stack.Controls.Add(b);
        }
        if (AppServices.ChecklistTemplates.Items.Count == 0)
            stack.Controls.Add(Ui.Text("No checklists yet. Tap \"Manage checklists\" to create one.", fore: Brand.Muted));

        var open = AppServices.ChecklistRuns.Items.Where(r => !r.IsComplete).OrderByDescending(r => r.StartedUtc).ToList();
        if (open.Count > 0)
        {
            stack.Controls.Add(Ui.Text("In progress", FontRole.Heading, Brand.Navy, new Padding(0, 14, 0, 2)));
            foreach (var r in open)
                stack.Controls.Add(RunButton(r, ButtonStyle.Accent));
        }

        var history = AppServices.ChecklistRuns.Items.Where(r => r.IsComplete).OrderByDescending(r => r.CompletedUtc).Take(30).ToList();
        if (history.Count > 0)
        {
            stack.Controls.Add(Ui.Text("Completed (latest 30)", FontRole.Heading, Brand.Navy, new Padding(0, 14, 0, 2)));
            foreach (var r in history)
                stack.Controls.Add(RunButton(r, r.Faults > 0 ? ButtonStyle.Danger : ButtonStyle.Secondary));
        }

        var manage = Ui.Button("Manage checklists…", ButtonStyle.Secondary, () => { _managing = true; Rebuild(); });
        manage.Margin = new Padding(0, 16, 0, 0);
        stack.Controls.Add(manage);
        Finish(stack);
    }

    private Button RunButton(ChecklistRun r, ButtonStyle style)
    {
        var when = (r.CompletedUtc ?? r.StartedUtc).ToLocalTime();
        var text = $"{r.Name}{(r.Reference.Length > 0 ? " – " + r.Reference : "")}   ·   {when:dd MMM HH:mm}   ·   {r.Done}/{r.Total}" +
                   (r.Faults > 0 ? $"   ·   {r.Faults} FAULT{(r.Faults == 1 ? "" : "S")}" : "");
        var b = Ui.Button(text, style, () => { _run = r; Rebuild(); }, FontRole.Bold);
        b.TextAlign = ContentAlignment.MiddleLeft;
        b.Height = Ui.Scaled(52);
        b.Margin = new Padding(0, 2, 0, 2);
        return b;
    }

    private void StartRun(ChecklistTemplate t)
    {
        var reference = "";
        if (t.ReferencePrompt.Length > 0)
        {
            var answer = InputDialog.Ask(this, t.Name, t.ReferencePrompt + ":");
            if (answer == null)
                return;
            reference = answer;
        }
        var run = ChecklistRun.Start(t, AppServices.Settings.UserName, reference, DateTime.UtcNow);
        AppServices.ChecklistRuns.Items.Add(run);
        SaveRuns();
        _run = run;
        Rebuild();
    }

    // ------------------------------------------------------------------ a run

    private void BuildRun()
    {
        var run = _run!;
        SetTitle(run.Name + (run.Reference.Length > 0 ? " – " + run.Reference : ""));
        var stack = NewPage();

        var back = Ui.Button("‹ All checklists", ButtonStyle.Secondary, () => { _run = null; Rebuild(); });
        stack.Controls.Add(back);
        var summary = new VStack { BackColor = run.Faults > 0 ? Brand.Danger : run.IsComplete ? Brand.Success : Brand.Navy, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 6, 0, 6) };
        var status = run.IsComplete ? $"Completed {run.CompletedUtc!.Value.ToLocalTime():HH:mm dd MMM}" : $"Started {run.StartedUtc.ToLocalTime():HH:mm dd MMM}";
        summary.Controls.Add(Ui.Text($"{run.Done} of {run.Total} checked" + (run.Faults > 0 ? $" · {run.Faults} fault{(run.Faults == 1 ? "" : "s")}" : "") + $" · {status}" +
                                     (run.CheckedBy.Length > 0 ? $" · {run.CheckedBy}" : ""), FontRole.Heading, Color.White));
        stack.Controls.Add(summary);

        foreach (var line in run.Lines)
        {
            if (line.Heading)
            {
                stack.Controls.Add(Ui.Text(line.Text, FontRole.Heading, Brand.Navy, new Padding(0, 12, 0, 2)));
                continue;
            }
            stack.Controls.Add(ItemRow(run, line));
        }

        var notes = Ui.Button(run.Notes.Length > 0 ? "Notes: " + run.Notes : "Add notes…", ButtonStyle.Secondary, () =>
        {
            var text = InputDialog.Ask(this, "Notes", "Notes for this checklist:", run.Notes);
            if (text == null)
                return;
            run.Notes = text;
            SaveRuns();
            Rebuild();
        }, FontRole.Normal);
        notes.TextAlign = ContentAlignment.MiddleLeft;
        notes.Margin = new Padding(0, 14, 0, 4);
        stack.Controls.Add(notes);

        var actions = new List<Button> { Ui.Button("Copy report", ButtonStyle.Secondary, () => CopyReport(run), FontRole.Heading) };
        if (!run.IsComplete)
            actions.Add(Ui.Button("Finish", ButtonStyle.Primary, () => Complete(run), FontRole.Heading));
        else
            actions.Add(Ui.Button("Reopen", ButtonStyle.Secondary, () => { run.CompletedUtc = null; SaveRuns(); Rebuild(); }, FontRole.Heading));
        actions.Add(Ui.Button("Delete", ButtonStyle.Danger, () => DeleteRun(run), FontRole.Heading));
        var row = Ui.ButtonRow(actions.ToArray());
        row.Height = Ui.Scaled(64);
        row.FixedSize();
        stack.Controls.Add(row);
        Finish(stack);
    }

    private Control ItemRow(ChecklistRun run, RunLine line)
    {
        var box = new VStack
        {
            BackColor = line.State switch { CheckState.Fault => Color.FromArgb(0xFD, 0xEC, 0xEC), CheckState.Ok => Color.FromArgb(0xE3, 0xF4, 0xE8), _ => Brand.Panel },
            Padding = new Padding(8, 4, 8, 6),
            Margin = new Padding(0, 2, 0, 2)
        };
        var caption = line.Text + (line.CheckedUtc is { } t ? $"   ({t.ToLocalTime():HH:mm})" : "") + (line.Note.Length > 0 ? $"\n⚠ {line.Note}" : "");
        box.Controls.Add(Ui.Text(caption, FontRole.Large, line.State == CheckState.Fault ? Brand.Danger : Brand.Navy));
        Button State(string text, CheckState s, ButtonStyle on)
        {
            var b = Ui.Button(text, line.State == s ? on : ButtonStyle.Secondary, () => SetState(run, line, s));
            b.Enabled = !run.IsComplete;
            return b;
        }
        var buttons = Ui.ButtonRow(State("OK", CheckState.Ok, ButtonStyle.Success), State("Fault", CheckState.Fault, ButtonStyle.Danger), State("N/A", CheckState.NotApplicable, ButtonStyle.Primary));
        buttons.Height = Ui.Scaled(52);
        buttons.FixedSize();
        box.Controls.Add(buttons);
        return box;
    }

    private void SetState(ChecklistRun run, RunLine line, CheckState state)
    {
        string? note = null;
        if (state == CheckState.Fault && line.State != CheckState.Fault)
            note = InputDialog.Ask(this, "Fault", $"What is wrong? ({line.Text})", line.Note) ?? "";
        run.Set(line, state, DateTime.UtcNow, note);
        SaveRuns();
        Rebuild();
    }

    private void Complete(ChecklistRun run)
    {
        var open = run.Total - run.Done;
        if (open > 0 && MessageBox.Show(this, $"{open} item{(open == 1 ? " is" : "s are")} not checked. Finish anyway?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        run.CompletedUtc = DateTime.UtcNow;
        SaveRuns();
        Rebuild();
    }

    private void CopyReport(ChecklistRun run)
    {
        try
        {
            Clipboard.SetText(run.Report());
            MessageBox.Show(this, "Report copied. Paste it into a message, the radio log or a handover note.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "The clipboard is busy. Try again.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DeleteRun(ChecklistRun run)
    {
        if (MessageBox.Show(this, $"Delete this record of \"{run.Name}\"?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        AppServices.ChecklistRuns.Items.Remove(run);
        _run = null;
        SaveRuns();
        Rebuild();
    }

    // ------------------------------------------------------------------ manage templates

    private void BuildManage()
    {
        SetTitle("Manage checklists");
        var stack = NewPage();
        stack.Controls.Add(Ui.Button("‹ Back", ButtonStyle.Secondary, () => { _managing = false; Rebuild(); }));
        stack.Controls.Add(Ui.Text("Your checklists", FontRole.Heading, Brand.Navy, new Padding(0, 10, 0, 2)));
        var templates = AppServices.ChecklistTemplates;
        for (var i = 0; i < templates.Items.Count; i++)
        {
            var t = templates.Items[i];
            var index = i;
            var row = new TableLayoutPanel { ColumnCount = 4, RowCount = 1, Height = Ui.Scaled(54), Margin = new Padding(0, 2, 0, 2) };
            row.FixedSize();
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var c = 0; c < 3; c++)
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            var name = new Label { Text = $"{t.Name} ({t.ItemCount} items)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false }.Styled(FontRole.Bold);
            row.Controls.Add(name, 0, 0);
            var up = Ui.Button("Move up", ButtonStyle.Secondary, () => MoveTemplate(index, -1), FontRole.Normal);
            up.Enabled = i > 0;
            var edit = Ui.Button("Edit", ButtonStyle.Primary, () => Edit(t));
            var delete = Ui.Button("Delete", ButtonStyle.Danger, () => DeleteTemplate(t));
            foreach (var b in new[] { up, edit, delete })
                b.Dock = DockStyle.Fill;
            row.Controls.Add(up, 1, 0);
            row.Controls.Add(edit, 2, 0);
            row.Controls.Add(delete, 3, 0);
            stack.Controls.Add(row);
        }
        var add = Ui.Button("New checklist…", ButtonStyle.Accent, () => Edit(null));
        add.Margin = new Padding(0, 10, 0, 0);
        stack.Controls.Add(add);
        stack.Controls.Add(Ui.Text("Checklists are saved on this computer. Records of completed checks are kept until you delete them.", fore: Brand.Muted, padding: new Padding(0, 10, 0, 0)));
        Finish(stack);
    }

    private void Edit(ChecklistTemplate? t)
    {
        var editing = t ?? new ChecklistTemplate { Name = "New checklist" };
        using var d = new TemplateDialog(editing);
        if (d.ShowDialog(this) != DialogResult.OK)
            return;
        if (t == null)
            AppServices.ChecklistTemplates.Items.Add(editing);
        AppServices.ChecklistTemplates.Save();
        Rebuild();
    }

    private void MoveTemplate(int index, int delta)
    {
        var items = AppServices.ChecklistTemplates.Items;
        var target = index + delta;
        if (target < 0 || target >= items.Count)
            return;
        (items[index], items[target]) = (items[target], items[index]);
        AppServices.ChecklistTemplates.Save();
        Rebuild();
    }

    private void DeleteTemplate(ChecklistTemplate t)
    {
        if (MessageBox.Show(this, $"Delete the checklist \"{t.Name}\"? Records of checks already done are kept.", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        AppServices.ChecklistTemplates.Items.Remove(t);
        AppServices.ChecklistTemplates.Save();
        Rebuild();
    }
}
