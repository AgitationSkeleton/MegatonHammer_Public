namespace MegatonHammer.Forms;

/// <summary>
/// One titled, collapsible section in a <see cref="DockStack"/> — a Hammer-style stacked panel.
/// The header bar (title + ▼/► chevron) toggles the content's visibility when clicked; the parent
/// DockStack re-lays-out the stack in response to <see cref="CollapseToggled"/>.
/// </summary>
public sealed class CollapsibleSection : Panel
{
    public const int HeaderHeight = 24;

    private static readonly Color HeaderBg = Color.FromArgb(50, 50, 53);
    private static readonly Color HeaderFg = Color.FromArgb(215, 215, 215);
    private static readonly Color BodyBg   = Color.FromArgb(30, 30, 30);

    private readonly Label _title;
    private readonly Panel _content;
    private readonly string _titleText;
    private bool _collapsed;

    public event Action? CollapseToggled;

    public bool IsCollapsed => _collapsed;

    public CollapsibleSection(string title, Control child)
    {
        _titleText = title;
        BackColor  = BodyBg;

        _content = new Panel { Dock = DockStyle.Fill, BackColor = BodyBg };
        child.Dock = DockStyle.Fill;
        _content.Controls.Add(child);

        _title = new Label
        {
            Dock = DockStyle.Fill, ForeColor = HeaderFg, BackColor = HeaderBg,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand,
        };
        var header = new Panel { Dock = DockStyle.Top, Height = HeaderHeight, BackColor = HeaderBg, Cursor = Cursors.Hand };
        header.Controls.Add(_title);

        Controls.Add(_content);   // index 0 — fills
        Controls.Add(header);     // index 1 — top
        _title.Click  += (_, _) => Toggle();
        header.Click  += (_, _) => Toggle();
        UpdateTitle();
    }

    public void Toggle() => SetCollapsed(!_collapsed);

    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;
        _content.Visible = !collapsed;
        UpdateTitle();
        CollapseToggled?.Invoke();
    }

    private void UpdateTitle() => _title.Text = (_collapsed ? "  ►  " : "  ▼  ") + _titleText;
}
