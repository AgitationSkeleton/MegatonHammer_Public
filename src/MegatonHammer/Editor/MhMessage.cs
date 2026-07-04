using System.Linq;
using System.Text;

namespace MegatonHammer.Editor;

/// <summary>
/// One authored in-game text message in the project's <b>Message Bank</b>. An actor's Message field
/// references it by the in-game <see cref="Id"/> (textId); the field writes the params bits via its
/// TextIdBase. The per-target encoder lowers <see cref="Text"/> to engine bytes at export (OoT: END
/// 0x02, colour 0x05+arg; MM: END 0xBF, header-in-body, bare colour bytes). See docs/dialogue-authoring-plan.md.
/// </summary>
/// <summary>What a dialogue box does: just show text, or present a two-choice prompt.</summary>
public enum MhMsgKind { Message, Prompt }

/// <summary>What happens after a message advances (Message: on OK; Prompt: per chosen option). Branching to
/// another box (<see cref="NextMsgId"/>) rides the vanilla goto-message control code and works on carts; the
/// rest (give item / charge rupees / set a flag) is NPC behaviour, so on unmodified carts it only applies to
/// actors that already do it — on the SoH/2Ship playtest forks a generic dialogue interpreter honours all of
/// it (emitted in the mh/messages resource). See docs/dialogue-authoring-plan.md.</summary>
public sealed class MhOutcome
{
    public int  NextMsgId   { get; set; } = -1;   // "Display MsgBox #" — an in-game textId, or -1 = close
    public int  FireFlag    { get; set; } = -1;   // "Fire Trigger #" — a scene switch flag, or -1 = none
    public int  GiveItem    { get; set; } = -1;   // "Give Item" — a GetItem id, or -1 = none
    public bool ChargeRupees { get; set; }        // deduct rupees (a purchase) — needs GiveItem or a flag to be worthwhile
    public int  RupeeCost   { get; set; }         // amount charged when ChargeRupees (gated on affording it)

    public bool IsEmpty => NextMsgId < 0 && FireFlag < 0 && GiveItem < 0 && !ChargeRupees;
    public MhOutcome Clone() => (MhOutcome)MemberwiseClone();
}

public sealed class MhMessage
{
    /// <summary>The in-game textId this message provides (e.g. a sign's 0x0300 | index).</summary>
    public int Id { get; set; }

    /// <summary>Friendly, engine-agnostic markup: <c>&amp;</c> = newline, <c>^</c> = new box/page,
    /// <c>%r %g %b %y %w %p</c> = colour, <c>~N</c> = text speed (0 fastest). Lowered to control bytes at export.</summary>
    public string Text { get; set; } = "";

    public int BoxType { get; set; }       // textbox type (0 = default black box)
    public int YPos { get; set; }          // on-screen position (0 = top, 1 = middle, 2 = bottom)
    public int Icon { get; set; } = -1;    // item-icon id, or -1 / 0xFE = none

    /// <summary>Message = plain text; Prompt = a two-choice question (the vanilla CTRL_TWO_CHOICE).</summary>
    public MhMsgKind Kind { get; set; } = MhMsgKind.Message;

    /// <summary>Prompt option labels (the two selectable lines). Ignored for a plain Message.</summary>
    public string Choice1 { get; set; } = "Yes";
    public string Choice2 { get; set; } = "No";

    /// <summary>Outcome for a plain Message (on advance) or a Prompt's FIRST option.</summary>
    public MhOutcome Outcome1 { get; set; } = new();
    /// <summary>Outcome for a Prompt's SECOND option (unused for a plain Message).</summary>
    public MhOutcome Outcome2 { get; set; } = new();

    /// <summary>The "already fulfilled" state (a Zelda staple): once <see cref="DoneFlag"/> (a scene switch/event
    /// flag) is set, the NPC/sign shows <see cref="AfterMsgId"/> instead of this one. An outcome that completes
    /// the interaction should FireFlag = DoneFlag. -1 = no fulfilled state (always shows this message).</summary>
    public int DoneFlag  { get; set; } = -1;
    public int AfterMsgId { get; set; } = -1;   // textId shown once DoneFlag is set (-1 = none)

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
