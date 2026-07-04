using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// Hammer's "Check for Problems" for Zelda 64. Lists scene issues found by <see cref="SceneValidator"/>
/// (errors/warnings/info), colour-coded; double-click a problem with a target to select + centre on it.
/// </summary>
public sealed class SceneCheckDialog : Form
{
    private static readonly Color BgDark = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(210, 210, 210);

    private readonly MapDocument _doc;
    private readonly bool _isOoT;
    private readonly ListBox _list = new();
    private readonly List<SceneValidator.Problem> _problems = [];

    public event Action<object>? GoToRequested;

    public SceneCheckDialog(MapDocument doc, bool isOoT)
    {
        _doc = doc;
        _isOoT = isOoT;

        Text = "Check for Problems";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(560, 420);
        BackColor = BgDark; ForeColor = FgNormal;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _list.SetBounds(12, 12, 536, 360);
        _list.BackColor = BgInput; _list.ForeColor = FgNormal; _list.BorderStyle = BorderStyle.FixedSingle;
        _list.Font = new Font("Consolas", 9f);
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = 18;
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _list.DrawItem += OnDrawItem;
        _list.DoubleClick += (_, _) => GoTo();
        Controls.Add(_list);

        var recheck = new Button
        {
            Text = "Re-check", Left = 12, Top = 382, Width = 90, Height = 26,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        recheck.Click += (_, _) => Rebuild();
        var go = new Button
        {
            Text = "Go to", Left = 348, Top = 382, Width = 100, Height = 26,
            BackColor = Color.FromArgb(60, 60, 65), ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        go.Click += (_, _) => GoTo();
        var close = new Button
        {
            Text = "Close", Left = 458, Top = 382, Width = 90, Height = 26,
            BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        close.Click += (_, _) => Close();
        Controls.AddRange([recheck, go, close]);

        Rebuild();
    }

    public void Rebuild()
    {
        _problems.Clear();
        _problems.AddRange(SceneValidator.Check(_doc, _isOoT));
        _list.BeginUpdate();
        _list.Items.Clear();
        int err = _problems.Count(p => p.Level == SceneValidator.Severity.Error);
        int warn = _problems.Count(p => p.Level == SceneValidator.Severity.Warning);
        foreach (var p in _problems) _list.Items.Add(p.Message);
        _list.EndUpdate();
        Text = $"Check for Problems — {err} error(s), {warn} warning(s)";
    }

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _problems.Count) return;
        e.DrawBackground();
        var p = _problems[e.Index];
        var (tag, col) = p.Level switch
        {
            SceneValidator.Severity.Error => ("✖ ", Color.FromArgb(240, 120, 120)),
            SceneValidator.Severity.Warning => ("⚠ ", Color.FromArgb(230, 200, 120)),
            _ => ("· ", Color.FromArgb(160, 200, 160)),
        };
        TextRenderer.DrawText(e.Graphics, tag + p.Message, _list.Font, e.Bounds, col, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        e.DrawFocusRectangle();
    }

    private void GoTo()
    {
        int i = _list.SelectedIndex;
        if (i >= 0 && i < _problems.Count && _problems[i].Target is { } t) GoToRequested?.Invoke(t);
    }
}
