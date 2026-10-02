using System.Text;

namespace FieldToolkit.Core.Kit;

/// <summary>A tracked item with an expiry or service date: medical kit, extinguisher, AED pads, oxygen…</summary>
public sealed class KitItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";

    /// <summary>Where it is kept: vehicle, bag, station.</summary>
    public string Location { get; set; } = "";

    /// <summary>Serial, lot or asset number.</summary>
    public string Identifier { get; set; } = "";

    /// <summary>Expiry or next service date.</summary>
    public DateOnly? Expires { get; set; }

    /// <summary>Warn this many days before <see cref="Expires"/>.</summary>
    public int WarnDays { get; set; } = 30;

    public DateOnly? LastChecked { get; set; }
    public string Notes { get; set; } = "";
}

public enum KitStatus
{
    Expired,
    DueSoon,
    Ok,
    NoDate
}

public static class KitRules
{
    public static readonly string[] DefaultCategories =
        { "Medical kit", "Medication", "AED / defibrillator", "Oxygen", "Fire extinguisher", "Vehicle", "PPE", "Other" };

    public static int? DaysLeft(KitItem item, DateOnly today) => item.Expires is { } e ? e.DayNumber - today.DayNumber : null;

    public static KitStatus Status(KitItem item, DateOnly today) => DaysLeft(item, today) switch
    {
        null => KitStatus.NoDate,
        < 0 => KitStatus.Expired,
        var d when d <= Math.Max(0, item.WarnDays) => KitStatus.DueSoon,
        _ => KitStatus.Ok
    };

    /// <summary>Expired first, then due soon, then by date; undated items last.</summary>
    public static IEnumerable<KitItem> Sorted(IEnumerable<KitItem> items, DateOnly today) =>
        items.OrderBy(i => Status(i, today)).ThenBy(i => i.Expires ?? DateOnly.MaxValue).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);

    public static (int Expired, int DueSoon) Summary(IEnumerable<KitItem> items, DateOnly today)
    {
        var list = items.ToList();
        return (list.Count(i => Status(i, today) == KitStatus.Expired), list.Count(i => Status(i, today) == KitStatus.DueSoon));
    }

    /// <summary>"Expired 3 days ago" / "Expires today" / "Expires in 12 days (14 Oct 2026)".</summary>
    public static string Describe(KitItem item, DateOnly today)
    {
        if (DaysLeft(item, today) is not { } d)
            return "No expiry date";
        var date = item.Expires!.Value.ToString("dd MMM yyyy");
        return d switch
        {
            < -1 => $"EXPIRED {-d} days ago ({date})",
            -1 => $"EXPIRED yesterday ({date})",
            0 => $"Expires TODAY ({date})",
            1 => $"Expires tomorrow ({date})",
            _ => $"Expires in {d} days ({date})"
        };
    }

    public static string Report(IEnumerable<KitItem> items, DateOnly today)
    {
        var sb = new StringBuilder();
        var list = Sorted(items, today).ToList();
        var (expired, due) = Summary(list, today);
        sb.AppendLine($"KIT STATUS {today:dd MMM yyyy}: {expired} expired, {due} due soon, {list.Count} items");
        foreach (var i in list)
        {
            var where = string.Join(", ", new[] { i.Category, i.Location, i.Identifier }.Where(x => x.Length > 0));
            sb.AppendLine($"{i.Name}{(where.Length > 0 ? $" ({where})" : "")}: {Describe(i, today)}");
        }
        return sb.ToString().TrimEnd();
    }
}
