using MegatonHammer.Editor;
using MegatonHammer.Textures;
using OpenTK.Mathematics;

namespace MegatonHammer.Forms;

/// <summary>
/// Valve Hammer "Face Edit Sheet" (Material page). Opens with the Texture/Material tool and stays
/// modeless while you click faces in the 3D view. Edits the selected face(s)' texture scale X/Y,
/// shift X/Y and rotation; previews the current face's texture with its name + size; Browse opens
/// the material browser and applies the pick, Replace swaps it everywhere; plus justify/fit, world
/// vs face mapping, treat-as-one, and align-to-adjacent. Operates on
/// <see cref="MapDocument.SelectedFaces"/>.
/// </summary>
public sealed class FaceEditDialog : Form
{
    private static readonly Color Bg     = Color.FromArgb(45, 45, 48);
    private static readonly Color BgIn   = Color.FromArgb(30, 30, 30);
    private static readonly Color BtnBg  = Color.FromArgb(60, 60, 63);
    private static readonly Color Fg     = Color.FromArgb(220, 220, 220);
    private static readonly Color HdrFg  = Color.FromArgb(150, 200, 255);

    private readonly MapDocument   _doc;
    private readonly TextureLibrary _lib;
    private readonly Action _redraw;
    private readonly Func<string?> _currentTexture;
    private readonly Action<string>? _setActiveTexture;   // make a browsed texture the global active one

    private readonly NumericUpDown _scaleS, _scaleT, _shiftS, _shiftT, _rotate;
    private readonly CheckBox _alignFace, _treatAsOne;
    private readonly Label _selLabel, _texLabel;
    private readonly PictureBox _preview;
    private TextureBrowserForm? _browser;
    private bool _loading;

    // The sheet's "current material" texture — what Apply pushes to the selected faces (Hammer's
    // m_pTexture / the preview). Set by Browse (Pick) and by selection sync (ShowPreview).
    private string? _picked;
    private string? _shownTexture;

    /// <summary>The sheet's current material — what right-click apply paints while the sheet is open
    /// (the shown/previewed texture, falling back to the last picked one).</summary>
    public string? CurrentMaterial => _shownTexture ?? _picked;
    private readonly ToolTip _toolTip = new();
    private SolidFace? _lastFace;   // last face shown — so clicking a NEW face inherits its texture (#22)

