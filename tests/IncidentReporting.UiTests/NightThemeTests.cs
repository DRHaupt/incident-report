using System.Drawing.Imaging;
using System.Globalization;
using IncidentReporting.Models;
using IncidentReporting.Services;
using IncidentReporting.Templates;
using IncidentReporting.UI;
using Shared.UI;

namespace IncidentReporting.UiTests;

/// <summary>
/// Opens the main window and a report in the Night theme at the size they have on a tablet set to 150 % display
/// scaling (text 1.5× larger and every fixed size scaled to match), checks that text fits its rows, header and
/// buttons, and saves screenshots (ui-screenshots/ next to the test output) for a person to look at.
/// </summary>
public class NightThemeTests
{
    private static readonly string Shots = Path.Combine(AppContext.BaseDirectory, "ui-screenshots");

    private static void Pump(int milliseconds = 50)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Application.DoEvents();
            Thread.Sleep(5);
        } while (DateTime.UtcNow < until);
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(5)), "UI test timed out.");
        if (error != null)
            throw new Exception("UI test failed: " + error, error);
    }

    private static void Shot(Form form, string name)
    {
        Directory.CreateDirectory(Shots);
        form.Activate();
        Pump(400);
        var b = form.Bounds;
        using var bmp = new Bitmap(b.Width, b.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(b.Location, Point.Empty, b.Size);
        bmp.Save(Path.Combine(Shots, name + ".png"), ImageFormat.Png);
    }

    private static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));

    private static int TextHeight(Control c) => (int)Math.Ceiling(c.Font.GetHeight());

    [Fact]
    public void Night_theme_at_150_percent_fits_and_colours_every_part()
    {
        RunSta(() =>
        {
            Application.EnableVisualStyles();
            AppSession.Initialize();
            var db = Path.Combine(Path.GetTempPath(), "irm-ui-" + Guid.NewGuid().ToString("N") + ".db");
            AppSession.Settings.DatabasePath = db;
            AppSession.ReloadRepository();
            // 150 % display scaling: the text is 1.5× larger (13.5 pt instead of 9 pt) and fixed sizes scale with it.
            UiMode.Initialize(touch: false, baseFontSize: 13.5f, night: true);
            UiMode.ScaleWithDpi = true;
            UiMode.DpiScaleOverride = 1.5f;
            try
            {
                using var main = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1600, 1000) };
                main.Show();
                Pump(800);

                // Sidebar entries fit the sidebar.
                var tree = All(main).OfType<TreeView>().Single();
                var split = All(main).OfType<SplitContainer>().First(s => s.Panel1.Controls.Contains(tree));
                Assert.True(tree.ItemHeight >= TextHeight(tree), $"Tree rows {tree.ItemHeight}px for {TextHeight(tree)}px text");
                foreach (var node in tree.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>().Prepend(n)))
                {
                    var need = TextRenderer.MeasureText(node.Text, tree.Font).Width + tree.Indent * (node.Level + 1);
                    Assert.True(need <= split.SplitterDistance, $"\"{node.Text}\" needs {need}px; sidebar is {split.SplitterDistance}px");
                }
                // Dark surfaces, including the list and its header (drawn by the theme).
                var list = All(main).OfType<ListView>().Single();
                Assert.Equal(UiMode.NightPalette.Surface, list.BackColor);
                Assert.True(list.OwnerDraw);
                Assert.False(list.GridLines);
                Shot(main, "main-night-150");

                // A new report.
                var form = FormCatalog.Find("CON-1540") ?? FormCatalog.All[0];
                var today = DateTime.Today.ToString(FieldFormats.Date, CultureInfo.InvariantCulture);
                var report = new IncidentReport { FormCode = form.Code, Status = ReportStatus.Draft };
                report.Values[CommonKeys.IncidentDate] = today;
                using var editor = new ReportEditorForm(report, form) { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1500, 950) };
                editor.Show();
                Pump(800);

                var nav = All(editor).OfType<ListBox>().First();
                Assert.True(nav.ItemHeight >= TextHeight(nav) + 4, $"Section list rows {nav.ItemHeight}px for {TextHeight(nav)}px text");
                Assert.Equal(UiMode.NightPalette.Surface, nav.BackColor);
                // Docked header labels (title, report number line) are tall enough for their text.
                foreach (var label in All(editor).OfType<Label>().Where(l => l.Dock == DockStyle.Top && !l.AutoSize && l.Text.Length > 0))
                    Assert.True(label.Height >= TextHeight(label), $"Label \"{label.Text}\" {label.Height}px for {TextHeight(label)}px text");
                // Buttons are wide and tall enough for their captions.
                foreach (var button in All(editor).OfType<Button>().Where(b => b.Visible && b.Text.Length > 0))
                {
                    var need = TextRenderer.MeasureText(button.Text.Replace("&&", "&"), button.Font);
                    Assert.True(button.Width >= need.Width + 8 && button.Height >= need.Height,
                        $"Button \"{button.Text}\" is {button.Size} for text {need}");
                }
                // Drop-downs and date pickers follow the theme.
                Assert.All(All(editor).OfType<ComboBox>(), c => Assert.Equal(FlatStyle.Flat, c.FlatStyle));
                Assert.All(All(editor).OfType<DateTimePicker>(), p => Assert.IsType<ThemedDateTimePicker>(p));
                Shot(editor, "report-night-150");

                // Same windows in Daylight and back, live.
                UiMode.SetTheme(UiTheme.Daylight);
                Pump(500);
                Shot(editor, "report-daylight-150");
                UiMode.SetTheme(UiTheme.Standard);
                Pump(500);
                Shot(main, "main-standard-150");
                editor.Close();
                main.Close();
            }
            finally
            {
                UiMode.DpiScaleOverride = 0;
                UiMode.ScaleWithDpi = false;
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { File.Delete(db); } catch (IOException) { }
            }
        });
    }
}
