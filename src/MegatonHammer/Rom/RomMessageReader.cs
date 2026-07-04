using System.Collections.Generic;
using System.Text;
using MegatonHammer.Editor;

namespace MegatonHammer.Rom;

/// <summary>
/// Reads and decodes OoT dialogue from a ROM: locates the message table (<see cref="MessageTableLocator"/>),
/// finds the message-data file, and decodes a message body back into the editor's engine-agnostic markup
/// (see <see cref="MessageEncoder"/> for the inverse). Used so imported actors show their actual dialogue in
/// the entity dialog and can be edited. OoT (NES/English charmap) only for now; the control-code arg lengths
/// are taken verbatim from message_data_fmt.h.
/// </summary>
public sealed class RomMessageReader
{
    private readonly byte[] _data;                                   // the message-data file bytes
    private readonly Dictionary<ushort, (byte opts, uint off)> _index = new();

    private RomMessageReader(byte[] data, MessageTableLocator.Result loc)
    {
        _data = data;
        foreach (var e in loc.Entries) _index[e.TextId] = (e.Opts, e.Offset);
    }

    /// <summary>Builds a reader for an OoT ROM, or null if the message table/data can't be located.</summary>
    public static RomMessageReader? Build(RomImage rom)
    {
        if (rom.Game != RomGame.OoT) return null;   // MM message format differs — not handled yet
        // The table lives in the code file; scan each file for it (as SceneImporter does for the scene table).
        MessageTableLocator.Result loc = default; bool found = false;
        foreach (var f in rom.Files)
        {
            if (!f.Exists || f.Size < MessageTableLocator.EntrySize * 200) continue;
            var r = MessageTableLocator.Find(rom.GetFile(f.Index));
            if (r.Found) { loc = r; found = true; break; }
        }
        if (!found) return null;

        // The data file holds the message bodies at the table's offsets: pick the file whose size matches the
        // largest offset and whose first few entries start with printable text.
        var e = loc.Entries;
        uint span = e[^1].Offset;
        foreach (var f in rom.Files)
        {
            if (!f.Exists || f.Size < (int)span || f.Size > (int)span + 0x4000) continue;
            byte[] fb = rom.GetFile(f.Index);
            bool ok = true;
            for (int k = 1; k <= 3 && ok; k++)
            {
                int o = (int)e[k].Offset, printable = 0;
                for (int b = 0; b < 16 && o + b < fb.Length; b++)
                    if (fb[o + b] >= 0x20 && fb[o + b] < 0x7F) printable++;
                if (printable < 6) ok = false;
            }
            if (ok) return new RomMessageReader(fb, loc);
        }
        return null;
    }

    /// <summary>True if the ROM has a message with this text id.</summary>
    public bool Has(int textId) => _index.ContainsKey((ushort)textId);

    /// <summary>Decodes message <paramref name="textId"/> into an editor <see cref="MhMessage"/>, or null
    /// if absent. Box type / y-position come from the entry's option byte; an item-icon control code sets Icon.</summary>
    public MhMessage? Read(int textId)
    {
        if (!_index.TryGetValue((ushort)textId, out var ent)) return null;
        var msg = new MhMessage { Id = textId, BoxType = (ent.opts >> 4) & 0xF, YPos = ent.opts & 0xF, Text = Decode((int)ent.off, out int icon) };
        if (icon >= 0) msg.Icon = icon;
        return msg;
    }

    // OoT NES control-code arg lengths (message_data_fmt.h): the number of bytes that follow each control
    // byte. 0 = no argument; entries not listed are printable (>= 0x20) or the 0-arg default.
    private static readonly Dictionary<byte, int> ArgLen = new()
    {
        [0x05] = 1, [0x06] = 1, [0x07] = 2, [0x0C] = 1, [0x0E] = 1, [0x11] = 2,
        [0x12] = 2, [0x13] = 1, [0x14] = 1, [0x15] = 3, [0x1E] = 1,
    };
    // COLOR arg (0x05) -> editor colour markup letter (message_data_fmt.h CTRL_* colours).
    private static readonly char[] Color = { 'w', 'r', 'w', 'b', 'b', 'p', 'y', 'w' };

    private string Decode(int off, out int icon)
    {
        icon = -1;
        var sb = new StringBuilder(64);
        int p = off;
        for (int guard = 0; guard < 4096 && p < _data.Length; guard++)
        {
            byte c = _data[p++];
            if (c == 0x02) break;                                   // END
            if (c >= 0x20 && c < 0x7F) { sb.Append((char)c); continue; }   // printable ASCII (charmap identity)
            switch (c)
            {
                case 0x01: sb.Append('&'); break;                   // NEWLINE
                case 0x04: sb.Append('^'); break;                   // BOX_BREAK (new page)
                case 0x05:                                          // COLOR + 1 arg
                    if (p < _data.Length) { byte a = _data[p++]; sb.Append('%').Append(Color[a & 7]); }
                    break;
                case 0x13:                                          // ITEM_ICON + 1 arg (capture the icon)
                    if (p < _data.Length) { icon = _data[p++]; }
                    break;
                default:
                    p += ArgLen.GetValueOrDefault(c, 0);            // skip other control codes + their args
                    break;
            }
        }
        return sb.ToString();
    }
}
