using FieldToolkit.Core.FirstAid;
using FieldToolkit.Core.Timers;
using FieldToolkit.Framework;
using FieldToolkit.Tools.Cpr;
using FieldToolkit.Tools.Timers;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Tools.FirstAid;

/// <summary>
/// First Aid & Emergency Guide. Home page: symptom search and topic list (scene safety always first). Topic page:
/// prominent "call emergency services" banner, one step per screen with large Back/Next, an all-steps view,
/// warning signs and do-NOT lists, related topics and sources. Several windows can be open side by side.
/// </summary>
public sealed class FirstAidWindow : ToolWindow
{
    public const string ToolId = "firstaid";

    private enum TopicView { Steps, All, Warnings, More }

    private static FirstAidLibrary Lib => AppServices.FirstAid;

    private readonly Panel _home = new() { Dock = DockStyle.Fill };
    private readonly Panel _topicPage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private readonly FlowLayoutPanel _results;
    private readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 200 };

    private readonly Label _topicTitle = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
    private readonly VStack _callBanner = new() { Dock = DockStyle.Top, BackColor = Brand.Danger, Padding = new Padding(12, 8, 12, 8), Cursor = Cursors.Hand };
    private readonly Button[] _tabs = new Button[4];
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private bool _callExpanded;

    private FirstAidTopic? _topic;
    private int _step;
    private TopicView _view;

    public FirstAidWindow(string? state) : base(ToolRegistry.Find(ToolId)!)
    {
        _results = Ui.Stack(new Padding(8, 4, 8, 8));
        BuildHome();
        BuildTopicPage();
        Body.Controls.Add(_topicPage);
        Body.Controls.Add(_home);
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ShowResults(); };
        FormClosed += (_, _) => _searchDelay.Dispose();
        ShowResults();
        RestoreState(state);
    }

    // ------------------------------------------------------------------ state

    public override string SaveState() => _topic == null ? "" : $"{_topic.Id}|{_step}|{_view}";

    private void RestoreState(string? state)
    {
        if (string.IsNullOrEmpty(state))
            return;
        var parts = state.Split('|');
        var topic = Lib.Find(parts[0]);
        if (topic == null)
            return;
        var step = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 0;
        var view = parts.Length > 2 && Enum.TryParse<TopicView>(parts[2], out var v) ? v : TopicView.Steps;
        OpenTopic(topic, step, view);
    }

    // ------------------------------------------------------------------ home page

    private void BuildHome()
    {
        var searchRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 64, ColumnCount = 2, RowCount = 1, Padding = new Padding(8, 8, 8, 4) };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        _search.Styled(FontRole.Large);
        _search.PlaceholderText = "Search symptoms, e.g. not breathing, bleeding a lot, fitting";
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { _search.Clear(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Enter && _results.Controls.OfType<Button>().FirstOrDefault()?.Tag is FirstAidTopic first)
            {
                OpenTopic(first);
                e.SuppressKeyPress = true;
            }
        };
        var clear = Ui.Button("Clear", ButtonStyle.Secondary, () => { _search.Clear(); _search.Focus(); });
        clear.Dock = DockStyle.Fill;
        clear.Margin = new Padding(6, 0, 0, 0);
        searchRow.Controls.Add(_search, 0, 0);
        searchRow.Controls.Add(clear, 1, 0);

        _home.Controls.Add(_results);
        _home.Controls.Add(BuildDisclaimerStrip());
        _home.Controls.Add(searchRow);
    }

    private Control BuildDisclaimerStrip()
    {
        var reviewed = Lib.Guide.Review.IsReviewed;
        var strip = new VStack { Dock = DockStyle.Bottom, BackColor = reviewed ? Brand.Panel : Brand.Amber, Padding = new Padding(10, 6, 10, 6) };
        var text = "Supports first aid training – does not replace it. Always call for professional help. ";
        text += reviewed
            ? $"Content reviewed by {Lib.Guide.Review.ReviewedBy} on {Lib.Guide.Review.ReviewedOn}."
            : "DRAFT CONTENT – not yet clinically reviewed.";
        text += Lib.Problems.Count > 0 ? $" {Lib.Problems.Count} content problem(s) found." : "";
        strip.Controls.Add(Ui.Text(text + "  Tap for details.", FontRole.Bold, Brand.Navy));
        Ui.OnTap(strip, () => { using var d = new ContentInfoDialog(Lib); d.ShowDialog(this); });
        return strip;
    }

    private void ShowResults()
    {
        _results.SuspendLayout();
        foreach (Control c in _results.Controls.Cast<Control>().ToList())
            c.Dispose();
        _results.Controls.Clear();

        var query = _search.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            string? category = null;
            foreach (var t in Lib.Topics)
            {
                if (t.Category != category)
                {
                    category = t.Category;
                    _results.Controls.Add(Ui.Text(category, FontRole.Heading, Brand.Navy, new Padding(2, 10, 0, 2)));
                }
                _results.Controls.Add(TopicButton(t));
            }
            if (Lib.Topics.Count == 0)
                _results.Controls.Add(Ui.Text("No first aid content found in " + Lib.Folder, FontRole.Large, Brand.Danger));
        }
        else
        {
            var hits = AppServices.FirstAidSearch.Search(query);
            _results.Controls.Add(Ui.Text(hits.Count == 0
                    ? "No matching topics. Try other words, or clear the search to see every topic."
                    : $"{hits.Count} matching topic{(hits.Count == 1 ? "" : "s")}", FontRole.Bold, Brand.Muted, new Padding(2, 6, 0, 2)));
            foreach (var h in hits)
                _results.Controls.Add(TopicButton(h.Topic));
            // Scene safety stays one tap away whatever was searched.
            if (hits.All(h => !h.Topic.PinnedFirst) && Lib.Topics.FirstOrDefault(t => t.PinnedFirst) is { } pinned)
            {
                _results.Controls.Add(Ui.Text("Always:", FontRole.Bold, Brand.Muted, new Padding(2, 10, 0, 2)));
                _results.Controls.Add(TopicButton(pinned));
            }
        }
        UiMode.Apply(_results);
        _results.ResumeLayout(true);
    }

    private Button TopicButton(FirstAidTopic t)
    {
        var b = Ui.Button(t.Title, t.PinnedFirst ? ButtonStyle.Primary : ButtonStyle.Secondary, () => OpenTopic(t), FontRole.Large);
        b.TextAlign = ContentAlignment.MiddleLeft;
        b.Padding = new Padding(10, 0, 0, 0);
        b.Height = Ui.Scaled(58);
        b.Margin = new Padding(0, 2, 0, 2);
        b.Tag = t;
        return b;
    }

    // ------------------------------------------------------------------ topic page

    private void BuildTopicPage()
    {
        var titleRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 60, ColumnCount = 2, RowCount = 1, Padding = new Padding(8, 6, 8, 2) };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var back = Ui.Button("‹ All topics", ButtonStyle.Secondary, ShowHome);
        back.Dock = DockStyle.Fill;
        back.Margin = new Padding(0, 0, 8, 0);
        titleRow.Controls.Add(back, 0, 0);
        _topicTitle.ForeColor = Brand.Navy;
        _topicTitle.Styled(FontRole.Title);
        titleRow.Controls.Add(_topicTitle, 1, 0);

        Ui.OnTap(_callBanner, () => { _callExpanded = !_callExpanded; FillCallBanner(); });

        var tabs = new TableLayoutPanel { Dock = DockStyle.Top, Height = 58, ColumnCount = 4, RowCount = 1, Padding = new Padding(8, 6, 8, 2) };
        tabs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var names = new[] { "Step by step", "All steps", "Warnings · Do NOT", "Related · Sources" };
        for (var i = 0; i < 4; i++)
        {
            var view = (TopicView)i;
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            _tabs[i] = Ui.Button(names[i], ButtonStyle.Secondary, () => ShowView(view));
            _tabs[i].Dock = DockStyle.Fill;
            _tabs[i].Margin = new Padding(2, 0, 2, 0);
            tabs.Controls.Add(_tabs[i], i, 0);
        }

        _topicPage.Controls.Add(_content);
        _topicPage.Controls.Add(tabs);
        _topicPage.Controls.Add(_callBanner);
        _topicPage.Controls.Add(titleRow);
    }

    private void ShowHome()
    {
        _topic = null;
        _topicPage.Visible = false;
        _home.Visible = true;
        SetTitle(Tool.Title);
        _search.Focus();
    }

    private void OpenTopic(FirstAidTopic topic, int step = 0, TopicView view = TopicView.Steps)
    {
        _topic = topic;
        _step = Math.Clamp(step, 0, Math.Max(0, topic.Steps.Count - 1));
        _callExpanded = false;
        _topicTitle.Text = topic.Title;
        SetTitle(topic.Title);
        FillCallBanner();
        _home.Visible = false;
        _topicPage.Visible = true;
        ShowView(view);
    }

    private void FillCallBanner()
    {
        if (_topic == null)
            return;
        _callBanner.SuspendLayout();
        foreach (Control c in _callBanner.Controls.Cast<Control>().ToList())
            c.Dispose();
        _callBanner.Controls.Clear();
        _callBanner.Controls.Add(Ui.Text(Lib.Expand(_topic.CallEmergency.Headline), FontRole.Heading, Color.White));
        if (_callExpanded)
        {
            foreach (var w in _topic.CallEmergency.When)
                _callBanner.Controls.Add(Ui.Text("•  " + Lib.Expand(w), FontRole.Bold, Color.White));
            _callBanner.Controls.Add(Ui.Text("Tap to hide", fore: Color.White));
        }
        else
        {
            _callBanner.Controls.Add(Ui.Text($"Tap to see when to call ({_topic.CallEmergency.When.Count})", fore: Color.White));
        }
        foreach (Control c in _callBanner.Controls)
            Ui.OnTap(c, () => { _callExpanded = !_callExpanded; FillCallBanner(); });
        UiMode.Apply(_callBanner);
        _callBanner.ResumeLayout(true);
    }

    private void ShowView(TopicView view)
    {
        if (_topic == null)
            return;
        _view = view;
        for (var i = 0; i < _tabs.Length; i++)
            Ui.SetSelected(_tabs[i], i == (int)view);

        _content.SuspendLayout();
        foreach (Control c in _content.Controls.Cast<Control>().ToList())
            c.Dispose();
        _content.Controls.Clear();
        switch (view)
        {
            case TopicView.Steps:
                BuildStepView();
                break;
            case TopicView.All:
                BuildAllSteps();
                break;
            case TopicView.Warnings:
                BuildWarnings();
                break;
            default:
                BuildMore();
                break;
        }
        UiMode.Apply(_content);
        _content.ResumeLayout(true);
        AppServices.Windows.NotifyChanged();
    }

    private void BuildStepView()
    {
        var topic = _topic!;
        var stack = Ui.Stack(new Padding(12, 8, 12, 8));
        if (topic.Steps.Count > 0)
        {
            var step = topic.Steps[_step];
            stack.Controls.Add(Ui.Text($"Step {_step + 1} of {topic.Steps.Count}", FontRole.Bold, Brand.Muted));
            AddStep(stack, step, numbered: null, large: true);
        }

        var back = Ui.Button("‹ Back", ButtonStyle.Secondary, () => { _step--; ShowView(TopicView.Steps); }, FontRole.Heading);
        back.Enabled = _step > 0;
        var last = _step >= topic.Steps.Count - 1;
        var next = last
            ? Ui.Button("Warnings & Do NOT ›", ButtonStyle.Accent, () => ShowView(TopicView.Warnings), FontRole.Heading)
            : Ui.Button("Next ›", ButtonStyle.Primary, () => { _step++; ShowView(TopicView.Steps); }, FontRole.Heading);
        var nav = Ui.ButtonRow(back, next);
        var navPanel = new Panel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(8, 6, 8, 8) };
        nav.Dock = DockStyle.Fill;
        navPanel.Controls.Add(nav);

        _content.Controls.Add(stack);
        _content.Controls.Add(navPanel);
    }

    private void BuildAllSteps()
    {
        var stack = Ui.Stack(new Padding(12, 8, 12, 8));
        for (var i = 0; i < _topic!.Steps.Count; i++)
            AddStep(stack, _topic.Steps[i], numbered: i + 1, large: false);
        AddWarningLists(stack);
        _content.Controls.Add(stack);
    }

    private void BuildWarnings()
    {
        var stack = Ui.Stack(new Padding(12, 8, 12, 8));
        stack.Controls.Add(Ui.Text("Call " + Lib.Guide.EmergencyNumber + " if:", FontRole.Heading, Brand.Danger));
        foreach (var w in _topic!.CallEmergency.When)
            stack.Controls.Add(Ui.Text("•  " + Lib.Expand(w), FontRole.Large));
        AddWarningLists(stack);
        _content.Controls.Add(stack);
    }

    private void AddWarningLists(FlowLayoutPanel stack)
    {
        var topic = _topic!;
        if (topic.WarningSigns.Count > 0)
        {
            stack.Controls.Add(Ui.Text("Warning signs – get help fast", FontRole.Heading, Brand.Navy, new Padding(0, 14, 0, 2)));
            foreach (var w in topic.WarningSigns)
                stack.Controls.Add(Ui.Text("•  " + Lib.Expand(w), FontRole.Large));
        }
        if (topic.DoNot.Count > 0)
        {
            var box = new VStack { BackColor = Color.FromArgb(0xFD, 0xEC, 0xEC), Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 14, 0, 4) };
            box.Controls.Add(Ui.Text("Do NOT", FontRole.Heading, Brand.Danger));
            foreach (var d in topic.DoNot)
                box.Controls.Add(Ui.Text("✗  " + Lib.Expand(d), FontRole.Large, Brand.Danger));
            stack.Controls.Add(box);
        }
    }

    private void BuildMore()
    {
        var topic = _topic!;
        var stack = Ui.Stack(new Padding(12, 8, 12, 8));
        if (topic.Summary.Length > 0)
            stack.Controls.Add(Ui.Text(Lib.Expand(topic.Summary), FontRole.Large, padding: new Padding(0, 0, 0, 8)));

        stack.Controls.Add(Ui.Text("Related topics", FontRole.Heading, Brand.Navy));
        foreach (var id in topic.Related)
        {
            if (Lib.Find(id) is not { } related)
                continue;
            var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = Ui.Scaled(56), Margin = new Padding(0, 2, 0, 2) };
            row.FixedSize();
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            var open = Ui.Button(related.Title, ButtonStyle.Secondary, () => OpenTopic(related));
            open.TextAlign = ContentAlignment.MiddleLeft;
            open.Dock = DockStyle.Fill;
            var newWindow = Ui.Button("New window", ButtonStyle.Secondary, () => AppServices.Windows.Open(ToolId, related.Id + "|0|Steps"), FontRole.Normal);
            newWindow.Dock = DockStyle.Fill;
            row.Controls.Add(open, 0, 0);
            row.Controls.Add(newWindow, 1, 0);
            stack.Controls.Add(row);
        }

        stack.Controls.Add(Ui.Text("Sources", FontRole.Heading, Brand.Navy, new Padding(0, 14, 0, 2)));
        foreach (var s in topic.Sources)
        {
            var line = $"•  {s.Body}: {s.Title}" + (s.Year is { } y ? $" ({y})" : "") + (s.Section.Length > 0 ? $" – {s.Section}" : "") + (s.Url.Length > 0 ? $"\n    {s.Url}" : "");
            stack.Controls.Add(Ui.Text(line));
        }

        stack.Controls.Add(Ui.Text("Content review", FontRole.Heading, Brand.Navy, new Padding(0, 14, 0, 2)));
        var r = topic.Review;
        stack.Controls.Add(Ui.Text($"Status: {r.Status}\nReviewed by: {(r.ReviewedBy.Length > 0 ? r.ReviewedBy : "—")}\nReviewed on: {(r.ReviewedOn.Length > 0 ? r.ReviewedOn : "—")}" +
                                   (r.Notes.Length > 0 ? "\n" + r.Notes : ""), fore: r.IsReviewed ? null : Brand.Danger));
        stack.Controls.Add(Ui.Text($"File: {topic.SourceFile}", fore: Brand.Muted));
        stack.Controls.Add(Ui.Text(Lib.Guide.Disclaimer, FontRole.Bold, padding: new Padding(0, 14, 0, 2)));
        _content.Controls.Add(stack);
    }

    private void AddStep(FlowLayoutPanel stack, FirstAidStep step, int? numbered, bool large)
    {
        var title = (numbered is { } n ? $"{n}. " : "") + Lib.Expand(step.Title);
        stack.Controls.Add(Ui.Text(title, large ? FontRole.Title : FontRole.Heading, Brand.Navy, new Padding(0, numbered is null ? 4 : 12, 0, 4)));
        if (step.Text.Length > 0)
            stack.Controls.Add(Ui.Text(Lib.Expand(step.Text), large ? FontRole.Large : FontRole.Normal, padding: new Padding(0, 2, 0, 4)));
        foreach (var p in step.Points)
            stack.Controls.Add(Ui.Text("•  " + Lib.Expand(p), large ? FontRole.Large : FontRole.Normal, padding: new Padding(12, 2, 0, 2)));
        if (step.Caution.Length > 0)
        {
            var box = new VStack { BackColor = Brand.Amber, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 6, 0, 6) };
            box.Controls.Add(Ui.Text("⚠  " + Lib.Expand(step.Caution), large ? FontRole.Large : FontRole.Bold, Brand.Navy));
            stack.Controls.Add(box);
        }
        if (step.Action is { } action)
            stack.Controls.Add(ActionButton(action));
    }

    private Button ActionButton(StepAction action)
    {
        var style = action.Type switch { StepActionType.Cpr => ButtonStyle.Danger, StepActionType.Timer => ButtonStyle.Accent, _ => ButtonStyle.Primary };
        Button? button = null;
        button = Ui.Button(Lib.Expand(action.Label), style, () => RunAction(action, button!), FontRole.Heading);
        button.Height = Ui.Scaled(62);
        button.Margin = new Padding(0, 8, 0, 8);
        return button;
    }

    private void RunAction(StepAction action, Button button)
    {
        switch (action.Type)
        {
            case StepActionType.Cpr:
                AppServices.Windows.Open(CprWindow.ToolId, string.IsNullOrEmpty(action.Target) ? "adult" : action.Target);
                break;
            case StepActionType.Topic:
                if (Lib.Find(action.Target) is { } target)
                    OpenTopic(target);
                break;
            case StepActionType.Timer when button.Tag is FieldTimer:
                AppServices.Windows.Open(TimersWindow.ToolId);
                break;
            case StepActionType.Timer:
                var timer = StartTimer(action);
                if (timer == null)
                    return;
                button.Tag = timer;
                button.Text = $"✓ {timer.Label} – started {timer.StartedLocal:HH:mm}. Tap to view timers";
                var (back, fore) = Ui.Colors(ButtonStyle.Success);
                UiMode.SetColors(button, back, fore);
                break;
        }
    }

    private FieldTimer? StartTimer(StepAction a)
    {
        var label = string.IsNullOrWhiteSpace(a.TimerLabel) ? a.Label : a.TimerLabel;
        var source = $"First Aid · {_topic?.Title}";
        if (a.Mode == "countdown")
            return AppServices.Timers.StartCountdown(label, TimeSpan.FromMinutes(a.Minutes), source);
        DateTime? start = null;
        if (a.AskStartTime)
        {
            start = StartTimeDialog.Ask(this, $"When did it start? ({label})");
            if (start == null)
                return null;
        }
        return AppServices.Timers.StartStopwatch(label, source, a.RemindEveryMinutes, a.AlertAfterMinutes, start);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_topic != null && !_search.Focused)
        {
            switch (keyData)
            {
                case Keys.Right or Keys.PageDown when _view == TopicView.Steps && _step < _topic.Steps.Count - 1:
                    _step++;
                    ShowView(TopicView.Steps);
                    return true;
                case Keys.Left or Keys.PageUp when _view == TopicView.Steps && _step > 0:
                    _step--;
                    ShowView(TopicView.Steps);
                    return true;
                case Keys.Escape:
                    ShowHome();
                    return true;
            }
        }
        if (keyData == (Keys.Control | Keys.F))
        {
            ShowHome();
            _search.SelectAll();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
