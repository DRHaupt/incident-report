using Shared;
using Shared.UI;

namespace FieldMap.Framework;

/// <summary>
/// Base class for every Field Map window: an independent top-level window with the suite header (pin on top, new window,
/// home), the red SAMPLE DATA banner while sample data is in use, and live Touch/Daylight/Night styling.
/// </summary>
public abstract class ToolWindow : Form
{
    private readonly Button _pin;
    private readonly Label _title;
    private readonly Label _sampleBanner;

    protected ToolWindow(ToolDescriptor tool)
    {
        Tool = tool;
        Text = tool.Title;
        Icon = Branding.ToolIcon(tool.IconName);
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        MinimumSize = new Size(360, 320);
        ClientSize = tool.DefaultSize;
        KeyPreview = true;

        Body = new Panel { Dock = DockStyle.Fill };

        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 56, ColumnCount = 5, RowCount = 1, BackColor = Brand.Navy, Padding = new Padding(6, 4, 6, 4) };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++)
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new PictureBox { Image = Branding.ToolImage(tool.IconName, 48), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 6, 2) }, 0, 0);
        _title = new Label { Text = tool.Title, Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false }.Styled(FontRole.Title);
        header.Controls.Add(_title, 1, 0);
        _pin = HeaderButton("Pin on top", TogglePin);
        if (!tool.SingleInstance)
            header.Controls.Add(HeaderButton("+ Window", () => AppServices.Windows.Open(Tool.Id, NewWindowState())), 2, 0);
        header.Controls.Add(_pin, 3, 0);
        header.Controls.Add(HeaderButton("Home", AppServices.ShowLauncher), 4, 0);

        _sampleBanner = new Label
        {
            Dock = DockStyle.Top, Height = 34, BackColor = Brand.Danger, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter,
            Text = "SAMPLE DATA – fictitious places, not for operational use. Install real map data under Data & updates.", AutoEllipsis = true
        }.Styled(FontRole.Bold);

        Controls.Add(Body);
        Controls.Add(_sampleBanner);
        Controls.Add(header);
        UiMode.Track(this);
        UiMode.Changed += OnUiModeChanged;
        AppServices.Data.Changed += OnDataChanged;
        FormClosed += (_, _) =>
        {
            UiMode.Changed -= OnUiModeChanged;
            AppServices.Data.Changed -= OnDataChanged;
        };
        UpdateSampleBanner();
    }

    private void OnDataChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;
        UpdateSampleBanner();
        OnMapDataChanged();
    }

    private void UpdateSampleBanner() => _sampleBanner.Visible = AppServices.Data.IsSample || !AppServices.Data.HasData;

    protected void SetBannerText(string text) => _sampleBanner.Text = text;

    private Button HeaderButton(string text, Action onClick)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false, BackColor = Color.FromArgb(0x41, 0x52, 0x6A), ForeColor = Color.White,
            Margin = new Padding(3, 0, 0, 0), Padding = new Padding(8, 0, 8, 0), Cursor = Cursors.Hand, UseMnemonic = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => onClick();
        return b.Styled(FontRole.Bold);
    }

    public ToolDescriptor Tool { get; }

    protected Panel Body { get; }

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

    protected void SetTitle(string title)
    {
        _title.Text = title;
        Text = title == Tool.Title ? Tool.Title : $"{title} – {Branding.ProductName}";
        AppServices.Windows.NotifyChanged();
    }

    /// <summary>State for session restore. Empty = fresh window.</summary>
    public virtual string SaveState() => "";

    protected virtual string? NewWindowState() => null;

    protected virtual void OnUiModeChanged(object? sender, EventArgs e)
    {
    }

    /// <summary>Map data was reloaded (update, rollback, package install, data folder change).</summary>
    protected virtual void OnMapDataChanged()
    {
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (UiModeButtons.HandleShortcut(keyData))
            return true;
        if (keyData == (Keys.Control | Keys.H))
        {
            AppServices.ShowLauncher();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
