using System.Diagnostics;
using FieldToolkit.Core.FirstAid;
using FieldToolkit.Core.Timers;
using FieldToolkit.Framework;
using FieldToolkit.Tools.FirstAid;
using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Tools.Cpr;

/// <summary>
/// CPR helper: on-screen metronome (100–120/min from _cpr.json), optional click, compression counter, breath prompts
/// for 30:2 / 15:2, and a 2-minute swap reminder. Starting it also records "CPR started" in the shared timers.
/// </summary>
public sealed class CprWindow : ToolWindow, IAcceptsState
{
    public const string ToolId = "cpr";

    private readonly CprGuide _guide = AppServices.FirstAid.Cpr;
    private readonly CprMetronome _metronome = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTime _clockBase = DateTime.UtcNow;
    private readonly System.Windows.Forms.Timer _ticker = new() { Interval = 10 };
    private readonly ClickSounds _sounds = new();
    private readonly PulseDisplay _display = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly Button[] _presetButtons;
    private readonly FlowLayoutPanel _ratioRow = new() { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
    private readonly WrapLabel _guidance = new() { Padding = new Padding(10, 4, 10, 4) };
    private readonly Label _stats = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    private readonly Label _rateLabel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button _sound;
    private readonly Button _startStop;
    private readonly Button _breathsDone;
    private readonly Button _switchBanner;
    private CprPreset? _preset;
    private string _ratio = "30:2";
    private FieldTimer? _cprTimer;
    private DateTime _flashUntil;
    private DateTime _switchBannerUntil;

    public CprWindow(string? state) : base(ToolRegistry.Find(ToolId)!)
    {
        _metronome.MinRate = _guide.MinRate;
        _metronome.MaxRate = _guide.MaxRate;
        _metronome.Rate = AppServices.Settings.CprRate;
        _metronome.BreathPause = TimeSpan.FromSeconds(_guide.BreathPauseSeconds);
        _metronome.SwitchInterval = TimeSpan.FromMinutes(_guide.SwitchEveryMinutes);

        // Preset row: Adult / Child / Infant
        _presetButtons = _guide.Presets.Select(p => Ui.Button(p.Label, ButtonStyle.Secondary, () => SelectPreset(p))).ToArray();
        var presets = Ui.ButtonRow(_presetButtons.Length > 0 ? _presetButtons : new Control[] { new Label { Text = "No CPR presets in _cpr.json" } });
        presets.Dock = DockStyle.Top;
        presets.Height = 56;
        presets.Padding = new Padding(8, 6, 8, 0);

        var ratioPanel = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(8, 4, 8, 0) };
        ratioPanel.Controls.Add(_ratioRow);

        var guidancePanel = new VStack { Dock = DockStyle.Top, BackColor = Brand.Panel };
        _guidance.Styled(FontRole.Bold);
        guidancePanel.Controls.Add(_guidance);

        _switchBanner = Ui.Button("", ButtonStyle.Accent, () => _switchBannerUntil = DateTime.MinValue, FontRole.Heading);
        _switchBanner.Dock = DockStyle.Top;
        _switchBanner.Height = 58;
        _switchBanner.Visible = false;

        _breathsDone = Ui.Button("Breaths given – resume compressions ›", ButtonStyle.Accent, () => _metronome.BreathsDone(Now), FontRole.Heading);
        _breathsDone.Dock = DockStyle.Bottom;
        _breathsDone.Height = 64;
        _breathsDone.Visible = false;

        // Rate / sound / stats row
        var controls = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 60, ColumnCount = 5, RowCount = 1, Padding = new Padding(8, 4, 8, 4) };
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var slower = Ui.Button("−", ButtonStyle.Secondary, () => ChangeRate(-5), FontRole.Heading);
        var faster = Ui.Button("+", ButtonStyle.Secondary, () => ChangeRate(5), FontRole.Heading);
        _sound = Ui.Button("", ButtonStyle.Secondary, ToggleSound);
        foreach (var b in new[] { slower, faster, _sound })
            b.Dock = DockStyle.Fill;
        _rateLabel.Styled(FontRole.Heading);
        _stats.Styled(FontRole.Bold);
        controls.Controls.Add(slower, 0, 0);
        controls.Controls.Add(_rateLabel, 1, 0);
        controls.Controls.Add(faster, 2, 0);
        controls.Controls.Add(_sound, 3, 0);
        controls.Controls.Add(_stats, 4, 0);

