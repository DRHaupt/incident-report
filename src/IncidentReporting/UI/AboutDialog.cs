using System.Reflection;
using IncidentReporting.Data;
using IncidentReporting.Services;
using IncidentReporting.Templates;

namespace IncidentReporting.UI;

public sealed class AboutDialog : Form
{
    public AboutDialog()
    {
        Text = "About Incident Report Manager";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 250);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        var band = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.FromArgb(0x2B, 0x3A, 0x4F) };
        band.Controls.Add(new Label
        {
            Text = "Incident Report Manager",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(16, 18)
        });

        var info = new Label
        {
            Location = new Point(18, 80),
            Size = new Size(410, 120),
            Text = $"Version {version}\r\n\r\n" +
                   $"{FormCatalog.All.Count} report forms in {FormCatalog.Categories.Count()} categories.\r\n\r\n" +
                   $"Database: {AppSession.Settings.EffectiveDatabasePath}\r\n" +
                   $"Settings: {AppSettings.AppDataFolder}"
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Location = new Point(334, 210) };
        Controls.Add(band);
        Controls.Add(info);
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = ok;
    }
}
