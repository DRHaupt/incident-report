using System.Text;

namespace Shared.Core.Radio;

public sealed record PhoneticEntry(string Character, string Word, string Pronunciation);

/// <summary>The ICAO / NATO radiotelephony spelling alphabet and figures (ICAO Annex 10, Volume II).</summary>
public static class Phonetic
{
    public static readonly IReadOnlyList<PhoneticEntry> Letters = new PhoneticEntry[]
    {
        new("A", "Alfa", "AL-FAH"), new("B", "Bravo", "BRAH-VOH"), new("C", "Charlie", "CHAR-LEE"),
        new("D", "Delta", "DELL-TAH"), new("E", "Echo", "ECK-OH"), new("F", "Foxtrot", "FOKS-TROT"),
        new("G", "Golf", "GOLF"), new("H", "Hotel", "HOH-TELL"), new("I", "India", "IN-DEE-AH"),
        new("J", "Juliett", "JEW-LEE-ETT"), new("K", "Kilo", "KEY-LOH"), new("L", "Lima", "LEE-MAH"),
        new("M", "Mike", "MIKE"), new("N", "November", "NO-VEM-BER"), new("O", "Oscar", "OSS-CAH"),
        new("P", "Papa", "PAH-PAH"), new("Q", "Quebec", "KEH-BECK"), new("R", "Romeo", "ROW-ME-OH"),
        new("S", "Sierra", "SEE-AIR-RAH"), new("T", "Tango", "TANG-GO"), new("U", "Uniform", "YOU-NEE-FORM"),
        new("V", "Victor", "VIK-TAH"), new("W", "Whiskey", "WISS-KEY"), new("X", "X-ray", "ECKS-RAY"),
        new("Y", "Yankee", "YANG-KEY"), new("Z", "Zulu", "ZOO-LOO")
    };

    public static readonly IReadOnlyList<PhoneticEntry> Figures = new PhoneticEntry[]
    {
        new("0", "Zero", "ZE-RO"), new("1", "One", "WUN"), new("2", "Two", "TOO"), new("3", "Three", "TREE"),
        new("4", "Four", "FOW-ER"), new("5", "Five", "FIFE"), new("6", "Six", "SIX"), new("7", "Seven", "SEV-EN"),
        new("8", "Eight", "AIT"), new("9", "Nine", "NIN-ER"), new(".", "Decimal", "DAY-SEE-MAL"),
        new("00", "Hundred", "HUN-DRED"), new("000", "Thousand", "TOU-SAND")
    };

    private static readonly Dictionary<char, PhoneticEntry> Lookup =
        Letters.Concat(Figures.Take(11)).ToDictionary(e => e.Character[0], e => e);

    /// <summary>"AB 12" → "Alfa Bravo / One Two". Words are separated by " / "; unknown characters are kept as they are.</summary>
    public static string Spell(string text, bool pronunciation = false)
    {
        var words = new List<string>();
        foreach (var word in (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = new List<string>();
            foreach (var ch in word.ToUpperInvariant())
            {
                if (Lookup.TryGetValue(ch, out var e))
                    parts.Add(pronunciation ? e.Pronunciation : e.Word);
                else if (ch is '-' or ',' or '/')
                    continue;
                else
                    parts.Add(ch.ToString());
            }
            if (parts.Count > 0)
                words.Add(string.Join(" ", parts));
        }
        return string.Join(" / ", words);
    }

    /// <summary>Digits read one by one with the radio pronunciation: "105" → "WUN ZE-RO FIFE".</summary>
    public static string Figures1By1(string digits)
    {
        var sb = new StringBuilder();
        foreach (var ch in digits)
        {
            if (!Lookup.TryGetValue(ch, out var e))
                continue;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(e.Pronunciation);
        }
        return sb.ToString();
    }
}
