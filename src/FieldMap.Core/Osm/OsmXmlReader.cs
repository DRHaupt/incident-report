using System.Globalization;
using System.Xml;

namespace FieldMap.Core.Osm;

/// <summary>Streaming reader for OpenStreetMap XML (.osm) files, used for the sample region and small test extracts.</summary>
public static class OsmXmlReader
{
    public static void Read(string path, IOsmHandler handler, CancellationToken cancel = default, IProgress<double>? progress = null)
    {
        using var stream = File.OpenRead(path);
        Read(stream, handler, cancel, progress);
    }

    public static void Read(Stream stream, IOsmHandler handler, CancellationToken cancel = default, IProgress<double>? progress = null)
    {
        var inv = CultureInfo.InvariantCulture;
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true, DtdProcessing = DtdProcessing.Ignore });
        var length = stream.CanSeek ? stream.Length : 0;
        var count = 0;
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element)
                continue;
            if (++count % 10000 == 0)
            {
                cancel.ThrowIfCancellationRequested();
                if (length > 0)
                    progress?.Report((double)stream.Position / length);
            }
            switch (xml.Name)
            {
                case "node":
                {
                    var id = long.Parse(xml.GetAttribute("id")!, inv);
                    var lat = double.Parse(xml.GetAttribute("lat")!, inv);
                    var lon = double.Parse(xml.GetAttribute("lon")!, inv);
                    var tags = new List<KeyValuePair<string, string>>();
                    ReadChildren(xml, (name, r) =>
                    {
                        if (name == "tag")
                            tags.Add(new(r.GetAttribute("k")!, r.GetAttribute("v")!));
                    });
                    if (handler.WantNodes)
                        handler.Node(id, lat, lon, tags.Count == 0 ? OsmTags.Empty : new OsmTags(tags.ToArray()));
                    break;
                }
                case "way":
                {
                    var id = long.Parse(xml.GetAttribute("id")!, inv);
                    var tags = new List<KeyValuePair<string, string>>();
                    var refs = new List<long>();
                    ReadChildren(xml, (name, r) =>
                    {
                        if (name == "tag")
                            tags.Add(new(r.GetAttribute("k")!, r.GetAttribute("v")!));
                        else if (name == "nd")
                            refs.Add(long.Parse(r.GetAttribute("ref")!, inv));
                    });
                    if (handler.WantWays)
                        handler.Way(id, refs, new OsmTags(tags.ToArray()));
                    break;
                }
                case "relation":
                {
                    var id = long.Parse(xml.GetAttribute("id")!, inv);
                    var tags = new List<KeyValuePair<string, string>>();
                    var members = new List<OsmMember>();
                    ReadChildren(xml, (name, r) =>
                    {
                        if (name == "tag")
                            tags.Add(new(r.GetAttribute("k")!, r.GetAttribute("v")!));
                        else if (name == "member")
                        {
                            var type = r.GetAttribute("type") switch { "node" => OsmMemberType.Node, "way" => OsmMemberType.Way, _ => OsmMemberType.Relation };
                            members.Add(new OsmMember(type, long.Parse(r.GetAttribute("ref")!, inv), r.GetAttribute("role") ?? ""));
                        }
                    });
                    if (handler.WantRelations)
                        handler.Relation(id, members, new OsmTags(tags.ToArray()));
                    break;
                }
            }
        }
        progress?.Report(1);
    }

    private static void ReadChildren(XmlReader xml, Action<string, XmlReader> onChild)
    {
        if (xml.IsEmptyElement)
            return;
        var depth = xml.Depth;
        while (xml.Read() && xml.Depth > depth)
            if (xml.NodeType == XmlNodeType.Element)
                onChild(xml.Name, xml);
    }
}
