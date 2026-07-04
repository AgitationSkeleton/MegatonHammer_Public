using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MegatonHammer.Editor;

/// <summary>
/// Associates the editor's .mhproj project files with this executable for the CURRENT USER
/// (HKCU\Software\Classes — no administrator rights needed): double-clicking a project opens it
/// here, and the files show the app icon. Idempotent and best-effort — it only rewrites the
/// registry when the recorded command no longer matches this exe (moved / rebuilt), so normal
/// launches do no shell work. Pairs with <see cref="Forms.WindowsJumpList"/>, whose "Recent
/// Projects" items launch the same <c>exe "%1"</c> command this registers.
/// </summary>
public static class FileAssociation
{
    private const string ProgId = "MegatonHammer.Project";
    private const string Ext = ProjectSerializer.Extension;     // ".mhproj"
    private const string FriendlyType = "Megaton Hammer Project";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    private static string OpenCommand(string exe) => $"\"{exe}\" \"%1\"";

    /// <summary>True if .mhproj is currently associated with THIS executable.</summary>
    public static bool IsRegistered()
    {
        try { return CurrentCommand() == OpenCommand(ExePath); } catch { return false; }
    }

    /// <summary>Registers (or refreshes) the per-user .mhproj association if it isn't already
    /// pointing at this exe. Safe to call on every launch.</summary>
    public static void EnsureRegistered()
    {
        try
        {
            string exe = ExePath;
            if (string.IsNullOrEmpty(exe)) return;
            if (CurrentCommand() == OpenCommand(exe)) return;   // already current — no shell churn
            Register(exe);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* association is a convenience; never block startup over it */ }
    }

    /// <summary>Removes the per-user .mhproj association.</summary>
    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            using (var ext = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Ext}", writable: true))
            {
                if (ext != null)
                {
                    if (ext.GetValue(null) as string == ProgId) ext.DeleteValue(string.Empty, throwOnMissingValue: false);
                    using var owp = ext.OpenSubKey("OpenWithProgids", writable: true);
                    owp?.DeleteValue(ProgId, throwOnMissingValue: false);
                }
            }
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static string? CurrentCommand()
    {
        using var cmd = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        return cmd?.GetValue(null) as string;
    }

    private static void Register(string exe)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

        // The ProgID: friendly name, icon (the exe's embedded app icon), and the open verb.
        using (var progid = classes.CreateSubKey(ProgId))
        {
            progid.SetValue(null, FriendlyType);
            progid.SetValue("FriendlyTypeName", FriendlyType);
            using (var icon = progid.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exe}\",0");
            using (var cmd = progid.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, OpenCommand(exe));
        }

        // Point the extension at the ProgID, and also list it under OpenWithProgids so it appears in
        // the shell's "Open with" menu even if some other app later claims the default.
        using (var ext = classes.CreateSubKey(Ext))
        {
            ext.SetValue(null, ProgId);
            using var owp = ext.CreateSubKey("OpenWithProgids");
            owp.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
    }
}