    public FaceEditDialog(MapDocument doc, TextureLibrary lib, Func<string?> currentTexture, Action redraw,
                          Action<string>? setActiveTexture = null)
    {
        _doc = doc; _lib = lib; _currentTexture = currentTexture; _redraw = redraw; _setActiveTexture = setActiveTexture;

        Text = "Face Edit Sheet";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(412, 340);
        BackColor = Bg; ForeColor = Fg;
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _selLabel = Lbl("No faces selected", 12, 8, HdrFg);
        Controls.Add(_selLabel);

        // ── Texture scale / shift / rotation (Hammer's top row) ──────────────
        Controls.Add(Lbl("Texture scale:", 12, 32));
        Controls.Add(Lbl("Texture shift:", 150, 32));
        Controls.Add(Lbl("Rotation:", 300, 32));
        Controls.Add(Lbl("X", 12, 56)); Controls.Add(Lbl("Y", 12, 82));
        Controls.Add(Lbl("X", 150, 56)); Controls.Add(Lbl("Y", 150, 82));

        _scaleS = Num(32, 54, -100000, 100000, 8, 2);   // negative scale = mirror the texture (Hammer)
        _scaleT = Num(32, 80, -100000, 100000, 8, 2);
        _shiftS = Num(170, 54, -1000, 1000, 0.0625m, 4);
        _shiftT = Num(170, 80, -1000, 1000, 0.0625m, 4);
        _rotate = Num(300, 54, -359, 359, 15, 0);
        foreach (var n in new[] { _scaleS, _scaleT, _shiftS, _shiftT, _rotate }) n.ValueChanged += (_, _) => ApplyMapping();

        // ── World / Face mapping + treat-as-one ──────────────────────────────
        _alignFace = Chk("Face-aligned (vs World)", 300, 84);
        _alignFace.CheckedChanged += (_, _) => ApplyMapping();
        Controls.Add(_alignFace);
        _treatAsOne = Chk("Treat selection as one", 300, 108);
        Controls.Add(_treatAsOne);

        // ── Texture preview + name/size + material buttons ───────────────────
        _preview = new PictureBox
        {
            Location = new Point(12, 120), Size = new Size(96, 96), BackColor = BgIn,
            BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.Zoom,
        };
        Controls.Add(_preview);
        // #23: right-click the texture preview = realign the shown texture cleanly to the selected
        // face(s) — apply it face-aligned with zero shift/rotation (a fresh "stick it to this face")
        // versus left-click pick-and-keep which preserves the existing mapping.
        _preview.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) RealignToFace(); };
        _toolTip.SetToolTip(_preview, "Left-click a face to pick its texture · right-click here to realign the texture to the selected face");
        _texLabel = Lbl("(no texture)", 12, 220);

        Controls.Add(Btn("Browse…",  120, 120, 130, 26, OnBrowse));
        Controls.Add(Btn("Replace…", 120, 150, 130, 26, OnReplace));
        var apply = Btn("Apply (to selected)", 120, 180, 130, 26, ApplySelected);
        apply.BackColor = Color.FromArgb(0, 122, 204); apply.ForeColor = Color.White;
        Controls.Add(apply);
        Controls.Add(Btn("Mark", 120, 210, 130, 24, MarkFaces));   // Hammer: select every face using this texture

        // ── Justify / Fit / Align ──────────────────────────────────────────── (#19: lowered)
        Controls.Add(Lbl("Justify:", 264, 232));
        Btn("L", 312, 228, 28, 24, () => Justify('L')); Btn("R", 344, 228, 28, 24, () => Justify('R'));
        Btn("T", 312, 256, 28, 24, () => Justify('T')); Btn("B", 344, 256, 28, 24, () => Justify('B'));
        Btn("C", 376, 242, 28, 24, () => Justify('C'));
        Controls.Add(Btn("Fit", 264, 288, 66, 24, Fit));
        Controls.Add(Btn("Align→adj", 336, 288, 68, 24, AlignToAdjacent));

        Refresh2();
    }

    // ── Window placement: always open centred on the editor window ──────────

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CenterOnOwner();
    }

    /// <summary>Centres the sheet on its owner (the editor window). Called on show and on re-open,
    /// since the sheet is modeless and reused.</summary>
    public void CenterOnOwner()
    {
        var o = Owner;
        Location = o == null
            ? new Point(Math.Max(0, (Screen.PrimaryScreen!.WorkingArea.Width - Width) / 2), 80)
            : new Point(o.Left + (o.Width - Width) / 2, o.Top + (o.Height - Height) / 2);
    }

    // ── Selection sync ──────────────────────────────────────────────────────

    /// <summary>Loads the (last) selected face's mapping + texture into the sheet.</summary>
    public void Refresh2()
    {
        var faces = TargetFaces();
        _selLabel.Text = faces.Count == 0
            ? "No faces or brushes selected — click a brush face in the 3D view"
            : $"{faces.Count} face(s) selected";
        var f = faces.LastOrDefault();

        _loading = true;
        if (f != null)
        {
            _scaleS.Value = Clamp(f.TexScaleS, -100000, 100000);
            _scaleT.Value = Clamp(f.TexScaleT, -100000, 100000);
            _shiftS.Value = Clamp(f.TexShiftS, -1000, 1000);
            _shiftT.Value = Clamp(f.TexShiftT, -1000, 1000);
            // Read the rotation back from the actual texture axes — the stored field can be stale after a
            // brush rotate / seam-align (which moved the axes), so a face that's visibly turned 90° shows 90.
            _rotate.Value = Clamp(MathF.Round(f.CurrentRotationDegrees()), -359, 359);
            _alignFace.Checked = f.AlignToFace;
        }
        _loading = false;

        // Hammer "pick it up": clicking a NEW face loads ITS texture into the sheet (so you can keep
        // using it). A mere refresh (same face) or a just-browsed pick keeps the chosen texture.
        if (f != null && !ReferenceEquals(f, _lastFace)) { _picked = f.TextureName; ShowPreview(f.TextureName); }
        else ShowPreview(_picked ?? _currentTexture() ?? f?.TextureName);
        _lastFace = f;
    }

    private void ShowPreview(string? name)
    {
        _shownTexture = name;
        var entry = name != null ? _lib.Find(name) : null;
        _preview.Image = entry?.Image;
        _texLabel.Text = entry != null
            ? $"{Short(entry.Name)}   {entry.Image.Width}×{entry.Image.Height}  {entry.TypeLabel}"
            : "(no texture)";
    }

    private static string Short(string n) => n.Length <= 40 ? n : "…" + n[^39..];

    /// <summary>
    /// Hammer's Face Edit target set: every individually-selected face, PLUS every face of any
    /// selected brush (selecting a whole brush applies to all its faces). Union, de-duplicated —
    /// this is what every face operation on the sheet acts on.
    /// </summary>
    private List<SolidFace> TargetFaces()
    {
        // #21: individually-clicked faces take priority — if you've selected specific faces, operations
        // (Justify/Fit/Apply) act on ONLY those, not the whole brush. Only when NO face is individually
        // selected does selecting a whole brush apply to all of its faces.
        var faces = _doc.SelectedFaces.ToList();
        if (faces.Count > 0) return faces;
        var result = new List<SolidFace>();
        foreach (var s in _doc.Solids)
            if (s.IsSelected)
                result.AddRange(s.Faces);
        return result;
    }

    // Refresh the sheet when its selection context might have changed by gaining focus — e.g. you
    // selected brushes in a viewport and clicked over to the sheet to press Apply.
    protected override void OnActivated(EventArgs e) { base.OnActivated(e); Refresh2(); }

    // ── Material browse / replace ───────────────────────────────────────────

    private void OnBrowse()
    {
        // Hammer: browsing only picks the CURRENT texture — a single click previews it, a double click
        // confirms the choice and closes the browser. It never paints faces; that's the Apply button.
        OpenBrowser(select: Pick, commit: Pick);
    }

    // The chosen texture becomes the sheet's current texture AND the global active texture (so it's
    // immediately usable for painting), and is shown in the preview — Hammer's global current texture.
    private void Pick(string name)
    {
        _picked = name;
        _setActiveTexture?.Invoke(name);
        ShowPreview(name);
    }

    /// <summary>Make <paramref name="name"/> the sheet's current (shown + applied) texture — called when a
    /// texture is picked in the docked panel / pop-out browser, so the sheet's preview and its Apply/Align
    /// stay in sync with the last-clicked swatch (they were diverging: the sheet showed a stale pick).</summary>
    public void SetCurrentTexture(string name)
    {
        _picked = name;
        ShowPreview(name);
    }

    // Hammer's Replace: opens the Replace Textures dialog (not the browser), pre-filled with the
    // current face's texture as "Find"; swaps it for the chosen texture across the map (or selection).
    private void OnReplace()
    {
        string? old = TargetFaces().LastOrDefault()?.TextureName ?? _shownTexture;
        var dlg = new ReplaceTexturesDialog(_lib, preFind: old);
        dlg.ReplaceRequested += () =>
        {
            // "Selected faces only" = this Face Edit sheet's current target faces, re-read live each click.
            var targetSet = new HashSet<SolidFace>(TargetFaces());
            _doc.RecordUndo();
            int n = 0;
            foreach (var s in _doc.Solids)
                foreach (var f in s.Faces)
                {
                    if (dlg.SelectedOnly && !targetSet.Contains(f)) continue;
                    if (string.Equals(f.TextureName, dlg.Find, StringComparison.OrdinalIgnoreCase)) { f.TextureName = dlg.Replace; n++; }
                }
            _doc.NotifyChanged(); _redraw(); Refresh2();
            dlg.ShowResult(n);
        };
        dlg.Show(this);
    }

    // Hammer's Mark: select every face in the map that uses the sheet's current texture.
    private void MarkFaces()
    {
        var tex = _shownTexture;
        if (string.IsNullOrEmpty(tex)) return;
        _doc.ClearFaceSelection();
        foreach (var s in _doc.Solids)
            foreach (var f in s.Faces)
                if (string.Equals(f.TextureName, tex, StringComparison.OrdinalIgnoreCase)) f.FaceSelected = true;
        _doc.NotifyChanged(); _redraw(); Refresh2();
    }

    private void OpenBrowser(Action<string> select, Action<string> commit)
    {
        if (_browser is { IsDisposed: false }) { _browser.Activate(); return; }
        _browser = new TextureBrowserForm(_lib);
        _browser.TextureSelected  += name => { select(name); Refresh2(); };  // single click: preview/select
        _browser.TextureCommitted += name => { commit(name); Refresh2(); };  // double click: apply + close
        _browser.FormClosed += (_, _) => _browser = null;
        _browser.Show(this);
    }

    // Hammer's Apply (FACE_APPLY_ALL): push the current material — texture AND the scale / shift /
    // rotation / alignment shown in the sheet — onto every selected face at once.
    private void ApplySelected()
    {
        var faces = TargetFaces();
        if (faces.Count == 0)
        {
            // Don't fail silently — tell the user why nothing happened and how to build a selection.
            string hint = _doc.Solids.Count == 0
                ? "There are no editable brushes in this level yet. Face Edit works on brushes you " +
                  "create with the Block/Brush tool — imported ROM geometry is read-only reference " +
                  "and can't be textured here.\n\nCreate a brush, then select it (or click its faces)."
                : "Nothing is selected.\n\nApply covers the selected faces — and all faces of any " +
                  "selected brush. Either select a brush (Select tool), or click brush faces in the " +
                  "3D view with the Face Edit tool (Ctrl-click to add more), then press Apply.";
            MessageBox.Show(this, hint, "Apply to Selected Faces", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var tex = _shownTexture ?? _picked ?? _currentTexture();   // WYSIWYG: apply the texture the sheet SHOWS
        _doc.RecordUndo();
        foreach (var f in faces)
        {
            if (!string.IsNullOrEmpty(tex)) f.TextureName = tex;
            ApplyMappingFields(f);
        }
        _doc.NotifyChanged(); _redraw();
        if (tex != null) ShowPreview(tex);
    }

    // ── Mapping edits (unchanged behaviour) ─────────────────────────────────

    private void ApplyMapping()
    {
        if (_loading) return;
        var faces = TargetFaces();
        if (faces.Count == 0) return;
        _doc.RecordUndo();
        foreach (var f in faces)
            ApplyMappingFields(f);
        _doc.NotifyChanged(); _redraw();
    }

    // Push the dialog's scale/shift/rotation/align onto a face, updating the EXPLICIT texture axes the
    // way Hammer's Face-Edit sheet does: the rotation field rotates the stored axes by the DELTA (so it
    // composes with any texture-lock rotation already baked in), and toggling face-alignment re-derives
    // the axes from the normal. Scale/shift are independent of the axis directions.
    private void ApplyMappingFields(SolidFace f)
    {
        bool alignChanged = f.AlignToFace != _alignFace.Checked;
        f.AlignToFace = _alignFace.Checked;
        f.TexScaleS = (float)_scaleS.Value; f.TexScaleT = (float)_scaleT.Value;
        f.TexShiftS = (float)_shiftS.Value; f.TexShiftT = (float)_shiftT.Value;
        // Rotation is ABSOLUTE relative to the face's base axes. Only re-derive the axes when the rotation
        // (or alignment) actually changed, so editing just the scale/shift preserves a folded/locked mapping.
        float newRot = (float)_rotate.Value;
        if (alignChanged || MathF.Abs(newRot - MathF.Round(f.CurrentRotationDegrees())) > 0.5f)
            f.SetRotation(newRot);
    }

    private void Justify(char j)
    {
        var faces = TargetFaces();
        if (faces.Count == 0) return;
        _doc.RecordUndo();
        // Treat-as-one: justify against the combined bounds (lead-face axes) and give every face the
        // same shift, so the texture aligns across the whole selection rather than per-face.
        bool one = _treatAsOne.Checked;
        (float u0, float u1, float v0, float v1) shared = default;
        if (one)
        {
            var (u, v) = faces[0].TextureAxes();
            UvBounds(faces, u / Sane(faces[0].TexScaleS), v / Sane(faces[0].TexScaleT),
                     out shared.u0, out shared.u1, out shared.v0, out shared.v1);
        }
        foreach (var f in faces)
        {
            float uMin, uMax, vMin, vMax;
            if (one) { uMin = shared.u0; uMax = shared.u1; vMin = shared.v0; vMax = shared.v1; }
            else
            {
                var (u, v) = f.TextureAxes();
                UvBounds([f], u / Sane(f.TexScaleS), v / Sane(f.TexScaleT), out uMin, out uMax, out vMin, out vMax);
            }
            switch (j)
            {
                case 'L': f.TexShiftS = -uMin; break;
                case 'R': f.TexShiftS = -uMax + 1f; break;
                case 'T': f.TexShiftT = -vMin; break;
                case 'B': f.TexShiftT = -vMax + 1f; break;
                case 'C': f.TexShiftS = -(uMin + uMax) * 0.5f + 0.5f; f.TexShiftT = -(vMin + vMax) * 0.5f + 0.5f; break;
            }
        }
        _doc.NotifyChanged(); Refresh2(); _redraw();
    }

    private void Fit()
    {
        var faces = TargetFaces();
        if (faces.Count == 0) return;
        _doc.RecordUndo();
        if (_treatAsOne.Checked)
        {
            // Treat the whole selection as one surface: fit the texture once across the combined UV
            // bounds (measured in the lead face's axes) and give every face that same mapping —
            // continuous across coplanar faces.
            var (u, v) = faces[0].TextureAxes();
            UvBounds(faces, u, v, out float uMin, out float uMax, out float vMin, out float vMax);
            float sS = MathF.Max(1f, uMax - uMin), sT = MathF.Max(1f, vMax - vMin);
            foreach (var f in faces)
            {
                f.AlignToFace = faces[0].AlignToFace; f.TexRotation = faces[0].TexRotation;
                f.TexScaleS = sS; f.TexScaleT = sT;
                f.TexShiftS = -uMin / sS; f.TexShiftT = -vMin / sT;
            }
        }
        else
        {
            foreach (var f in faces)
            {
                var (u, v) = f.TextureAxes();
                UvBounds([f], u, v, out float uMin, out float uMax, out float vMin, out float vMax);
                f.TexScaleS = MathF.Max(1f, uMax - uMin); f.TexScaleT = MathF.Max(1f, vMax - vMin);
                f.TexShiftS = -uMin / f.TexScaleS; f.TexShiftT = -vMin / f.TexScaleT;
            }
        }
        _doc.NotifyChanged(); Refresh2(); _redraw();
    }

    // Combined raw (unscaled) UV bounds of every vertex of the given faces, projected onto axes (u,v).
    private static void UvBounds(IReadOnlyList<SolidFace> faces, Vector3 u, Vector3 v,
                                 out float uMin, out float uMax, out float vMin, out float vMax)
    {
        uMin = vMin = float.MaxValue; uMax = vMax = float.MinValue;
        foreach (var f in faces)
            foreach (var p in f.Vertices)
            {
                float du = Vector3.Dot(p, u), dv = Vector3.Dot(p, v);
                uMin = MathF.Min(uMin, du); uMax = MathF.Max(uMax, du);
                vMin = MathF.Min(vMin, dv); vMax = MathF.Max(vMax, dv);
            }
    }

    // #15: align the texture across the visible seam (Hammer "align to adjacent"). For each non-lead
    // selected face that shares an EDGE with the lead, fold the lead's texture axes about that edge into
    // the neighbour's plane and offset its shift so the texture is continuous across the seam — the
    // texture "wraps around the corner" instead of restarting. Faces with no shared edge inherit the
    // lead's mapping verbatim (the previous behaviour) as a fallback.
    private void AlignToAdjacent()
    {
        var faces = TargetFaces();
        if (faces.Count < 2) return;
        var src = faces[0];
        _doc.RecordUndo();
        foreach (var dst in faces.Skip(1))
            TextureAlign.TryAlignAcrossSeam(src, dst);   // fold the lead's mapping across each seam
        _doc.NotifyChanged(); Refresh2(); _redraw();
    }

    // #23: realign the shown texture cleanly to the selected face(s) — face-aligned, zero shift/rotation,
    // axes re-derived from the face normal. "Stick this texture flat onto this face."
    private void RealignToFace()
    {
        var faces = TargetFaces();
        if (faces.Count == 0) return;
        var tex = _shownTexture ?? _picked ?? _currentTexture();   // WYSIWYG: realign the texture the sheet SHOWS
        _doc.RecordUndo();
        foreach (var f in faces)
        {
            if (!string.IsNullOrEmpty(tex)) f.TextureName = tex;
            f.AlignToFace = true;
            f.TexRotation = 0f;
            f.TexShiftS = 0f; f.TexShiftT = 0f;
            f.ResetAxes();
        }
        _doc.NotifyChanged(); Refresh2(); _redraw();
        if (tex != null) ShowPreview(tex);
    }

    // ── Small control helpers ───────────────────────────────────────────────

    private static decimal Clamp(float v, decimal min, decimal max) => Math.Clamp((decimal)v, min, max);
    private static float Sane(float s) => MathF.Abs(s) < 1e-3f ? 64f : s;

    private static Label Lbl(string t, int x, int y, Color? fg = null) => new()
    { Text = t, Location = new Point(x, y), AutoSize = true, ForeColor = fg ?? Fg, Font = new Font("Segoe UI", 8.5f) };

    private NumericUpDown Num(int x, int y, decimal min, decimal max, decimal inc, int dec)
    {
        var n = new NumericUpDown
        {
            Location = new Point(x, y), Width = 92, Minimum = min, Maximum = max,
            DecimalPlaces = dec, Increment = inc, Value = Math.Clamp(dec == 0 ? 0 : 64, min, max),
            BackColor = BgIn, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
        };
        Controls.Add(n);
        return n;
    }

    private CheckBox Chk(string t, int x, int y) => new()
    { Text = t, Location = new Point(x, y), AutoSize = true, ForeColor = Fg, Font = new Font("Segoe UI", 8f) };

    private Button Btn(string t, int x, int y, int w, int h, Action onClick)
    {
        var b = new Button { Text = t, Location = new Point(x, y), Size = new Size(w, h),
            BackColor = BtnBg, ForeColor = Fg, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f) };
        b.Click += (_, _) => onClick();
        Controls.Add(b);
        return b;
    }
}
