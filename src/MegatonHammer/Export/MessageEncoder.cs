using System.Collections.Generic;
using MegatonHammer.Editor;

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

    // Append the control bytes for one run of friendly markup (NO terminator). Shared by both back-ends.
    private static void AppendText(string text, bool mm, List<byte> o)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '&': o.Add(mm ? (byte)0x11 : (byte)0x01); break;   // newline
                case '^': o.Add(mm ? (byte)0x10 : (byte)0x04); break;   // new box / page break
                case '%' when i + 1 < text.Length:                      // colour
                    if (mm) o.Add(MmColor.GetValueOrDefault(text[++i], (byte)0));
                    else { o.Add(0x05); o.Add(OotColor.GetValueOrDefault(text[++i], (byte)0)); }
                    break;
                case '~' when i + 1 < text.Length && char.IsDigit(text[i + 1]):   // text speed (~0 fastest)
                    if (!mm) { o.Add(0x14); o.Add((byte)(text[++i] - '0')); }      // OoT CTRL_TEXT_SPEED + arg
                    else i++;   // MM text-speed byte not yet mapped -> drop the marker (renders at default speed)
                    break;
                default:
                    if (c >= 0x20 && c < 0x7F) o.Add((byte)c);          // printable ASCII (charmap identity)
                    break;
            }
        }
    }

    /// <summary>Encode <paramref name="text"/> to an OoT message body (terminated with END 0x02).</summary>
    public static byte[] EncodeOoT(string text) { var o = new List<byte>(text.Length + 8); AppendText(text, false, o); o.Add(0x02); return o.ToArray(); }

    /// <summary>Encode <paramref name="text"/> to an MM message body (terminated with 0xBF).</summary>
    public static byte[] EncodeMM(string text) { var o = new List<byte>(text.Length + 8); AppendText(text, true, o); o.Add(0xBF); return o.ToArray(); }

    public static byte[] Encode(string text, bool mm) => mm ? EncodeMM(text) : EncodeOoT(text);

    /// <summary>Encode a full <see cref="MhMessage"/> body. A Prompt appends the vanilla two-choice control
    /// code (OoT CTRL_TWO_CHOICE 0x1B) followed by the two option lines, so the in-game cursor selects between
    /// them. (MM's two-choice byte isn't mapped yet, so an MM prompt currently renders its choices as plain
    /// lines — the OUTCOME wiring is engine-side regardless; see docs/dialogue-authoring-plan.md.)</summary>
    public static byte[] Encode(MhMessage m, bool mm)
    {
        var o = new List<byte>(m.Text.Length + 16);
        AppendText(m.Text, mm, o);
        if (m.Kind == MhMsgKind.Prompt)
        {
            o.Add(mm ? (byte)0x11 : (byte)0x01);   // newline before the choices
            if (!mm) o.Add(0x1B);                  // OoT CTRL_TWO_CHOICE
            AppendText(m.Choice1, mm, o);
            o.Add(mm ? (byte)0x11 : (byte)0x01);
            AppendText(m.Choice2, mm, o);
        }
        o.Add(mm ? (byte)0xBF : (byte)0x02);       // END
        return o.ToArray();
    }
}
