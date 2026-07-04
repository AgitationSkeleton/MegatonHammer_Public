using System.Runtime.InteropServices;

namespace MegatonHammer.Forms;

/// <summary>
/// Windows taskbar / Start-menu Jump List — the "recent projects" list shown when you right-click
/// the program's taskbar button or its Start-menu tile. Built with the Shell ICustomDestinationList
/// API; each item relaunches the editor with the project path as an argument (Program.Main opens it).
/// Entirely best-effort: any COM failure (older Windows, headless, no shell) is swallowed so it can
/// never affect editing. Pairs with the in-app File ▸ Open Recent menu.
/// </summary>
internal static class WindowsJumpList
{
    // Stable per-app identity so the jump list attaches to our taskbar button (and survives across
    // launches). Must be set before any window is shown (see Program.Main).
    private const string AppId = "AgitationSkeleton.MegatonHammer";

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

    public static void SetAppId()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch { /* best effort */ }
    }

    /// <summary>Rebuilds the "Recent Projects" jump-list category from the given paths (most-recent
    /// first). Missing files are skipped.</summary>
    public static void Update(IReadOnlyList<string> recentPaths)
    {
        try { UpdateCore(recentPaths); } catch { /* best effort — never crash the editor over a jump list */ }
    }

    private static void UpdateCore(IReadOnlyList<string> recentPaths)
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        if (string.IsNullOrEmpty(exe)) return;

        var list = (ICustomDestinationList)new CDestinationList();
        list.SetAppID(AppId);
        // BeginList hands back the items the user has manually removed; we must not re-add those.
        list.BeginList(out _, typeof(IObjectArray).GUID, out object removedObj);
        var removed = removedObj as IObjectArray;

        var coll = (IObjectCollection)new CEnumerableObjectCollection();
        int added = 0;
        foreach (var path in recentPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            if (IsRemoved(removed, path)) continue;
            coll.AddObject(CreateShellLink(exe, path));
            if (++added >= 10) break;
        }

        if (added > 0)
            list.AppendCategory("Recent Projects", (IObjectArray)coll);
        list.CommitList();
    }

    // True if the user previously dragged this item off their jump list — respect that.
    private static bool IsRemoved(IObjectArray? removed, string path)
    {
        if (removed == null) return false;
        try
        {
            removed.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                removed.GetAt(i, typeof(IShellLinkW).GUID, out object o);
                if (o is IShellLinkW link)
                {
                    var sb = new System.Text.StringBuilder(260);
                    link.GetArguments(sb, sb.Capacity);
                    if (sb.ToString().Trim('"').Equals(path, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static IShellLinkW CreateShellLink(string exe, string projectPath)
    {
        var link = (IShellLinkW)new CShellLink();
        link.SetPath(exe);
        link.SetArguments($"\"{projectPath}\"");
        link.SetIconLocation(exe, 0);
        link.SetDescription(projectPath);   // tooltip

        // The visible label comes from the PKEY_Title property, not SetDescription.
        var store = (IPropertyStore)link;
        var key = PropertyKey.Title;
        using var title = new PropVariantString(Path.GetFileNameWithoutExtension(projectPath));
        store.SetValue(ref key, title.Variant);
        store.Commit();
        return link;
    }

    // ── COM plumbing ────────────────────────────────────────────────────────

    [ComImport, Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6")] private class CDestinationList { }
    [ComImport, Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a")] private class CEnumerableObjectCollection { }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class CShellLink { }

    [ComImport, Guid("6332debf-87b5-4670-90c0-5e57b408a49e"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        [PreserveSig] int BeginList(out uint cMaxSlots, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                                    [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
        void AppendKnownCategory(int category);
        [PreserveSig] int AddUserTasks(IObjectArray poa);
        void CommitList();
        void GetRemovedDestinations([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                                    [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void AbortList();
    }

    [ComImport, Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint cObjects);
        void GetAt(uint uiIndex, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                   [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("5632b1a4-e38a-400a-928a-d4cd63230295"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection // : IObjectArray
    {
        void GetCount(out uint cObjects);
        void GetAt(uint uiIndex, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                   [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void AddObject([MarshalAs(UnmanagedType.Interface)] object pvObject);
        void AddFromArray(IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
                     int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
                             int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out IntPtr pv);
        void SetValue(ref PropertyKey key, IntPtr pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FmtId;
        public int Pid;
        // System.Title — the jump-list item's visible label.
        public static PropertyKey Title => new()
        { FmtId = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), Pid = 2 };
    }

    // Minimal PROPVARIANT holding a VT_LPWSTR string, allocated as an unmanaged blob for SetValue.
    private sealed class PropVariantString : IDisposable
    {
        private const ushort VT_LPWSTR = 31;
        public IntPtr Variant { get; }

        public PropVariantString(string value)
        {
            // PROPVARIANT layout: u16 vt; u16 r1; u16 r2; u16 r3; (8-byte-aligned) pointer payload.
            Variant = Marshal.AllocCoTaskMem(16);
            for (int i = 0; i < 16; i++) Marshal.WriteByte(Variant, i, 0);
            Marshal.WriteInt16(Variant, 0, unchecked((short)VT_LPWSTR));
            Marshal.WriteIntPtr(Variant, 8, Marshal.StringToCoTaskMemUni(value));
        }

        public void Dispose()
        {
            if (Variant == IntPtr.Zero) return;
            Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(Variant, 8));   // the string blob
            Marshal.FreeCoTaskMem(Variant);
        }
    }
}
