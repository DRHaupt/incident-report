using System.Reflection;
using System.Text;

namespace FieldToolkit.Core;

/// <summary>RFC 4180 CSV for simple records: the header row holds the property names (case-insensitive).</summary>
public static class Csv
{
    public static string Write<T>(IEnumerable<T> rows)
    {
        var props = Props<T>();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", props.Select(p => Quote(p.Name))));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", props.Select(p => Quote(p.GetValue(row)?.ToString() ?? ""))));
        return sb.ToString();
    }

    public static List<T> Read<T>(string text) where T : new()
    {
        var records = Parse(text);
        if (records.Count == 0)
            return new List<T>();
        var props = Props<T>().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var header = records[0].Select(h => props.TryGetValue(h.Trim(), out var p) ? p : null).ToList();
        if (header.All(h => h == null))
            throw new FormatException($"The first row must name the columns ({string.Join(", ", props.Keys)}).");
        var result = new List<T>();
        foreach (var r in records.Skip(1))
        {
            if (r.All(string.IsNullOrWhiteSpace))
                continue;
            var item = new T();
            for (var i = 0; i < r.Count && i < header.Count; i++)
                header[i]?.SetValue(item, r[i]);
            result.Add(item);
        }
        return result;
    }

    private static PropertyInfo[] Props<T>() =>
        typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite).ToArray();

    private static string Quote(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>Splits CSV text into records and fields (quoted fields may contain commas, quotes and line breaks).</summary>
    public static List<List<string>> Parse(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': record.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    record.Add(field.ToString()); field.Clear();
                    records.Add(record); record = new List<string>();
                    break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }
}
