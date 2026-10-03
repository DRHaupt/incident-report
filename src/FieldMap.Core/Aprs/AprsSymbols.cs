namespace FieldMap.Core.Aprs;

public enum AprsSymbolGroup
{
    Other,
    Emergency,
    Vehicle,
    Person,
    Aircraft,
    Boat,
    Weather,
    Infrastructure,
    Facility
}

/// <summary>Names and plain-text map glyphs for the common APRS symbols (primary '/' and alternate '\' tables).</summary>
public static class AprsSymbols
{
    private static readonly Dictionary<char, (string Name, string Glyph, AprsSymbolGroup Group)> Primary = new()
    {
        ['!'] = ("Police station", "Pol", AprsSymbolGroup.Facility),
        ['#'] = ("Digipeater", "Dg", AprsSymbolGroup.Infrastructure),
        ['$'] = ("Phone", "Ph", AprsSymbolGroup.Other),
        ['&'] = ("HF gateway", "GW", AprsSymbolGroup.Infrastructure),
        ['\''] = ("Small aircraft", "Air", AprsSymbolGroup.Aircraft),
        ['('] = ("Mobile satellite station", "Sat", AprsSymbolGroup.Vehicle),
        [')'] = ("Wheelchair", "WC", AprsSymbolGroup.Person),
        ['*'] = ("Snowmobile", "Sno", AprsSymbolGroup.Vehicle),
        ['+'] = ("Red Cross", "+", AprsSymbolGroup.Facility),
        [','] = ("Scouts", "Sc", AprsSymbolGroup.Person),
        ['-'] = ("House", "QTH", AprsSymbolGroup.Other),
        ['.'] = ("X", "X", AprsSymbolGroup.Other),
        ['/'] = ("Dot", "•", AprsSymbolGroup.Other),
        [':'] = ("Fire", "Fire", AprsSymbolGroup.Emergency),
        [';'] = ("Campground", "Cmp", AprsSymbolGroup.Other),
        ['<'] = ("Motorcycle", "MC", AprsSymbolGroup.Vehicle),
        ['='] = ("Railway engine", "Rail", AprsSymbolGroup.Vehicle),
        ['>'] = ("Car", "Car", AprsSymbolGroup.Vehicle),
        ['?'] = ("File server", "Srv", AprsSymbolGroup.Infrastructure),
        ['A'] = ("Aid station", "Aid", AprsSymbolGroup.Facility),
        ['B'] = ("BBS", "BBS", AprsSymbolGroup.Infrastructure),
        ['C'] = ("Canoe", "Can", AprsSymbolGroup.Boat),
        ['E'] = ("Eyeball (event)", "Eye", AprsSymbolGroup.Other),
        ['F'] = ("Farm vehicle", "Trac", AprsSymbolGroup.Vehicle),
        ['H'] = ("Hotel", "Htl", AprsSymbolGroup.Other),
        ['I'] = ("TCP/IP station", "IP", AprsSymbolGroup.Infrastructure),
        ['K'] = ("School", "Sch", AprsSymbolGroup.Facility),
        ['L'] = ("PC user", "PC", AprsSymbolGroup.Other),
        ['N'] = ("NTS station", "NTS", AprsSymbolGroup.Infrastructure),
        ['O'] = ("Balloon", "Bal", AprsSymbolGroup.Aircraft),
        ['P'] = ("Police", "Pol", AprsSymbolGroup.Emergency),
        ['R'] = ("RV", "RV", AprsSymbolGroup.Vehicle),
        ['S'] = ("Space shuttle", "Shu", AprsSymbolGroup.Aircraft),
        ['U'] = ("Bus", "Bus", AprsSymbolGroup.Vehicle),
        ['W'] = ("Weather service site", "NWS", AprsSymbolGroup.Weather),
        ['X'] = ("Helicopter", "Heli", AprsSymbolGroup.Aircraft),
        ['Y'] = ("Sailboat", "Yt", AprsSymbolGroup.Boat),
        ['['] = ("Person", "Ped", AprsSymbolGroup.Person),
        ['\\'] = ("DF triangle", "DF", AprsSymbolGroup.Other),
        [']'] = ("Mailbox", "Mail", AprsSymbolGroup.Other),
        ['^'] = ("Large aircraft", "Air", AprsSymbolGroup.Aircraft),
        ['_'] = ("Weather station", "Wx", AprsSymbolGroup.Weather),
        ['`'] = ("Dish antenna", "Dish", AprsSymbolGroup.Infrastructure),
        ['a'] = ("Ambulance", "Amb", AprsSymbolGroup.Emergency),
        ['b'] = ("Bicycle", "Bike", AprsSymbolGroup.Vehicle),
        ['c'] = ("Incident command post", "ICP", AprsSymbolGroup.Emergency),
        ['d'] = ("Fire department", "FD", AprsSymbolGroup.Facility),
        ['e'] = ("Horse", "Hrs", AprsSymbolGroup.Person),
        ['f'] = ("Fire truck", "FT", AprsSymbolGroup.Emergency),
        ['g'] = ("Glider", "Gli", AprsSymbolGroup.Aircraft),
        ['h'] = ("Hospital", "H", AprsSymbolGroup.Facility),
        ['j'] = ("Jeep", "4x4", AprsSymbolGroup.Vehicle),
        ['k'] = ("Truck", "Trk", AprsSymbolGroup.Vehicle),
        ['l'] = ("Laptop", "PC", AprsSymbolGroup.Other),
        ['m'] = ("Mic-E repeater", "Rpt", AprsSymbolGroup.Infrastructure),
        ['n'] = ("Node", "Nd", AprsSymbolGroup.Infrastructure),
        ['o'] = ("Emergency operations centre", "EOC", AprsSymbolGroup.Emergency),
        ['p'] = ("Dog", "Dog", AprsSymbolGroup.Person),
        ['r'] = ("Repeater", "Rpt", AprsSymbolGroup.Infrastructure),
        ['s'] = ("Power boat", "Boat", AprsSymbolGroup.Boat),
        ['t'] = ("Truck stop", "TS", AprsSymbolGroup.Other),
        ['u'] = ("Semi truck", "Semi", AprsSymbolGroup.Vehicle),
        ['v'] = ("Van", "Van", AprsSymbolGroup.Vehicle),
        ['w'] = ("Water station", "H2O", AprsSymbolGroup.Facility),
        ['y'] = ("House with Yagi antenna", "QTH", AprsSymbolGroup.Other)
    };

