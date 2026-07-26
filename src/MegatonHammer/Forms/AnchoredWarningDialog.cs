using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// A one-time popup shown when the user selects a position-anchored (immovable) entity — a Mir_Ray light beam
/// or a position-forcing boss. It explains where the entity works, what it needs to function, and that it
/// can't be moved due to an OoT/MM engine limitation. A "Don't show this again" tick persists per actor id.
/// Shown at most once per actor id per session (and never once suppressed), so re-selecting doesn't nag.
/// </summary>
public sealed class AnchoredWarningDialog : Form
{
    private static readonly Color BgDark  = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);
    private static readonly Color Accent   = Color.FromArgb(0, 122, 204);
    private static readonly Color Warn      = Color.FromArgb(224, 178, 84);

    private static readonly HashSet<int> _shownThisSession = new();

    private readonly int _actorId;
    private readonly CheckBox _dontShow;

    private AnchoredWarningDialog(int actorId, string text)
    {
        _actorId = actorId;
        Text = "Entity placement note";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        BackColor = BgDark; ForeColor = FgNormal;
        Font = UiFonts.Get("Segoe UI", 9f);
        ClientSize = new Size(500, 400);

        var title = new Label
        {
            Text = "⚠  This entity is fixed in place", Left = 18, Top = 16, Width = 464, Height = 26,
            ForeColor = Warn, Font = UiFonts.Get("Segoe UI", 11f, FontStyle.Bold),
        };
        Controls.Add(title);

        var body = new Label
        {
            Text = text, Left = 20, Top = 50, Width = 460, AutoSize = false,
            ForeColor = FgNormal, Font = UiFonts.Get("Segoe UI", 9f), TextAlign = ContentAlignment.TopLeft,
        };
        body.Height = TextRenderer.MeasureText(text, body.Font, new Size(460, 0), TextFormatFlags.WordBreak).Height + 6;
        Controls.Add(body);

        int y = body.Bottom + 14;
        _dontShow = new CheckBox
        {
            Text = "Don't show this again for this entity", Left = 20, Top = y, Width = 320, Height = 22,
            ForeColor = FgNormal, FlatStyle = FlatStyle.Flat, BackColor = BgDark,
        };
        Controls.Add(_dontShow);

        var ok = new Button
        {
            Text = "Got it", Left = 396, Top = y - 4, Width = 84, Height = 30, BackColor = Accent,
            ForeColor = Color.White, FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.OK,
        };
        ok.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 85);
        Controls.Add(ok);
        AcceptButton = ok;

        // Grow the window to fit the (variable-height) body text.
        ClientSize = new Size(500, y + 44);
        FormClosed += (_, _) => { if (_dontShow.Checked) EditorSettings.SuppressAnchorWarning(_actorId); };
    }

    /// <summary>Show the warning for an anchored actor, unless the user suppressed it or it was already shown for
    /// this actor id this session. No-op when <paramref name="text"/> is null (actor isn't anchored).</summary>
    public static void MaybeShow(IWin32Window? owner, int actorId, string? text)
    {
        if (text == null) return;
        if (EditorSettings.IsAnchorWarningSuppressed(actorId)) return;
        if (!_shownThisSession.Add(actorId)) return;   // once per id per session
        using var dlg = new AnchoredWarningDialog(actorId, text);
        dlg.ShowDialog(owner);
    }
}
