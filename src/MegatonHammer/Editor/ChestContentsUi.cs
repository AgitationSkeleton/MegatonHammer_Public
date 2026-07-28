using System.Drawing;
using System.Windows.Forms;

namespace MegatonHammer.Editor;

/// <summary>
/// The En_Box "Contents" dropdown, shared by BOTH the docked Properties panel and the double-click Entity
/// dialog so they can never drift apart. It lists None, then the enabled SoH-exclusive optional items (Roc's
/// Feather, Fierce Deity's Mask) at the top in blue — items with no vanilla GetItem id, stored in
/// ZActor.MhCustomItem — then every VALID vanilla get-item (the crash-inducing "(unused)" ids are omitted).
/// A SoH item selection writes MhCustomItem + a valid placeholder get-item id (Recovery Heart) so the chest
/// opens without OoT's empty-chest crash; the fork gives the real item.
/// </summary>
public static class ChestContentsUi
{
    private const int PlaceholderGi = 0x48; // Recovery Heart — a valid native get-item so the chest can open

    private readonly record struct Row(string Display, bool IsSoH, string? SohKey, int VanillaGi);

    /// <summary>True when this field is the En_Box (chest) "Contents" enum on an OoT project.</summary>
    public static bool IsChestContents(ZActor a, ActorParamSchema.Field f, bool nativeIsOoT) =>
        nativeIsOoT && a.Number == 0x000A && f.Kind == ActorParamSchema.FieldKind.Enum
        && f.Name == "Contents" && f.Options is { Count: > 0 };

    /// <summary>Builds the unified Contents combo, or null if this isn't a chest Contents field. Both editors
    /// call this; <paramref name="getGi"/>/<paramref name="putGi"/> read/write the 7-bit native get-item id,
    /// <paramref name="isLoading"/> suppresses change events during (re)load, and <paramref name="onChanged"/>
    /// re-runs the caller's refresh (decoded params, hologram, etc.).</summary>
    public static ComboBox? BuildCombo(ZActor a, ActorParamSchema.Field f, bool nativeIsOoT,
        Color bgInput, Color fgNormal, Font font, Func<int> getGi, Action<int> putGi,
        Func<bool> isLoading, Action onChanged)
    {
        if (!IsChestContents(a, f, nativeIsOoT)) return null;

        var sohOpts = OptionalItems.Enabled(OptionalItemEngine.Soh).ToList();
        var rows = new List<Row>();
        rows.Add(new Row(f.Options![0], false, null, 0));                                  // None (empty)
        foreach (var oi in sohOpts) rows.Add(new Row(oi.DisplayName + " (SoH-only)", true, oi.Key, PlaceholderGi));
        for (int gi = 1; gi < f.Options.Count; gi++)
            if (!f.Options[gi].Contains("(unused)"))                                       // omit crash-inducing ids
                rows.Add(new Row(f.Options[gi], false, null, gi));

        var combo = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bgInput,
            ForeColor = fgNormal, FlatStyle = FlatStyle.Flat, Font = font, Margin = new Padding(2),
            MaxDropDownItems = 24, DrawMode = DrawMode.OwnerDrawFixed, // Fixed (not Variable): no MeasureItem, safe before the handle exists
        };
        // Owner-draw so the SoH-exclusive rows read in blue (distinct, unique-and-exclusive), everything else
        // in the normal foreground. Attach before adding items; DrawItem tolerates the -1 (edit box) index.
        var sohColor = Color.FromArgb(120, 180, 255);
        combo.DrawItem += (_, e) =>
        {
            // A throw inside a paint handler crashes the whole app, so this must never throw.
            try
            {
                e.DrawBackground();
                if (e.Index < 0 || e.Index >= combo.Items.Count) return;
                bool soh = e.Index < rows.Count && rows[e.Index].IsSoH;
                var fg = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText
                         : soh ? sohColor : fgNormal;
                using var br = new SolidBrush(fg);
                e.Graphics.DrawString(combo.Items[e.Index]?.ToString(), e.Font ?? font, br, e.Bounds.Left + 2, e.Bounds.Top + 1);
                e.DrawFocusRectangle();
            }
            catch { /* leave the default background drawn */ }
        };
        foreach (var r in rows) combo.Items.Add(r.Display);

        // Current selection: a SoH item (MhCustomItem set) wins, else the native get-item id.
        int sel = 0;
        if (!string.IsNullOrEmpty(a.MhCustomItem))
        { for (int r = 0; r < rows.Count; r++) if (rows[r].SohKey == a.MhCustomItem) { sel = r; break; } }
        else
        { int cur = getGi(); for (int r = 0; r < rows.Count; r++) if (!rows[r].IsSoH && rows[r].VanillaGi == cur) { sel = r; break; } }
        combo.SelectedIndex = sel;

        // Breadcrumbs so a crash anywhere in this control is captured with context (this combo has a
        // history of handle/paint crashes). The dropdown open is the likely handle-exhaustion trigger.
        combo.DropDown += (_, _) => CrashLog.Breadcrumb($"chest Contents dropdown open (actor 0x{a.Number:X})");
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (isLoading()) return;
            try
            {
                int i = combo.SelectedIndex;
                if (i < 0 || i >= rows.Count) return;
                var r = rows[i];
                CrashLog.Breadcrumb($"set chest Contents -> {r.Display}");
                a.MhCustomItem = r.IsSoH ? r.SohKey : null;
                putGi(r.VanillaGi);   // SoH → the placeholder; vanilla → its get-item id
                onChanged();
            }
            catch (Exception ex) { CrashLog.Fatal("chest Contents change", ex); }
        };
        return combo;
    }

    /// <summary>The SoH-exclusive item key a chest awards (for the in-world hologram), or null. Prefers
    /// MhCustomItem; only when the SoH master toggle is on so it matches what the picker offers.</summary>
    public static string? SohHologramKey(ZActor a) =>
        a.Number == 0x000A && !string.IsNullOrEmpty(a.MhCustomItem)
        && OptionalItems.Enabled(OptionalItemEngine.Soh).Any(o => o.Key == a.MhCustomItem)
            ? a.MhCustomItem : null;
}
