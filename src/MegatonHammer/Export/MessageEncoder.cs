using System.Collections.Generic;

namespace MegatonHammer.Export;

/// <summary>
/// Lowers the editor's engine-agnostic dialogue markup (see <see cref="Editor.MhMessage"/>) to a game's
/// in-game message control bytes. Markup: <c>&amp;</c> = newline, <c>^</c> = new box/page,
/// <c>%r %g %b %y %w %p</c> = colour; printable ASCII passes through (the OoT/MM charmaps map ASCII to
/// itself). Produces the message BODY (control codes + terminator); the table-append layer adds any
/// header. OoT and MM use different control maps (OoT END 0x02, colour 0x05+arg; MM END 0xBF, bare
/// colour bytes, newline 0x11, box 0x10). See docs/dialogue-authoring-plan.md.
/// </summary>
public static class MessageEncoder
{
    // OoT colour arg after the 0x05 control byte (message_data_fmt.h): 0 default,1 red,3 blue,5 purple,
    // 6 yellow. OoT has no green → fall back to default.
    private static readonly Dictionary<char, byte> OotColor = new()
    { ['w'] = 0, ['r'] = 1, ['b'] = 3, ['p'] = 5, ['y'] = 6, ['g'] = 0 };

    // MM bare colour byte: 0 white/default,1 red,2 green,3 blue,4 yellow,6 pink(≈purple).
    private static readonly Dictionary<char, byte> MmColor = new()
    { ['w'] = 0, ['r'] = 1, ['g'] = 2, ['b'] = 3, ['y'] = 4, ['p'] = 6 };

    /// <summary>Encode <paramref name="text"/> to an OoT message body (terminated with END 0x02).</summary>
    public static byte[] EncodeOoT(string text)
    {
        var o = new List<byte>(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '&': o.Add(0x01); break;                       // newline
                case '^': o.Add(0x04); break;                       // new box / page break
                case '%' when i + 1 < text.Length:
                    o.Add(0x05); o.Add(OotColor.GetValueOrDefault(text[++i], (byte)0)); break;
                default:
                    if (c >= 0x20 && c < 0x7F) o.Add((byte)c);      // printable ASCII (charmap identity)
                    break;
            }
        }
        o.Add(0x02);   // END
        return o.ToArray();
    }

    /// <summary>Encode <paramref name="text"/> to an MM message body (terminated with 0xBF). The textbox
    /// header (type/pos/icon/nextId) is prepended separately by the table-append layer.</summary>
    public static byte[] EncodeMM(string text)
    {
        var o = new List<byte>(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '&': o.Add(0x11); break;                       // newline
                case '^': o.Add(0x10); break;                       // new box / page break
                case '%' when i + 1 < text.Length:
                    o.Add(MmColor.GetValueOrDefault(text[++i], (byte)0)); break;   // bare colour byte
                default:
                    if (c >= 0x20 && c < 0x7F) o.Add((byte)c);
                    break;
            }
        }
        o.Add(0xBF);   // END
        return o.ToArray();
    }

    public static byte[] Encode(string text, bool mm) => mm ? EncodeMM(text) : EncodeOoT(text);
}
