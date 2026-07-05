using System.Drawing;
using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// Pop-out brush inspector opened by double-clicking a brush or right-click ▸ Properties. It hosts a full
/// <see cref="PropertiesPanel"/> (the same control docked bottom-right), so it shows EVERY brush setting —
/// warp trigger + destination, floor property, floor hazard (fire/damage), wall type, footstep material,
/// hookshot/soft/horse, conveyor, water box, and the raw surface words — not just the warp fields. Modeless,
/// edits apply live, and it reopens where you left it (like the actor config window).
/// </summary>
public sealed class BrushPropertiesDialog : Form
{
    private static readonly Color BgDark = Color.FromArgb(37, 37, 38);
    private readonly PropertiesPanel _panel;

    public BrushPropertiesDialog(MapDocument doc, ActorDatabase actorDb, bool nativeIsOoT)
    {
        Text = "Brush Properties";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(340, 560);
        MinimumSize = new Size(300, 320);
        BackColor = BgDark; ForeColor = Color.FromArgb(210, 210, 210);
        ShowInTaskbar = false;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // Embed the same inspector control the docked panel uses — no duplicated UI. It binds to the document
        // selection, so with the clicked brush selected it shows that brush's full property set.
        _panel = new PropertiesPanel(doc, actorDb, nativeIsOoT, showHeader: false) { Dock = DockStyle.Fill };
        Controls.Add(_panel);
    }

    // Remembered across open/close for the session, like the actor config + shade-paint pop-outs.
    private static Point? _lastLoc;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_lastLoc is { } l && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(l, Size))))
            Location = l;
        else
        {
            var root = Owner; while (root?.Owner != null) root = root.Owner;
            Location = root != null
                ? new Point(root.Right - Width - 40, root.Top + 80)
                : new Point(Math.Max(0, (Screen.PrimaryScreen!.WorkingArea.Width - Width) / 2), 120);
        }
        _panel.ForceRefresh();   // ensure it shows the current selection immediately
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (Visible && WindowState == FormWindowState.Normal) _lastLoc = Location;
    }
}