        _startStop = Ui.Button("START", ButtonStyle.Danger, StartStop, FontRole.Title);
        var startPanel = new Panel { Dock = DockStyle.Bottom, Height = 84, Padding = new Padding(8, 4, 8, 8) };
        _startStop.Dock = DockStyle.Fill;
        startPanel.Controls.Add(_startStop);

        var instructions = Ui.Button("Full CPR instructions", ButtonStyle.Secondary, OpenInstructions, FontRole.Normal);
        instructions.Dock = DockStyle.Bottom;
        instructions.Height = 44;

        Body.Controls.Add(_display);
        Body.Controls.Add(_switchBanner);
        Body.Controls.Add(guidancePanel);
        Body.Controls.Add(ratioPanel);
        Body.Controls.Add(presets);
        Body.Controls.Add(_breathsDone);
        Body.Controls.Add(controls);
        Body.Controls.Add(startPanel);
        Body.Controls.Add(instructions);

        _ticker.Tick += (_, _) => OnTick();
        FormClosed += (_, _) =>
        {
            _ticker.Dispose();
            _sounds.Dispose();
        };
        UpdateRateAndSound();
        ApplyState(state ?? "adult");
        _ticker.Start();
    }

    private DateTime Now => _clockBase + _clock.Elapsed;

    internal CprMetronome Metronome => _metronome;

    public override string SaveState() => _preset?.Id ?? "adult";

    public void ApplyState(string state)
    {
        var preset = _guide.Preset(state) ?? _guide.Presets.FirstOrDefault();
        if (preset != null)
            SelectPreset(preset);
    }

    private void SelectPreset(CprPreset preset)
    {
        _preset = preset;
        for (var i = 0; i < _presetButtons.Length; i++)
            Ui.SetSelected(_presetButtons[i], _guide.Presets[i] == preset, ButtonStyle.Primary);
        SetTitle($"CPR Helper – {preset.Label}");
        _guidance.Text = $"Push {preset.Depth}.  {preset.HandPosition}  Let the chest fully recoil.";
        BuildRatioButtons(preset);
        SelectRatio(preset.Ratios.Contains(_ratio) ? _ratio : preset.DefaultRatio);
    }

    private void BuildRatioButtons(CprPreset preset)
    {
        _ratioRow.SuspendLayout();
        foreach (Control c in _ratioRow.Controls.Cast<Control>().ToList())
            c.Dispose();
        _ratioRow.Controls.Clear();
        foreach (var r in preset.Ratios)
        {
            var ratio = r;
            var caption = r == "continuous" ? "Compressions only" : r;
            var b = Ui.Button(caption, ButtonStyle.Secondary, () => SelectRatio(ratio));
            b.Tag = r;
            b.AutoSize = true;
            b.MinimumSize = new Size(Ui.Scaled(110), 0);
            b.Height = Ui.Scaled(46);
            b.Margin = new Padding(0, 0, 6, 0);
            _ratioRow.Controls.Add(b);
        }
        UiMode.Apply(_ratioRow);
        _ratioRow.ResumeLayout(true);
    }

    private void SelectRatio(string ratio)
    {
        _ratio = ratio;
        var (compressions, breaths) = CprPreset.ParseRatio(ratio);
        _metronome.CompressionsPerCycle = compressions;
        _metronome.BreathsPerCycle = breaths;
        foreach (var b in _ratioRow.Controls.OfType<Button>())
            Ui.SetSelected(b, (string)b.Tag! == ratio);
        var note = _preset != null && _preset.RatioNotes.TryGetValue(ratio, out var n) ? n : "";
        _guidance.Text = $"Push {_preset?.Depth}.  {_preset?.HandPosition}  Let the chest fully recoil." + (note.Length > 0 ? $"\n{(ratio == "continuous" ? "Compressions only" : ratio)}: {note}" : "");
        RefreshDisplay();
    }

    private void ChangeRate(int delta)
    {
        _metronome.Rate += delta;
        AppServices.Settings.CprRate = _metronome.Rate;
        AppServices.Settings.Save();
        UpdateRateAndSound();
    }

    private void ToggleSound()
    {
        AppServices.Settings.CprSound = !AppServices.Settings.CprSound;
        AppServices.Settings.Save();
        UpdateRateAndSound();
    }

    private void UpdateRateAndSound()
    {
        _rateLabel.Text = $"{_metronome.Rate}/min";
        _sound.Text = AppServices.Settings.CprSound ? "Click: ON" : "Click: OFF";
    }

    internal void StartStop()
    {
        if (_metronome.Phase == CprPhase.Stopped)
        {
            _metronome.Start(Now);
            _cprTimer ??= AppServices.Timers.StartStopwatch("CPR started", "CPR Helper");
            _startStop.Text = "STOP";
            UiMode.SetColors(_startStop, Brand.Navy, Color.White);
        }
        else
        {
            _metronome.Stop(Now);
            _startStop.Text = "RESUME";
            UiMode.SetColors(_startStop, Brand.Danger, Color.White);
        }
        RefreshDisplay();
    }

    private void OnTick()
    {
        var now = Now;
        foreach (var e in _metronome.Update(now))
        {
            switch (e)
            {
                case CprEvent.Beat:
                    _flashUntil = now.AddMilliseconds(140);
                    if (AppServices.Settings.CprSound)
                        _sounds.Click();
                    break;
                case CprEvent.BreathsDue:
                    if (AppServices.Settings.CprSound)
                        _sounds.Breaths();
                    break;
                case CprEvent.SwitchDue:
                    _switchBannerUntil = now.AddSeconds(15);
                    _switchBanner.Text = $"{_guide.SwitchEveryMinutes} minutes – swap compressors if possible · follow AED prompts";
                    break;
            }
        }
        _display.Flash = now < _flashUntil;
        var showSwitch = now < _switchBannerUntil;
        if (_switchBanner.Visible != showSwitch)
            _switchBanner.Visible = showSwitch;
        var breathing = _metronome.Phase == CprPhase.Breaths;
        if (_breathsDone.Visible != breathing)
            _breathsDone.Visible = breathing;
        RefreshDisplay();
    }

    private string _lastDisplay = "";

    private void RefreshDisplay()
    {
        var now = Now;
        string main, sub;
        switch (_metronome.Phase)
        {
            case CprPhase.Breaths:
                main = $"{_metronome.BreathsPerCycle} BREATHS";
                sub = $"then compressions in {Math.Ceiling(_metronome.BreathTimeLeft(now).TotalSeconds):0} s";
                break;
            case CprPhase.Compressions:
                main = _metronome.Count.ToString();
                sub = _metronome.CompressionsPerCycle > 0 ? $"of {_metronome.CompressionsPerCycle} · PUSH" : "PUSH HARD · PUSH FAST";
                break;
            default:
                main = _metronome.TotalCompressions == 0 ? "READY" : "PAUSED";
                sub = _metronome.TotalCompressions == 0 ? "Tap START" : "Tap RESUME";
                break;
        }
        var key = main + sub + _metronome.Phase;
        if (key != _lastDisplay)
        {
            _lastDisplay = key;
            _display.Show(main, sub, _metronome.Phase == CprPhase.Breaths);
        }
        var stats = $"Time {FieldTimer.Format(_metronome.Elapsed(now))} · Cycles {_metronome.CyclesCompleted} · Total {_metronome.TotalCompressions}";
        if (_stats.Text != stats)
            _stats.Text = stats;
    }

    private void OpenInstructions()
    {
        var topic = _preset?.Topic is { Length: > 0 } t ? t : "cpr-adult";
        AppServices.Windows.Open(FirstAidWindow.ToolId, topic + "|0|Steps");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Space)
        {
            StartStop();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
