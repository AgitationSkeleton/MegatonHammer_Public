using System.Runtime.InteropServices;

namespace MegatonHammer.Rendering;

/// <summary>
/// Creates and manages a native Win32 WGL OpenGL 3.3 Core context
/// attached to any WinForms control handle.
/// </summary>
public sealed class WglContext : IDisposable
{
    // ── GDI / User32 ──────────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool   ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]  private static extern int    ChoosePixelFormat(IntPtr hDC, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("gdi32.dll")]  private static extern bool   SetPixelFormat(IntPtr hDC, int format, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("gdi32.dll",  EntryPoint = "SwapBuffers")]
    private static extern bool GdiSwapBuffers(IntPtr hDC);

    // ── opengl32.dll ──────────────────────────────────────────────────────
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hDC);
    [DllImport("opengl32.dll")] private static extern bool   wglDeleteContext(IntPtr hRC);
    [DllImport("opengl32.dll")] private static extern bool   wglMakeCurrent(IntPtr hDC, IntPtr hRC);
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);
    [DllImport("opengl32.dll")] private static extern bool   wglShareLists(IntPtr hRC1, IntPtr hRC2);

    // wglCreateContextAttribsARB
    private delegate IntPtr wglCreateContextAttribsARBDelegate(IntPtr hDC, IntPtr hShareContext, int[] attribs);
    private static wglCreateContextAttribsARBDelegate? _createContextAttribs;

    // wglSwapIntervalEXT (vsync control)
    private delegate bool wglSwapIntervalEXTDelegate(int interval);
    private static wglSwapIntervalEXTDelegate? _swapInterval;

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion;
        public uint   dwFlags;
        public byte   iPixelType, cColorBits;
        public byte   cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift;
        public byte   cAlphaBits, cAlphaShift;
        public byte   cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte   cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint   dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    // PFD flags
    private const uint PFD_DRAW_TO_WINDOW   = 0x00000004;
    private const uint PFD_SUPPORT_OPENGL   = 0x00000020;
    private const uint PFD_DOUBLEBUFFER     = 0x00000001;
    private const byte PFD_TYPE_RGBA        = 0;
    private const byte PFD_MAIN_PLANE       = 0;

    // WGL_ARB_create_context attrib tokens
    private const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    private const int WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    private const int WGL_CONTEXT_FLAGS_ARB         = 0x2094;
    private const int WGL_CONTEXT_PROFILE_MASK_ARB  = 0x9126;
    private const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB = 0x00000001;

    private IntPtr _hwnd, _hDC, _hRC;
    private bool _disposed;

    // ── Public ────────────────────────────────────────────────────────────

    public void Create(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _hDC  = GetDC(hwnd);

        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize        = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion     = 1,
            dwFlags      = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            iPixelType   = PFD_TYPE_RGBA,
            cColorBits   = 32,
            cDepthBits   = 24,
            cStencilBits = 8,
            iLayerType   = PFD_MAIN_PLANE
        };

        int fmt = ChoosePixelFormat(_hDC, ref pfd);
        if (fmt == 0) throw new Exception("ChoosePixelFormat failed");
        if (!SetPixelFormat(_hDC, fmt, ref pfd)) throw new Exception("SetPixelFormat failed");

        // Create a legacy context first so we can load ARB extension
        IntPtr legacyRC = wglCreateContext(_hDC);
        if (legacyRC == IntPtr.Zero) throw new Exception("wglCreateContext (legacy) failed");
        wglMakeCurrent(_hDC, legacyRC);

        // Load wglCreateContextAttribsARB if not yet loaded
        if (_createContextAttribs == null)
        {
            IntPtr fn = wglGetProcAddress("wglCreateContextAttribsARB");
            if (fn != IntPtr.Zero)
                _createContextAttribs = Marshal.GetDelegateForFunctionPointer<wglCreateContextAttribsARBDelegate>(fn);
        }

        if (_createContextAttribs != null)
        {
            int[] attribs =
            [
                WGL_CONTEXT_MAJOR_VERSION_ARB, 3,
                WGL_CONTEXT_MINOR_VERSION_ARB, 3,
                WGL_CONTEXT_PROFILE_MASK_ARB,  WGL_CONTEXT_CORE_PROFILE_BIT_ARB,
                0
            ];

            IntPtr coreRC = _createContextAttribs(_hDC, IntPtr.Zero, attribs);
            wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            wglDeleteContext(legacyRC);

            if (coreRC == IntPtr.Zero) throw new Exception("wglCreateContextAttribsARB failed — driver may not support OpenGL 3.3");
            _hRC = coreRC;
        }
        else
        {
            // Fallback: keep the legacy context (OpenGL 2.x compat)
            _hRC = legacyRC;
        }

        wglMakeCurrent(_hDC, _hRC);

        // Disable vsync on this context. With four viewports each calling SwapBuffers per frame,
        // vsync (the driver default) makes each swap block on the vertical retrace, so the panes
        // stall each other and camera movement stutters. The render timer already paces redraws.
        if (_swapInterval == null)
        {
            IntPtr fn = wglGetProcAddress("wglSwapIntervalEXT");
            if (fn != IntPtr.Zero)
                _swapInterval = Marshal.GetDelegateForFunctionPointer<wglSwapIntervalEXTDelegate>(fn);
        }
        try { _swapInterval?.Invoke(0); } catch { /* extension absent — harmless */ }
    }

    public void MakeCurrent()   => wglMakeCurrent(_hDC, _hRC);
    public void SwapBuffers()   => GdiSwapBuffers(_hDC);
    public void ReleaseCurrent()=> wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);

    // ── Binding loader for OpenTK ─────────────────────────────────────────
    // OpenTK 4.x uses GL.LoadBindings(IBindingsContext) to set up its static GL wrappers.
    // We satisfy IBindingsContext by forwarding to wglGetProcAddress + opengl32 export table.
    public IntPtr GetProcAddress(string name)
    {
        var ptr = wglGetProcAddress(name);
        if (ptr == IntPtr.Zero || ptr.ToInt64() is 1 or 2 or 3 or -1)
        {
            // Some core functions aren't in wglGetProcAddress — fall back to opengl32.dll export
            if (NativeLibrary.TryLoad("opengl32.dll", out var lib) &&
                NativeLibrary.TryGetExport(lib, name, out var ep))
                return ep;
        }
        return ptr;
    }

    public void Dispose()
    {
        if (_disposed) return;
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        if (_hRC != IntPtr.Zero) wglDeleteContext(_hRC);
        if (_hDC != IntPtr.Zero && _hwnd != IntPtr.Zero) ReleaseDC(_hwnd, _hDC);
        _disposed = true;
    }
}

/// <summary>
/// Bridges WglContext.GetProcAddress to OpenTK 4.x's IBindingsContext.
/// </summary>
public sealed class WglBindingsContext : OpenTK.IBindingsContext
{
    private readonly WglContext _ctx;
    public WglBindingsContext(WglContext ctx) => _ctx = ctx;
    public IntPtr GetProcAddress(string procName) => _ctx.GetProcAddress(procName);
}
