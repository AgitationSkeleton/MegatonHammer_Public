using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// MM NPC schedule editor. Each row is a rule "on Day D, between Start and End, the NPC is at (X,Y,Z)
/// facing Yaw". The 2Ship fork applies these by overriding the actor's position/facing by the in-game
/// clock (see the mh/schedules convention). Rows with overlapping windows resolve to the first match.
/// </summary>
public sealed class ScheduleDialog : Form
{
    private static readonly Color Bg = Color.FromArgb(37, 37, 38);
    private static readonly Color BgIn = Color.FromArgb(30, 30, 30);
    private static readonly Color Fg = Color.FromArgb(220, 220, 220);

    private readonly ZActor _actor;
    private readonly DataGridView _grid = new();

    public ScheduleDialog(ZActor actor)
    {
        _actor = actor;
        Text = $"NPC Schedule — {_actor.DisplayName}";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(640, 360);
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Segoe UI", 8.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(new Label
        {
            Text = "Rules apply top-to-bottom; the first whose Day (0 = any) and time window contain the " +
                   "current clock wins. Position/facing override the NPC while that rule is active.",
            Left = 10, Top = 8, Width = 620, Height = 30, ForeColor = Fg,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        });

        _grid.SetBounds(10, 42, 620, 268);
        _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _grid.BackgroundColor = BgIn; _grid.ForeColor = Fg; _grid.GridColor = Color.FromArgb(70, 70, 74);
        _grid.BorderStyle = BorderStyle.FixedSingle; _grid.AllowUserToResizeRows = false;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(50, 50, 54);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Fg;
        _grid.DefaultCellStyle.BackColor = BgIn; _grid.DefaultCellStyle.ForeColor = Fg;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 90, 158);
        _grid.RowHeadersVisible = false;
        AddCol("Day", "Day (0=any)"); AddCol("SH", "Start h"); AddCol("SM", "Start m");
        AddCol("EH", "End h"); AddCol("EM", "End m"); AddCol("X", "X"); AddCol("Y", "Y"); AddCol("Z", "Z"); AddCol("Yaw", "Yaw");
        Controls.Add(_grid);

        foreach (var r in _actor.Schedule ?? [])
            _grid.Rows.Add(r.Day, r.StartHour, r.StartMin, r.EndHour, r.EndMin,
                           (int)MathF.Round(r.X), (int)MathF.Round(r.Y), (int)MathF.Round(r.Z), r.Yaw);

        var addBtn  = Btn("+ Rule", 10);
        var hereBtn = Btn("Set pos = current", 78);
        var delBtn  = Btn("Remove", 196);
        var ok      = Btn("OK", 470); ok.DialogResult = DialogResult.OK; ok.BackColor = Color.FromArgb(0, 122, 204); ok.ForeColor = Color.White;
        var cancel  = Btn("Cancel", 552); cancel.DialogResult = DialogResult.Cancel;
        foreach (var b in new[] { addBtn, hereBtn, delBtn, ok, cancel }) { b.Top = 322; b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; Controls.Add(b); }
        ok.Anchor = cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

        addBtn.Click  += (_, _) => _grid.Rows.Add((byte)0, (byte)0, (byte)0, (byte)23, (byte)59,
                                                  (int)MathF.Round(_actor.XPos), (int)MathF.Round(_actor.YPos), (int)MathF.Round(_actor.ZPos), _actor.YRot);
        hereBtn.Click += (_, _) => { if (_grid.CurrentRow is { IsNewRow: false } row) { row.Cells[5].Value = (int)MathF.Round(_actor.XPos); row.Cells[6].Value = (int)MathF.Round(_actor.YPos); row.Cells[7].Value = (int)MathF.Round(_actor.ZPos); row.Cells[8].Value = (int)_actor.YRot; } };
        delBtn.Click  += (_, _) => { if (_grid.CurrentRow is { IsNewRow: false } row) _grid.Rows.Remove(row); };
        AcceptButton = ok; CancelButton = cancel;
        FormClosing += (_, e) => { if (DialogResult == DialogResult.OK) Commit(); };
    }

    private void Commit()
    {
        var rules = new List<ScheduleRule>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            int V(int c) => int.TryParse(row.Cells[c].Value?.ToString(), out int x) ? x : 0;
            rules.Add(new ScheduleRule
            {
                Day = (byte)Math.Clamp(V(0), 0, 3),
                StartHour = (byte)Math.Clamp(V(1), 0, 23), StartMin = (byte)Math.Clamp(V(2), 0, 59),
                EndHour = (byte)Math.Clamp(V(3), 0, 23), EndMin = (byte)Math.Clamp(V(4), 0, 59),
                X = V(5), Y = V(6), Z = V(7), Yaw = (short)V(8),
            });
        }
        _actor.Schedule = rules.Count > 0 ? rules : null;
    }

    private void AddCol(string name, string header)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = name is "X" or "Y" or "Z" or "Yaw" ? 64 : 56 });
    }

    private static Button Btn(string t, int left) => new()
    { Text = t, Left = left, Width = t.Length > 10 ? 110 : (t.Length > 6 ? 100 : 62), Height = 26,
      BackColor = Color.FromArgb(60, 60, 63), ForeColor = Fg, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8f) };
}
