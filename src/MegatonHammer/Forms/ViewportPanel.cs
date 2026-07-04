namespace MegatonHammer.Forms;

public sealed class ViewportPanel : UserControl
{
    public GLViewport Viewport { get; }

    private static readonly Color HeaderBg     = Color.FromArgb(45, 45, 48);
    private static readonly Color ActiveBorder = Color.FromArgb(0, 122, 204);
    private static readonly Color IdleBorder   = Color.FromArgb(70, 70, 75);

    private readonly Label _headerLabel;
    private bool _active;

    /// <summary>Raised when the header bar is double-clicked (Hammer: maximize/restore this viewport).</summary>
    public event Action<ViewportPanel>? HeaderDoubleClicked;

    public ViewportPanel(ViewportType type)
    {
        Dock     = DockStyle.Fill;
        Viewport = new GLViewport(type);

        _headerLabel = new Label
        {
            Dock      = DockStyle.Top,
            Height    = 22,
            BackColor = HeaderBg,
            ForeColor = Color.FromArgb(200, 200, 200),
            Font      = new Font("Segoe UI", 8.5f),
            Text      = $"  {LabelFor(type)}",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(4, 0, 0, 0)
        };

        Controls.Add(Viewport);
        Controls.Add(_headerLabel);

        // Hammer: double-clicking a viewport's title bar maximizes it (and again restores the 4-pane grid).
        _headerLabel.DoubleClick += (_, _) => HeaderDoubleClicked?.Invoke(this);
        _headerLabel.Cursor = Cursors.Hand;

        Viewport.GotFocus  += (_, _) => SetActive(true);
        Viewport.LostFocus += (_, _) => SetActive(false);

        BackColor = IdleBorder;
        Padding   = new Padding(1);
    }

    private void SetActive(bool active)
    {
        _active   = active;
        BackColor = active ? ActiveBorder : IdleBorder;
        _headerLabel.BackColor = active
            ? Color.FromArgb(0, 80, 140)
            : HeaderBg;
    }

    private static string LabelFor(ViewportType t) => t switch
    {
        ViewportType.Perspective3D => "3D View  [RMB look | WASD fly | MMB pan]",
        ViewportType.Top           => "Top  (X/Z)  [Scroll zoom | MMB pan]",
        ViewportType.Front         => "Front  (X/Y)  [Scroll zoom | MMB pan]",
        ViewportType.Side          => "Side  (Z/Y)  [Scroll zoom | MMB pan]",
        _                          => ""
    };
}
