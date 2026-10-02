using FieldToolkit.Core;
using FieldToolkit.Core.Checklists;
using FieldToolkit.Core.Kit;

namespace FieldToolkit.Tests;

public class ChecklistTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 7, 0, 0, DateTimeKind.Utc);

    private static ChecklistTemplate Template() => new()
    {
        Name = "Pre-trip",
        Lines = ChecklistText.Parse("# Outside\nTyres\n- Lights\n\n# Inside\nFuel\n#\n")
    };

    [Fact]
    public void Text_format_round_trips_headings_and_items()
    {
        var t = Template();
        Assert.Equal(5, t.Lines.Count);
        Assert.Equal(3, t.ItemCount);
        Assert.True(t.Lines[0].Heading);
        Assert.Equal("Lights", t.Lines[2].Text);
        Assert.Equal(ChecklistText.Format(t.Lines), ChecklistText.Format(ChecklistText.Parse(ChecklistText.Format(t.Lines))));
    }

    [Fact]
    public void Run_records_states_times_and_faults()
    {
        var run = ChecklistRun.Start(Template(), "J. Smith", "Unit 3", T0);
        var items = run.Items.ToList();
        run.Set(items[0], CheckState.Ok, T0.AddMinutes(1));
        run.Set(items[1], CheckState.Fault, T0.AddMinutes(2), "Left indicator out");
        Assert.Equal(2, run.Done);
        Assert.Equal(1, run.Faults);
        Assert.Equal(T0.AddMinutes(2), items[1].CheckedUtc);

        // Tapping the same state again clears it.
        run.Set(items[0], CheckState.Ok, T0.AddMinutes(3));
        Assert.Equal(CheckState.Open, items[0].State);
        Assert.Null(items[0].CheckedUtc);

        var report = run.Report();
        Assert.Contains("PRE-TRIP – UNIT 3", report);
        Assert.Contains("[FAULT] Lights", report);
        Assert.Contains("Left indicator out", report);
        Assert.Contains("2 NOT CHECKED", report);
        Assert.Contains("--- Outside", report);
    }

    [Fact]
    public void Headings_cannot_be_checked()
    {
        var run = ChecklistRun.Start(Template(), "", "", T0);
        run.Set(run.Lines[0], CheckState.Ok, T0);
        Assert.Equal(CheckState.Open, run.Lines[0].State);
    }

    [Fact]
    public void Shipped_templates_load()
    {
        var store = new JsonListStore<ChecklistTemplate>(TestPaths.Content("checklists", "templates.json"));
        Assert.True(store.Loaded);
        Assert.Equal(3, store.Items.Count);
        Assert.All(store.Items, t => Assert.True(t.ItemCount > 5));
        Assert.Equal(store.Items.Count, store.Items.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Store_round_trips_runs()
    {
        var path = Path.Combine(TestPaths.TempFolder(), "runs.json");
        var store = new JsonListStore<ChecklistRun>(path);
        var run = ChecklistRun.Start(Template(), "A", "B", T0);
        run.Set(run.Items.First(), CheckState.NotApplicable, T0);
        store.Items.Add(run);
        store.Save();
        var again = new JsonListStore<ChecklistRun>(path);
        Assert.Equal(CheckState.NotApplicable, again.Items.Single().Items.First().State);
    }

    [Fact]
    public void Unreadable_store_is_kept_aside()
    {
        var path = Path.Combine(TestPaths.TempFolder(), "runs.json");
        File.WriteAllText(path, "{ broken");
        var store = new JsonListStore<ChecklistRun>(path);
        Assert.Empty(store.Items);
        Assert.NotNull(store.LoadError);
        Assert.True(File.Exists(path + ".bad"));
    }
}

public class KitTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);

    private static KitItem Item(string name, int? days, int warn = 30) =>
        new() { Name = name, Expires = days is { } d ? Today.AddDays(d) : null, WarnDays = warn };

    [Theory]
    [InlineData(-1, KitStatus.Expired)]
    [InlineData(0, KitStatus.DueSoon)]
    [InlineData(30, KitStatus.DueSoon)]
    [InlineData(31, KitStatus.Ok)]
    public void Status_uses_warning_window(int days, KitStatus expected) => Assert.Equal(expected, KitRules.Status(Item("x", days), Today));

    [Fact]
    public void Undated_items_have_no_status() => Assert.Equal(KitStatus.NoDate, KitRules.Status(Item("x", null), Today));

    [Fact]
    public void Sorted_and_summarised()
    {
        var items = new[] { Item("Ok", 200), Item("None", null), Item("Due", 10), Item("Old", -5), Item("Older", -50) };
        Assert.Equal(new[] { "Older", "Old", "Due", "Ok", "None" }, KitRules.Sorted(items, Today).Select(i => i.Name));
        Assert.Equal((2, 1), KitRules.Summary(items, Today));
        Assert.Contains("2 expired, 1 due soon", KitRules.Report(items, Today));
    }

    [Theory]
    [InlineData(-3, "EXPIRED 3 days ago")]
    [InlineData(0, "Expires TODAY")]
    [InlineData(12, "Expires in 12 days")]
    public void Describes_dates(int days, string expected) => Assert.StartsWith(expected, KitRules.Describe(Item("x", days), Today));

    [Fact]
    public void Dates_persist()
    {
        var path = Path.Combine(TestPaths.TempFolder(), "kit.json");
        var store = new JsonListStore<KitItem>(path);
        store.Items.Add(Item("AED pads", 100));
        store.Save();
        Assert.Equal(Today.AddDays(100), new JsonListStore<KitItem>(path).Items.Single().Expires);
    }
}
