using System.Xml.Linq;

namespace MegatonHammer.Editor;

/// <summary>Background-music sequence id → name (from SharpOcarina's SongNames.xml).</summary>
public static class SequenceNames
{
    public static List<(byte Id, string Name)> Load(bool isOoT)
    {
        var list = new List<(byte, string)>();
        string game = isOoT ? "OOT" : "MM";
        string path = $@"D:\Copilot_OOT\READ_ONLY_SourceCodes\SharpOcarina-main\XML\{game}\SongNames.xml";
        if (!File.Exists(path)) return list;

        try
        {
            foreach (var el in XDocument.Load(path).Root!.Elements("Song"))
            {
                byte id = Convert.ToByte((string)el.Attribute("Key")!, 16);
                list.Add((id, el.Value.Trim()));
            }
        }
        catch { /* fall back to empty */ }
        return list;
    }
}
