using System.Drawing;

namespace MegatonHammer.Forms;

/// <summary>
/// Shared, memoised <see cref="Font"/> instances for the editor's rebuildable property panels.
///
/// A <see cref="Font"/> is a GDI object with a Win32 handle. WinForms controls do NOT dispose a Font
/// assigned to their <c>Font</c> property (the control doesn't own it), so panels that rebuild on every
/// selection (EntityConfigDialog, PropertiesPanel) leaked one GDI handle per <c>new Font(...)</c> per
/// rebuild — thousands of rebuilds on a big map (oot_devtestmap) exhausted the process handle pool and
/// crashed with "Error creating window handle" (Win32 1158) the next time any menu/dropdown opened.
///
/// Returning a cached, process-lifetime instance (never disposed — a handful of distinct fonts total)
/// removes the leak entirely: the same Font is shared by every control that asks for it. Drop-in for
/// <c>new Font(...)</c> — same argument shapes.
/// </summary>
public static class UiFonts
{
    private static readonly Dictionary<(string, float, FontStyle), Font> Cache = new();
    private static readonly object Gate = new();

    public static Font Get(string family, float emSize, FontStyle style = FontStyle.Regular)
    {
        var key = (family, emSize, style);
        lock (Gate)
        {
            if (!Cache.TryGetValue(key, out var f)) Cache[key] = f = new Font(family, emSize, style);
            return f;
        }
    }
}
