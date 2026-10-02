using FieldToolkit.UI;
using Shared;
using Shared.UI;

namespace FieldToolkit.Framework;

/// <summary>
/// Base class for every tool window: an independent top-level window (own taskbar entry, resizable, movable) with the
/// toolkit header (pin on top, new window, launcher), the running-timers strip, and live Touch/Daylight styling.
/// Subclasses add their UI to <see cref="Body"/> and describe their state for session restore.
/// </summary>
public abstract class ToolWindow : Form
{
    private readonly Button _pin;
    private readonly Label _title;

    protected ToolWindow(ToolDescriptor tool)
    {
        Tool = tool;
        Text = tool.Title;
        Icon = Branding.ToolIcon(tool.IconName);
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        MinimumSize = new Size(320, 300); // small enough for a 3×2 grid on a 1024×768 screen
        ClientSize = tool.DefaultSize;
        KeyPreview = true;

        Body = new Panel { Dock = DockStyle.Fill };

        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 56, ColumnCount = 5, RowCount = 1, BackColor = Brand.Navy, Padding = new Padding(6, 4, 6, 4) };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new PictureBox { Image = Branding.ToolImage(tool.IconName, 48), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 6, 2) }, 0, 0);
        _title = new Label { Text = tool.Title, Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false }.Styled(FontRole.Title);
        header.Controls.Add(_title, 1, 0);

        _pin = HeaderButton("Pin on top", TogglePin);
        if (!tool.SingleInstance)
            header.Controls.Add(HeaderButton("+ Window", () => AppServices.Windows.Open(Tool.Id, NewWindowState())), 2, 0);
        header.Controls.Add(_pin, 3, 0);
        header.Controls.Add(HeaderButton("Launcher", AppServices.ShowLauncher), 4, 0);

        Controls.Add(Body);
        Controls.Add(new TimerStrip());
        Controls.Add(header);
        UiMode.Track(this);
        UiMode.Changed += OnUiModeChanged;
        FormClosed += (_, _) => UiMode.Changed -= OnUiModeChanged;
    }

    private Button HeaderButton(string text, Action onClick)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            BackColor = Color.FromArgb(0x41, 0x52, 0x6A),
            ForeColor = Color.White,
            Margin = new Padding(3, 0, 0, 0),
            Padding = new Padding(8, 0, 8, 0),
            Cursor = Cursors.Hand,
            UseMnemonic = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => onClick();
        return b.Styled(FontRole.Bold);
    }

    public ToolDescriptor Tool { get; }

    /// <summary>Container for the tool's own UI (between the header and the timer strip).</summary>
    protected Panel Body { get; }

    /// <summary>Kept on top of other windows.</summary>
    public bool Pinned
    {
        get => TopMost;
        set
        {
            TopMost = value;
            _pin.Text = value ? "Pinned ✓" : "Pin on top";
            var (back, fore) = value ? (Brand.Amber, Brand.Navy) : (Color.FromArgb(0x41, 0x52, 0x6A), Color.White);
            UiMode.SetColors(_pin, back, fore);
            AppServices.Windows.NotifyChanged();
        }
    }

    private void TogglePin() => Pinned = !Pinned;

    /// <summary>Sets the header title and the taskbar caption, e.g. "First Aid – Burns".</summary>
    protected void SetTitle(string title)
    {
        _title.Text = title;
        Text = title == Tool.Title ? Tool.Title : $"{title} – {Tool.Title}";
        AppServices.Windows.NotifyChanged();
    }

    /// <summary>The tool's state for session restore (e.g. open topic and step). Empty = fresh window.</summary>
    public virtual string SaveState() => "";

    /// <summary>State for a window opened with the header's "+ Window" button.</summary>
    protected virtual string? NewWindowState() => null;

    protected virtual void OnUiModeChanged(object? sender, EventArgs e)
    {
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (UiModeButtons.HandleShortcut(keyData))
            return true;
        if (keyData == (Keys.Control | Keys.L))
        {
            AppServices.ShowLauncher();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