    private static readonly Dictionary<char, (string Name, string Glyph, AprsSymbolGroup Group)> Alternate = new()
    {
        ['!'] = ("EMERGENCY", "SOS", AprsSymbolGroup.Emergency),
        ['#'] = ("Digipeater", "Dg", AprsSymbolGroup.Infrastructure),
        ['&'] = ("Gateway (I-gate)", "IG", AprsSymbolGroup.Infrastructure),
        ['-'] = ("House (HF)", "QTH", AprsSymbolGroup.Other),
        ['>'] = ("Car", "Car", AprsSymbolGroup.Vehicle),
        ['_'] = ("Weather site", "Wx", AprsSymbolGroup.Weather),
        ['k'] = ("SUV / 4x4", "SUV", AprsSymbolGroup.Vehicle),
        ['u'] = ("Truck", "Trk", AprsSymbolGroup.Vehicle),
        ['v'] = ("Van", "Van", AprsSymbolGroup.Vehicle),
        ['^'] = ("Aircraft", "Air", AprsSymbolGroup.Aircraft),
        ['s'] = ("Ship", "Ship", AprsSymbolGroup.Boat)
    };

    private static (string Name, string Glyph, AprsSymbolGroup Group)? Find(char table, char code) =>
        table == '/' ? Primary.TryGetValue(code, out var p) ? p : null
        : Alternate.TryGetValue(code, out var a) ? a : null;

    /// <summary>"Car", "Ambulance", "Digipeater (overlay S)"…</summary>
    public static string Name(char table, char code)
    {
        var name = Find(table, code)?.Name ?? $"Symbol {table}{code}";
        return table is '/' or '\\' ? name : $"{name} (overlay {table})";
    }

    /// <summary>A short plain-text glyph for the map symbol.</summary>
    public static string Glyph(char table, char code)
    {
        if (Find(table, code) is { } s)
            return s.Glyph;
        if (table == '/' && code is >= '0' and <= '9')
            return code.ToString();
        return code.ToString();
    }

    public static AprsSymbolGroup Group(char table, char code) => Find(table, code)?.Group ?? AprsSymbolGroup.Other;

    /// <summary>The overlay character drawn on the symbol (alternate-table symbols with a letter or digit table).</summary>
    public static char? Overlay(char table) => table is '/' or '\\' ? null : table;
}
