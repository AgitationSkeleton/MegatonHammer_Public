using System.Globalization;
using System.Text.RegularExpressions;
using MegatonHammer.Editor;

namespace MegatonHammer.Forms;

/// <summary>
/// Quick pop-out for a brush's warp (exit-trigger) settings, opened by double-clicking a brush (or the 2D
/// right-click "Properties…"). Shows/edits where the warp goes: for OoT, a searchable destination picker
/// backed by the gEntranceTable names (type "Water Temple" to find it); otherwise a raw entrance-index field.
/// Modeless and live-applying, like the actor properties window.
/// </summary>
public sealed class WarpPropertiesDialog : Form
{
    private static readonly Color BgDark   = Color.FromArgb(37, 37, 38);
    private static readonly Color BgInput  = Color.FromArgb(30, 30, 30);
    private static readonly Color FgNormal = Color.FromArgb(215, 215, 215);
    private static readonly Color FgDim    = Color.FromArgb(150, 150, 150);
    private static readonly Color Accent   = Color.FromArgb(140, 190, 255);

    private readonly Solid _solid;
    private readonly bool  _nativeIsOoT;
    private readonly List<EntranceNames.Entry> _leaders;

    private readonly CheckBox _isTrigger;
    private readonly ComboBox _picker = null!;    // OoT: searchable entrance list
    private readonly TextBox  _rawBox = null!;    // fallback / raw hex
    private readonly Label    _resolved;
    private readonly Label    _destLabel;
    private bool _loading;

    /// <summary>Fired on any edit so the host can redraw viewports / refresh the docked panel.</summary>
    public event Action? Changed;

    public WarpPropertiesDialog(Solid solid, bool nativeIsOoT)
    {
        _solid       = solid;
        _nativeIsOoT = nativeIsOoT;
        _leaders     = nativeIsOoT && EntranceNames.Available ? EntranceNames.Leaders.ToList() : new();

        Text            = "Warp / Brush Properties";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = BgDark;
        ForeColor       = FgNormal;
        ClientSize      = new Size(400, 190);
        MinimumSize     = new Size(360, 210);
        ShowInTaskbar   = false;
        Font            = new Font("Segoe UI", 9f);

        var (mn, mx) = solid.GetAABB();
        var size = new Label
        {
            Left = 12, Top = 12, Width = 372, Height = 18, ForeColor = FgDim,
            Text = $"Brush  {(int)(mx.X - mn.X)} × {(int)(mx.Y - mn.Y)} × {(int)(mx.Z - mn.Z)}",
        };

        _isTrigger = new CheckBox
        {
            Left = 12, Top = 36, Width = 372, Height = 22, AutoSize = false,
            Text = "This brush is a warp trigger (exit volume)", Checked = solid.IsTrigger, ForeColor = FgNormal,
        };
        _isTrigger.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _solid.IsTrigger = _isTrigger.Checked;
            if (_solid.IsTrigger && _solid.ExitEntrance < 0) _solid.ExitEntrance = 0;
            UpdateEnabled();
            RefreshResolved();
            Changed?.Invoke();
        };

        _destLabel = new Label { Left = 12, Top = 68, Width = 120, Height = 22, Text = "Destination:", ForeColor = Accent, TextAlign = ContentAlignment.MiddleLeft };

        if (_leaders.Count > 0)
        {
            _picker = new ComboBox
            {
                Left = 12, Top = 90, Width = 372, DropDownStyle = ComboBoxStyle.DropDown,
                BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
                MaxDropDownItems = 18, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            foreach (var e in _leaders) _picker.Items.Add(e.ComboText);
            _picker.SelectedIndexChanged += (_, _) =>
            {
                if (_loading) return;
                int i = _picker.SelectedIndex;
                if (i >= 0 && i < _leaders.Count) CommitIndex(_leaders[i].Index);
            };
            // Free-typed text ("0x0011", a decimal, or a partial that didn't match a suggestion): parse on leave/Enter.
            _picker.Leave += (_, _) => CommitTyped(_picker.Text);
            _picker.KeyDown += (_, ke) => { if (ke.KeyCode == Keys.Enter) { CommitTyped(_picker.Text); ke.SuppressKeyPress = true; } };
        }
        else
        {
            _rawBox = new TextBox
            {
                Left = 12, Top = 90, Width = 372, BackColor = BgInput, ForeColor = FgNormal,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            _rawBox.TextChanged += (_, _) => { if (!_loading) CommitTyped(_rawBox.Text); };
        }

        _resolved = new Label
        {
            Left = 12, Top = 120, Width = 372, Height = 34, ForeColor = FgDim,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var close = new Button
        {
            Text = "Close", Width = 84, Height = 26, BackColor = BgInput, ForeColor = FgNormal, FlatStyle = FlatStyle.Flat,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Left = ClientSize.Width - 96, Top = ClientSize.Height - 34,
            DialogResult = DialogResult.OK,
        };
        close.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { size, _isTrigger, _destLabel, _resolved, close });
        Controls.Add(_leaders.Count > 0 ? _picker : _rawBox);
        AcceptButton = close;

        _loading = true;
        SetPickerToCurrent();
        _loading = false;
        UpdateEnabled();
        RefreshResolved();
    }

    private void CommitIndex(int index)
    {
        _solid.ExitEntrance = index & 0xFFFF;
        RefreshResolved();
        Changed?.Invoke();
    }

    private static readonly Regex HexTail = new(@"\[0x([0-9A-Fa-f]+)\]", RegexOptions.Compiled);

    // Parse whatever's in the field: a matched combo item ("… [0xNNNN]"), an explicit "0xNN"/decimal.
    private void CommitTyped(string text)
    {
        if (_loading) return;
        text = text.Trim();
        if (text.Length == 0) return;
        var m = HexTail.Match(text);
        int? val = null;
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int h)) val = h;
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hx)) val = hx;
        else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)) val = d;
        if (val is { } v && v != _solid.ExitEntrance) CommitIndex(v);
    }

    private void SetPickerToCurrent()
    {
        int cur = _solid.ExitEntrance < 0 ? 0 : _solid.ExitEntrance;
        if (_leaders.Count > 0)
        {
            int i = _leaders.FindIndex(e => e.Index == cur);
            if (i >= 0) _picker.SelectedIndex = i;
            else _picker.Text = $"0x{cur:X4}";   // a non-leader/variant index — show raw, still editable
        }
        else _rawBox.Text = $"0x{cur:X4}";
    }

    private void UpdateEnabled()
    {
        bool on = _solid.IsTrigger;
        _destLabel.Enabled = on;
        if (_leaders.Count > 0) _picker.Enabled = on; else _rawBox.Enabled = on;
        _resolved.Enabled = on;
    }

    private void RefreshResolved()
    {
        if (!_solid.IsTrigger) { _resolved.Text = "Not a warp — check the box above to make this brush warp the player."; return; }
        int cur = _solid.ExitEntrance < 0 ? 0 : _solid.ExitEntrance;
        _resolved.Text = _nativeIsOoT
            ? $"→ 0x{cur:X4}   {EntranceNames.Label(cur)}"
            : $"→ entrance 0x{cur:X4}  (MM: raw gEntranceTable index)";
    }
}
