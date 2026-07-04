using System.Linq;
using System.Text;

namespace MegatonHammer.Editor;

/// <summary>
/// One authored in-game text message in the project's <b>Message Bank</b>. An actor's Message field
/// references it by the in-game <see cref="Id"/> (textId); the field writes the params bits via its
/// TextIdBase. The per-target encoder lowers <see cref="Text"/> to engine bytes at export (OoT: END
/// 0x02, colour 0x05+arg; MM: END 0xBF, header-in-body, bare colour bytes). See docs/dialogue-authoring-plan.md.
/// </summary>
public sealed class MhMessage
{
    /// <summary>The in-game textId this message provides (e.g. a sign's 0x0300 | index).</summary>
    public int Id { get; set; }

    /// <summary>Friendly, engine-agnostic markup: <c>&amp;</c> = newline, <c>^</c> = new box/page,
    /// <c>%r %g %b %y %w %p</c> = colour. Lowered to control bytes per target at export.</summary>
    public string Text { get; set; } = "";

    public int BoxType { get; set; }       // textbox type (0 = default black box)
    public int YPos { get; set; }          // on-screen position (0 = top, 1 = middle, 2 = bottom)
    public int Icon { get; set; } = -1;    // item-icon id, or -1 / 0xFE = none

    public MhMessage() { }
    public MhMessage(int id, string text) { Id = id; Text = text; }

    /// <summary>Short single-line preview for pickers: markup stripped, clamped to ~40 chars.</summary>
    public string Preview()
    {
        var s = Text.Replace("&", " ").Replace("^", " / ");
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 1 < s.Length) { i++; continue; }   // skip %x colour codes
            sb.Append(s[i]);
        }
        var p = sb.ToString().Trim();
        return p.Length > 40 ? p[..40] + "…" : p;
    }
}
