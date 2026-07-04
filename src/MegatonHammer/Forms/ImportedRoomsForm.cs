using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// Modeless panel listing an imported level's rooms with show/hide checkboxes, so a
/// multi-room dungeon (Forest Temple, Jabu-Jabu, …) can be viewed one room at a time or
/// all together (D14). Toggling redraws the viewports via the supplied callback.
/// </summary>
public sealed class ImportedRoomsForm : Form
{
    private readonly ImportedLevel _level;
    private readonly Action _redraw;
    private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true,
        BackColor = Color.FromArgb(37, 37, 38), ForeColor = Color.FromArgb(220, 220, 220), BorderStyle = BorderStyle.None };

    public ImportedRoomsForm(ImportedLevel level, Action redraw)
    {
        _level = level;
        _redraw = redraw;

        Text = "Imported Rooms";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        Size = new Size(200, 320);
        BackColor = Color.FromArgb(45, 45, 48);
        ForeColor = Color.FromArgb(220, 220, 220);

        for (int i = 0; i < level.Scene.Rooms.Count; i++)
        {
            int tris = i < level.RoomMeshes.Count ? level.RoomMeshes[i].Count : 0;
            _list.Items.Add($"Room {i}  ({tris} tris)", level.RoomVisible[i]);
        }
        _list.ItemCheck += (_, e) =>
        {
            if (e.Index >= 0 && e.Index < _level.RoomVisible.Length)
            {
                _level.RoomVisible[e.Index] = e.NewValue == CheckState.Checked;
                BeginInvoke(_redraw);     // after the check state settles
            }
        };

        var all = new Button { Text = "All", Dock = DockStyle.Bottom, Height = 26 };
        all.Click += (_, _) => SetAll(true);
        var none = new Button { Text = "None", Dock = DockStyle.Bottom, Height = 26 };
        none.Click += (_, _) => SetAll(false);

        Controls.Add(_list);
        Controls.Add(none);
        Controls.Add(all);
    }

    private void SetAll(bool visible)
    {
        for (int i = 0; i < _list.Items.Count; i++)
        {
            _list.SetItemChecked(i, visible);
            if (i < _level.RoomVisible.Length) _level.RoomVisible[i] = visible;
        }
        _redraw();
    }
}
