using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// The Zelda 64 analogue of Hammer's entity I/O view. Lists every switch / chest / collectible flag
/// used by the placed actors, grouped by flag, showing which actors SET it (⇒, like an output) and
/// which READ it (⇐, like an input) — plus the scene's exits/warps. Flags that are only set or only
/// read are flagged in amber (a likely dangling connection), mirroring Hammer's I/O validation.
/// Double-click any actor/exit to select + centre on it. Modeless.
/// </summary>
public sealed class FlagConnectionsDialog : Form
{
    private static readonly Color BgDark = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);
    private static readonly Color HdrFg = Color.FromArgb(140, 190, 255);
    private static readonly Color Warn = Color.FromArgb(230, 200, 120);

    private readonly MapDocument _doc;
    private readonly bool _isOoT;
    private readonly TreeView _tree = new();

    public event Action<object>? GoToRequested;

    public FlagConnectionsDialog(MapDocument doc, bool isOoT)
    {
        _doc = doc;
        _isOoT = isOoT;

        Text = "Logic — Flag Connections";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(540, 520);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(new Label
        {
            Text = "Switch / chest / collectible flags shared between actors (⇒ sets · ⇐ reads), and exits. " +
                   "Amber = only set or only read (likely dangling).",
            Left = 12, Top = 8, Width = 516, Height = 32, ForeColor = FgNormal,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        });

        _tree.SetBounds(12, 44, 516, 430);
        _tree.BackColor = BgInput; _tree.ForeColor = FgNormal; _tree.BorderStyle = BorderStyle.FixedSingle;
        _tree.Font = new Font("Consolas", 9f);
        _tree.HideSelection = false;
        _tree.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
        _tree.DrawNode += (_, e) => { e.DrawDefault = true; };
        // Double-click an actor/exit → go to it; double-click a flag/channel node → name it.
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node.Tag is FlagRef fr) RenameChannel(fr);
            else if (e.Node.Tag != null) GoToRequested?.Invoke(e.Node.Tag);
        };
        Controls.Add(_tree);

        var refresh = new Button
        {
            Text = "Refresh", Left = 12, Top = 484, Width = 90, Height = 26,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        refresh.Click += (_, _) => Rebuild();
        var nameBtn = new Button
        {
            Text = "Name channel…", Left = 108, Top = 484, Width = 120, Height = 26,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        nameBtn.Click += (_, _) => { if (_tree.SelectedNode?.Tag is FlagRef fr) RenameChannel(fr); };
        Controls.Add(nameBtn);
        var close = new Button
        {
            Text = "Close", Left = 438, Top = 484, Width = 90, Height = 26,
            BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        close.Click += (_, _) => Close();
        Controls.AddRange([refresh, close]);

        Rebuild();
    }

    public void Rebuild()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        var groups = FlagConnectionAnalyzer.Analyze(_doc, _isOoT);
        foreach (ActorParamSchema.FlagKind kind in new[]
                 { ActorParamSchema.FlagKind.Switch, ActorParamSchema.FlagKind.Chest, ActorParamSchema.FlagKind.Collectible,
                   ActorParamSchema.FlagKind.Clear, ActorParamSchema.FlagKind.Event, ActorParamSchema.FlagKind.GoldSkulltula,
                   ActorParamSchema.FlagKind.Scene })
        {
            var inKind = groups.Where(g => g.Kind == kind).ToList();
            if (inKind.Count == 0) continue;
            var head = new TreeNode($"{KindName(kind)} FLAGS  ({inKind.Count})") { ForeColor = HdrFg };
            foreach (var g in inKind)
            {
                // Room-clear's setter is the engine (last enemy in the room dies), so reader-only is NOT
                // dangling there. For every other kind, a one-sided flag is a wiring warning (Hammer-style).
                bool dangling = kind != ActorParamSchema.FlagKind.Clear && (g.HasSetter ^ g.HasReader);
                string? cname = _doc.FlagName(g.Kind, g.Index);
                string nameSuffix = cname != null ? $"  “{cname}”" : "";
                string label = kind == ActorParamSchema.FlagKind.Clear
                    ? $"Room {g.Index}{nameSuffix}  (opens on all enemies defeated)"
                    : $"Flag {g.Index}{nameSuffix}{(dangling ? "   ⚠ " + (g.HasSetter ? "set, never read" : "read, never set") : "")}";
                // Tag the channel node so double-click / Name… renames it (vs. actor nodes which go-to).
                var fn = new TreeNode(label) { ForeColor = dangling ? Warn : FgNormal, Tag = new FlagRef(g.Kind, g.Index) };
                foreach (var u in g.Users)
                {
                    string arrow = u.Role switch
                    {
                        ActorParamSchema.FlagRole.Setter => "⇒ set ",
                        ActorParamSchema.FlagRole.Reader => "⇐ read",
                        ActorParamSchema.FlagRole.Both => "⇄ both",
                        _ => "·     ",
                    };
                    fn.Nodes.Add(new TreeNode(
                        $"{arrow} · {ActorName(u.Actor)}  ({u.Actor.XPos:0}, {u.Actor.YPos:0}, {u.Actor.ZPos:0})")
                    { Tag = u.Actor });
                }
                head.Nodes.Add(fn);
            }
            _tree.Nodes.Add(head);
        }

        var exits = FlagConnectionAnalyzer.Exits(_doc.Solids, _doc.AllActors, _isOoT);
        if (exits.Count > 0)
        {
            var head = new TreeNode($"EXITS / WARPS  ({exits.Count})") { ForeColor = HdrFg };
            foreach (var x in exits)
                head.Nodes.Add(new TreeNode(x.Description) { Tag = (object?)x.Actor ?? x.Trigger });
            _tree.Nodes.Add(head);
        }

        if (_tree.Nodes.Count == 0)
            _tree.Nodes.Add(new TreeNode("(no flag-bearing actors or exits placed yet)") { ForeColor = Warn });

        _tree.ExpandAll();
        if (_tree.Nodes.Count > 0) _tree.TopNode = _tree.Nodes[0];
        _tree.EndUpdate();
    }

    private string ActorName(ZActor a) => ActorParamSchema.For(_isOoT, a.Number)?.Title ?? a.DisplayName;

    private sealed record FlagRef(ActorParamSchema.FlagKind Kind, int Index);

    // Give a flag channel a friendly editor-only name (Hammer targetname). Editor-only; not compiled.
    private void RenameChannel(FlagRef fr)
    {
        string? cur = _doc.FlagName(fr.Kind, fr.Index);
        string? name = PromptText($"Name for {KindName(fr.Kind)} {fr.Index}", cur ?? "");
        if (name == null) return;   // cancelled
        _doc.SetFlagName(fr.Kind, fr.Index, name);
        Rebuild();
    }

    // Minimal modal text prompt (returns null on cancel). A blank result clears the name.
    private string? PromptText(string title, string initial)
    {
        using var dlg = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(320, 96), MinimizeBox = false, MaximizeBox = false, BackColor = BgDark, ForeColor = FgNormal,
        };
        var box = new TextBox { Text = initial, Left = 12, Top = 16, Width = 296, BackColor = BgInput, ForeColor = FgNormal, BorderStyle = BorderStyle.FixedSingle };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 152, Top = 56, Width = 70, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 238, Top = 56, Width = 70, BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat };
        dlg.Controls.AddRange([box, ok, cancel]);
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    private static string KindName(ActorParamSchema.FlagKind k) => k switch
    {
        ActorParamSchema.FlagKind.Switch => "SWITCH",
        ActorParamSchema.FlagKind.Chest => "CHEST",
        ActorParamSchema.FlagKind.Collectible => "COLLECTIBLE",
        ActorParamSchema.FlagKind.Clear => "ROOM-CLEAR",
        ActorParamSchema.FlagKind.Event => "EVENT / STORY",
        ActorParamSchema.FlagKind.GoldSkulltula => "GOLD SKULLTULA",
        _ => "SCENE",
    };
}
