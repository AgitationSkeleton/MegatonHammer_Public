using MegatonHammer.Editor;
using MegatonHammer.Rendering;
using MegatonHammer.Tools;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MegatonHammer.Forms;

public enum ViewportType { Perspective3D, Top, Front, Side }

public sealed class GLViewport : Panel
{
    private static readonly string LogPath =
        @"D:\Copilot_OOT\WorkFolders\MegatonHammer\gl_error.log";

    public ViewportType ViewportType { get; }

    // ── Public editor state set by MainForm ────────────────────────────────
    public MapDocument? Document   { get; set; }
    public ITool?       ActiveTool { get; set; }

    /// <summary>Reference model source used for actor models when no ROM level is imported
    /// (e.g. a fresh scene) — built by the host from the configured game ROM.</summary>
    public Editor.ActorModelResolver? FallbackResolver { get; set; }
    public Rom.RomImage?              FallbackRom      { get; set; }

    /// <summary>The model resolver in effect: the imported level's (when a ROM level is loaded),
    /// else the configured reference ROM's. Used for model-based actor selection + rendering.</summary>
    public Editor.ActorModelResolver? Resolver => Document?.Imported?.Resolver ?? FallbackResolver;
    public int          GridSize   { get; set; } = 64;
    public Textures.TextureLibrary? Textures { get; set; }

    // ── Camera accessors ───────────────────────────────────────────────────
    public Camera2D? ActiveCamera2D => _cam2D;
    public Camera3D? ActiveCamera3D => _cam3D;

    // Exposed so MainForm can skip Q/E tool shortcuts while camera is flying
    public bool IsRightMouseDown => _rightMouseDown;

    /// <summary>Raised when an actor is double-clicked (opens its config dialog).</summary>
    public event Action<ZActor>? ActorDoubleClicked;
    /// <summary>Raised when a brush is double-clicked (no actor under the cursor) — the host opens the
    /// brush/warp properties pop-out. Carries the double-clicked solid.</summary>
    public event Action<Editor.Solid>? SolidDoubleClicked;
    /// <summary>#6: a path waypoint was double-clicked (pathIndex, pointIndex) — the host switches to the
    /// Path tool and selects it, so paths are discoverable/editable without first picking the Path tool.</summary>
    public event Action<int, int>? PathNodeDoubleClicked;

    /// <summary>A path waypoint was single-clicked (pathIndex, pointIndex) while the Select tool was active —
    /// the host switches to the Path tool and selects it, so the diamonds are clickable like an actor.</summary>
    public event Action<int, int>? PathNodeClicked;

    /// <summary>Raised on a right-click (no drag) in a 2D view — host shows the edit context menu.</summary>
    public event Action<Point>? ContextMenuRequested2D;

    /// <summary>Raised on mouse move with the world position under the cursor and the 2D zoom
    /// percentage (−1 in the 3D view) — for the status bar.</summary>
    public event Action<OpenTK.Mathematics.Vector3, float>? CursorMoved;

    // Hammer/GZDoom-Builder style toggled fly mode: cursor is hidden, locked to the
    // viewport centre, and WASD+QE fly without holding a mouse button.
    public bool IsMouseLook => _mouseLook;

    /// <summary>True while the 3D camera is being fly-navigated (RMB held or locked fly mode).</summary>
    public bool IsFlying => _rightMouseDown || _mouseLook;

    /// <summary>Sets a held movement key from outside, so fly movement keeps working even when a
    /// side panel or modeless dialog (not this viewport) holds keyboard focus.</summary>
    public void SetFlyKey(Keys k, bool down) { if (down) _heldKeys.Add(k); else _heldKeys.Remove(k); }

    // ── GL resources ──────────────────────────────────────────────────────
    private readonly WglContext _gl = new();
    private bool _initialized;
    private bool _bindingsLoaded;

    private Camera3D?      _cam3D;
    private Camera2D?      _cam2D;
    private GridRenderer?  _grid;
    private SolidRenderer? _solidRenderer;
    private ActorRenderer? _actorRenderer;
    private ImportedMeshRenderer? _importedRenderer;
    private System.Windows.Forms.Timer? _animTimer;   // ~30fps redraw for animated textures
    private long _animEpoch;                           // tick count when animation clock started
    /// <summary>When set, overrides the wall-clock animation time (deterministic offscreen renders/tests).</summary>
    public float? ForcedAnimTime;
    private BillboardRenderer? _billboards;
    private Rom.ItemIconSource? _itemIcons;   // item/inventory icons for the unmodeled-actor fallback

    // 2D ghost wireframe cache: the ghost mesh is static, so flatten it to world-space edge point-pairs once
    // (per loaded ghost) and just re-project per paint.
    private Editor.ImportedLevel? _ghost2DSrc;
    private float[]? _ghost2DEdges;
    private float[] Ghost2DEdges(Editor.ImportedLevel g)
    {
        if (ReferenceEquals(_ghost2DSrc, g) && _ghost2DEdges != null) return _ghost2DEdges;
        var pts = new List<float>();
        void E(OpenTK.Mathematics.Vector3 a, OpenTK.Mathematics.Vector3 b)
        { pts.Add(a.X); pts.Add(a.Y); pts.Add(a.Z); pts.Add(b.X); pts.Add(b.Y); pts.Add(b.Z); }
        foreach (var room in g.RoomMeshes)
            foreach (var t in room) { E(t.P0, t.P1); E(t.P1, t.P2); E(t.P2, t.P0); }
        _ghost2DSrc = g; _ghost2DEdges = pts.ToArray();
        return _ghost2DEdges;
    }
    private SkyRenderer?   _sky;
    private GlTextRenderer? _text;

    // ── Input state ───────────────────────────────────────────────────────
    // >0 while the user is dragging in ANY viewport. The idle animation timer pauses while it's set so its
    // ~30 fps repaints don't compete with the interaction — the dragged pane repaints on mouse-move anyway
    // (SDK2013 Hammer likewise repaints on demand, not continuously, during edits).
    internal static int InteractionDepth;
    private bool  _rightMouseDown;
    private bool  _middleMouseDown;
    private bool  _leftMouseDown;
    private bool  _mouseLook;
    private Point _lastMouse;
    private Point _rightDownPos;
    private readonly HashSet<Keys> _heldKeys = [];

    // Hammer-style drag auto-scroll: while a left-drag (draw / box-select / move) pushes the cursor past
    // the 2D viewport edge, a timer scrolls the view toward the cursor so you can keep dragging beyond it.
    private System.Windows.Forms.Timer? _dragScrollTimer;
    private const float DragScrollPixelsPerTick = 14f;   // constant on-screen scroll speed

    public GLViewport(ViewportType type)
    {
        ViewportType = type;
        Dock         = DockStyle.Fill;

        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint            |
                 ControlStyles.Opaque               |
                 ControlStyles.Selectable           |
                 ControlStyles.ResizeRedraw, true);
        TabStop        = true;
        DoubleBuffered = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _dragScrollTimer?.Dispose(); _dragScrollTimer = null; }
        base.Dispose(disposing);
    }

    // ── GL lifecycle ──────────────────────────────────────────────────────

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            _gl.Create(Handle);
            _gl.MakeCurrent();

            if (!_bindingsLoaded)
            {
                GL.LoadBindings(new WglBindingsContext(_gl));
                _bindingsLoaded = true;
            }

            InitGL();
            _initialized = true;
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[{ViewportType}] HandleCreated error: {ex}\n\n");
        }
    }

    private void InitGL()
    {
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.LineSmooth);
        GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);
        GL.ClearColor(0.118f, 0.118f, 0.118f, 1f);

        _grid          = new GridRenderer();
        _solidRenderer = new SolidRenderer();
        _actorRenderer = new ActorRenderer();
        _importedRenderer = new ImportedMeshRenderer();
        _billboards = new BillboardRenderer();
        _text = new GlTextRenderer();

        if (ViewportType == ViewportType.Perspective3D)
        {
            _cam3D = new Camera3D();
            _sky   = new SkyRenderer();
            // Drive animated textures (water/lava scroll): redraw ~30 fps while an imported level with
            // animated segments is shown. Idle otherwise (no animated scene = no redraws).
            _animEpoch = Environment.TickCount64;
            _animTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _animTimer.Tick += (_, __) =>
            {
                // Skip idle animation repaints while the user is dragging (any viewport) or the window isn't
                // active — so the ~30 fps churn never steals cycles from an edit or the background.
                if (InteractionDepth != 0 || Form.ActiveForm == null) return;
                if (Document?.Imported is { SegScroll.Count: > 0 } || Document?.Scene.Settings.TextureScrolls.Count > 0
                    || HasWaterBrush(Document)) Invalidate();
            };
            _animTimer.Start();
        }
        else
        {
            _cam2D = new Camera2D
            {
                Axis = ViewportType switch
                {
                    ViewportType.Top   => ViewAxis.Top,
                    ViewportType.Front => ViewAxis.Front,
                    ViewportType.Side  => ViewAxis.Side,
                    _                  => ViewAxis.Top
                }
            };
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_initialized || !IsHandleCreated) return;
        _gl.MakeCurrent();
        GL.Viewport(0, 0, Width, Height);
        Invalidate();
    }

    /// <summary>#8a: while the user is dragging the window border/resizing, the form sets this so every
    /// viewport skips its (expensive) full GL render and just clears — four live scene re-renders per
    /// resize tick was the cause of the severe resize/un-fullscreen stutter. One real redraw follows on
    /// ResizeEnd.</summary>
    public static bool SuspendRender;

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_initialized || SuspendRender)
        {
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(20, 20, 20)), ClientRectangle);
            return;
        }
        try
        {
            _gl.MakeCurrent();
            // 2D ortho views use Hammer's near-black background (#141414); the 3D view keeps
            // its dark grey (the sky gradient draws over it anyway).
            if (ViewportType == ViewportType.Perspective3D) GL.ClearColor(0.118f, 0.118f, 0.118f, 1f);
            else                                            GL.ClearColor(0.078f, 0.078f, 0.078f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // 2D ortho views draw all geometry at z=0; depth testing there only causes
            // z-fighting (gaps where brush/actor edges cross grid lines). Use painter's
            // order for 2D and real depth testing only for the 3D view.
            if (ViewportType == ViewportType.Perspective3D) GL.Enable(EnableCap.DepthTest);
            else                                            GL.Disable(EnableCap.DepthTest);

            // Visgroup visibility: a solid/actor in a hidden visgroup is skipped by the scene renderers.
            if (Document != null && _solidRenderer != null) _solidRenderer.Hidden = Document.IsHidden;

            if (ViewportType == ViewportType.Perspective3D && _cam3D != null)
            {
                RenderScene3D(_cam3D, Width, Height,
                    showSky: Editor.ViewOptions.ShowSky,
                    showGrid: Editor.ViewOptions.ShowGrid3D,
                    showActors: Editor.ViewOptions.ShowEntities3D);

                // Pending brush box preview (Hammer's pre-Enter box) in 3D.
                if (Document != null && ActiveTool is BrushTool pb && pb.HasPendingBox)
                {
                    var (bmn, bmx) = pb.GetBox3D();
                    _solidRenderer!.DrawWireBox3D(bmn, bmx, _cam3D, Width, Height);
                }
                // Clip/slice preview in 3D (Hammer): show where the cut lands as you draw the line —
                // kept brush halves bright, discarded halves greyed.
                if (Document != null && ActiveTool is ClipTool ct3 && ct3.HasPreview
                    && ct3.PreviewSegments3D() is { } clip3)
                    _solidRenderer!.DrawConnections3D(clip3, _cam3D, Width, Height);
            }
            else if (_cam2D != null)
            {
                _grid!.DrawOrtho(_cam2D, Width, Height);
                if (Document != null)
                {
                    // Ghost reference in 2D (trace-over) — dimmed wireframe under the brushes, so you align
                    // new geometry to the vanilla level's footprint in the top-down/front/side panes.
                    if (Document.Ghost != null && Editor.EditorSettings.GhostVisible)
                    {
                        float go = Math.Clamp(Editor.EditorSettings.GhostOpacity, 0.1f, 0.8f);
                        _actorRenderer!.RenderWorldWire2D(Ghost2DEdges(Document.Ghost), _cam2D, Width, Height,
                            new OpenTK.Mathematics.Vector4(0.45f, 0.55f, 0.72f, go));
                    }
                    _solidRenderer!.Render2D(Document.Scene, _cam2D, Width, Height);
                    _solidRenderer!.DrawDecals2D(Document.Scene, _cam2D, Width, Height);
                    if (Editor.ViewOptions.ShowLogicConnections) DrawLogicConnections2D();
                    if (Editor.ViewOptions.ShowEntities2D)
                        _actorRenderer!.Render2D(Document.ActorsToRender, _cam2D, Width, Height, Resolver, adult: true);

                    // Scene paths (0x0D waypoint tracks), with the active waypoint highlighted.
                    if (Document.Scene.Paths.Count > 0)
                    {
                        int hiPath = -1, hiPoint = -1;
                        if (ActiveTool is Tools.PathTool pt) { hiPath = pt.ActivePath; hiPoint = pt.ActivePoint; }
                        _solidRenderer!.DrawPaths2D(
                            Document.Scene.Paths.Select(PathLine).ToList(),
                            _cam2D, Width, Height, hiPath, hiPoint);
                    }
                }
                DrawToolOverlay2D();
                DrawSelectionDimensions2D();
            }

            _gl.SwapBuffers();
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[{ViewportType}] Paint error: {ex}\n\n");
        }
    }

    // Sky gradient colours derived from the scene's environment settings.
    // Sky gradient (top colour, horizon colour) for the scene's sky mode. The horizon blends into
    // the scene's own fog colour so the sky meets the world the way the game's does.
    private static (OpenTK.Mathematics.Vector3 top, OpenTK.Mathematics.Vector3 horizon) SkyColors(Editor.SceneSettings s)
    {
        var fog = new OpenTK.Mathematics.Vector3(s.FogColor.R / 255f, s.FogColor.G / 255f, s.FogColor.B / 255f);
        var top = s.Sky == Editor.SkyMode.Cloudy
            ? new OpenTK.Mathematics.Vector3(0.55f, 0.57f, 0.60f)   // overcast grey
            : new OpenTK.Mathematics.Vector3(0.28f, 0.50f, 0.86f);  // OoT day blue (Hyrule Field)
        // Day sky uses a sky-blue horizon rather than the (often greenish) fog so the gradient
        // reads as sky; cloudy meets the fog directly.
        var horizon = s.Sky == Editor.SkyMode.Cloudy ? fog
            : new OpenTK.Mathematics.Vector3(0.62f, 0.74f, 0.92f);
        return (top, horizon);
    }

    // Draws the full 3D scene (sky, grid, geometry, actors, paths) for a given camera/size — shared
    // by the live OnPaint and the offscreen render-to-image export so both match exactly.
    private void RenderScene3D(Camera3D cam, int w, int h, bool showSky, bool showGrid, bool showActors,
                               bool billboardsThroughWalls = false)
    {
        if (Document == null) return;

        if (showSky && _sky != null && Document.Scene.Settings.Sky != Editor.SkyMode.None)
        {
            var (top, horizon) = SkyColors(Document.Scene.Settings);
            _sky.Draw(top, horizon);
        }
        if (showGrid) _grid!.Draw3DGroundGrid(cam, w, h);

        if (Document.Imported != null)
        {
            _importedRenderer!.AnimTime = ForcedAnimTime ?? (Environment.TickCount64 - _animEpoch) / 1000f;   // drive scroll animations
            _importedRenderer!.Render3D(Document.Imported, cam, w, h);
        }
        // Ghost reference overlay (a vanilla level loaded only to trace over) — translucent, drawn before the
        // brushes so your geometry occludes it. Transient + opacity/toggle from EditorSettings.
        if (Document.Ghost != null && Editor.EditorSettings.GhostVisible)
        {
            _importedRenderer!.AnimTime = ForcedAnimTime ?? (Environment.TickCount64 - _animEpoch) / 1000f;
            _importedRenderer!.Render3D(Document.Ghost, cam, w, h, Editor.EditorSettings.GhostOpacity,
                depthWrite: !Editor.EditorSettings.GhostXray);
        }
        if (Document.ReferenceMesh != null)
            _importedRenderer!.RenderTris(Document.ReferenceMesh, cam, w, h);
        _importedRenderer!.RenderObjMeshes(Document.Scene, cam, w, h);   // imported OBJ level geometry (textured)
        _solidRenderer!.Library = Textures;
        _solidRenderer!.AnimTime = ForcedAnimTime ?? (Environment.TickCount64 - _animEpoch) / 1000f;   // brush-authored texture scroll
        _solidRenderer!.Render3D(Document.Scene, cam, w, h);
        _solidRenderer!.DrawDecals3D(Document.Scene, cam, w, h);   // Hammer-style decal overlays

        if (!showActors) return;

        // Real actor models — from the imported ROM if a level is loaded, otherwise from a configured
        // reference ROM so actors (incl. Link's spawn) show models even in a fresh scene.
        var resolver = Document.Imported?.Resolver ?? FallbackResolver;
        var modelRom = Document.Imported?.Rom ?? FallbackRom;
        if (resolver != null && modelRom != null)
        {
            resolver.DoorStyle = Document.Scene.Settings.DoorStyle;   // theme En_Door / Door_Shutter per scene
            resolver.BossDoorTheme = Document.Scene.Settings.BossDoorTheme;   // boss-door emblem (seg-8) per scene
            _importedRenderer!.RenderActors(Document.ActorsToRender, resolver, adult: true, modelRom, cam, w, h);

            // Actors with no 3D model in the ROM → an item/inventory icon billboard so they still read
            // as placed entities. Both OoT and MM now have decodable icons (MM via its yar archive).
            _itemIcons ??= new Rom.ItemIconSource(modelRom);
            bool mm = modelRom.Game == Rom.RomGame.MM;
            var unmodeled = Document.VisibleActors
                .Where(a => !a.IsObsolete && resolver.Resolve(a, adult: true) == null).ToList();
            if (_itemIcons.Available)
                _billboards!.RenderSprites(
                    unmodeled.Select(a => (a, Editor.ActorSpriteMap.IconFor(a.Number, mm))).ToList(), _itemIcons, cam, w, h,
                    ignoreDepth: billboardsThroughWalls);
            else
                _billboards!.RenderFlatSprites(unmodeled, cam, w, h, ignoreDepth: billboardsThroughWalls);

            // #6: float a translucent hologram of each chest's contents above it (En_Box, OoT). The GI
            // value lives in params bits [11:5]; IconForGi maps it to the item's inventory icon (map /
            // compass / keys included). Updates live as the chest's Contents property changes.
            if (_itemIcons.Available && !mm)
            {
                var chestHolos = Document.VisibleActors
                    .Where(a => a.Number == 0x000A)
                    .Select(a => (a, Editor.GetItemTable.IconForGi((a.Variable >> 5) & 0x7F)))
                    .Where(t => t.Item2 >= 0).ToList();
                if (chestHolos.Count > 0) _billboards!.RenderHologram(chestHolos, _itemIcons, cam, w, h);

                // #6: pot (Obj_Tsubo 0x111) contents preview — float the drop's actual model above the pot,
                // like the chest hologram. Rupees (coloured per type) and recovery hearts have real
                // gameplay_keep models; render them as small ghost En_Item00 instances above each pot.
                var potGhosts = new List<ZActor>();
                var potFairies = new List<(ZActor, int)>();
                foreach (var a in Document.VisibleActors.Where(a => a.Number == 0x0111))
                {
                    int drop = a.Variable & 0x1F;
                    int v = PotDropItem00(drop);
                    if (v >= 0)
                        potGhosts.Add(new ZActor { Number = 0x0015, Variable = (ushort)v,
                            XPos = a.XPos, YPos = a.YPos + 50f, ZPos = a.ZPos });
                    else if (drop == 0x12)   // ITEM00_FLEXIBLE: the low-health random drop that yields the pink
                                             // recovery fairy — mark those pots with the fairy icon over them.
                        potFairies.Add((new ZActor { Number = 0x0018, XPos = a.XPos, YPos = a.YPos + 60f, ZPos = a.ZPos },
                            Editor.ActorSpriteMap.IconFor(0x0018, mm)));
                }
                if (potGhosts.Count > 0)
                    _importedRenderer!.RenderActors(potGhosts, resolver, adult: true, modelRom, cam, w, h, cacheSlot: 1);
                if (potFairies.Count > 0 && _itemIcons.Available)
                    _billboards!.RenderHologram(potFairies, _itemIcons, cam, w, h);
            }
        }
        // Obsolete/unknown actors → Eyeball Frog "OBSOLETE" billboard.
        _billboards!.RenderObsolete(Document.VisibleActors.Where(a => a.IsObsolete), cam, w, h, ignoreDepth: billboardsThroughWalls);
        _actorRenderer!.Render3D(Document.VisibleActors, cam, w, h, resolver, adult: true);
        if (Editor.ViewOptions.ShowLogicConnections) DrawLogicConnections3D(cam, w, h);

        // Scene paths (0x0D waypoint tracks): the connecting polylines, then a billboard waypoint marker at
        // each node so nodes read as clickable entities (double-click → path properties).
        if (Document.Scene.Paths.Count > 0)
        {
            _solidRenderer!.DrawPaths3D(Document.Scene.Paths.Select(PathLine).ToList(), cam, w, h);
            var pathNodes = Document.Scene.Paths
                .SelectMany(p => p.Points.Select(pt => (pt, p.IsSelected))).ToList();
            if (pathNodes.Count > 0) _billboards!.RenderPathMarkers(pathNodes, cam, w, h);
        }
    }

    // A path's render polyline: when the path is flagged closed (loop), append the first waypoint so the
    // renderer draws the closing segment. Highlight indices still map to the original Points order.
    private static IReadOnlyList<Vector3> PathLine(Editor.ZPath p)
        => p.Closed && p.Points.Count > 2 ? [.. p.Points, p.Points[0]] : p.Points;

    // True if the scene has any water brush, so the anim timer keeps repainting to animate the water scroll.
    private static bool HasWaterBrush(MapDocument? doc)
    {
        if (doc == null) return false;
        foreach (var room in doc.Scene.Rooms)
            foreach (var s in room.Geometry)
                if (s.IsWater || s.Faces.Any(f => MegatonHammer.Textures.SpecialTextures.Classify(f.TextureName).HasFlag(MegatonHammer.Textures.SpecialKind.WaterSurface)))
                    return true;
        return false;
    }

    /// <summary>Renders the whole level off-screen at an arbitrary camera/resolution into a Bitmap
    /// (used by the level-render export). Sky is omitted; a solid background colour is used so the
    /// shaded geometry reads cleanly. Returns null if the GL context or framebuffer isn't ready.</summary>
    public System.Drawing.Bitmap? RenderToImage(Camera3D cam, int w, int h, bool showActors,
                                                System.Drawing.Color background)
    {
        if (!_initialized || w <= 0 || h <= 0) return null;
        _gl.MakeCurrent();

        int fbo = GL.GenFramebuffer();
        int colorRb = GL.GenRenderbuffer();
        int depthRb = GL.GenRenderbuffer();
        try
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colorRb);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                RenderbufferTarget.Renderbuffer, colorRb);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRb);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, depthRb);

            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                return null;

            GL.Viewport(0, 0, w, h);
            GL.Enable(EnableCap.DepthTest);
            GL.ClearColor(background.R / 255f, background.G / 255f, background.B / 255f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            RenderScene3D(cam, w, h, showSky: false, showGrid: false, showActors: showActors,
                          billboardsThroughWalls: true);   // renders show all actors, even behind walls
            GL.Finish();

            var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            GL.ReadPixels(0, 0, w, h, PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
            bmp.UnlockBits(data);
            bmp.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);   // GL origin is bottom-left
            return bmp;
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"[RenderToImage] {ex}\n\n");
            return null;
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo);
            GL.DeleteRenderbuffer(colorRb);
            GL.DeleteRenderbuffer(depthRb);
            GL.Viewport(0, 0, Math.Max(1, Width), Math.Max(1, Height));
        }
    }

    // Logic-connection wires (Hammer entity I/O) between actors sharing a flag, in this 2D view.
    private void DrawLogicConnections2D()
    {
        if (_cam2D == null || Document == null) return;
        var links = Editor.FlagConnectionAnalyzer.Links(Document.AllActors, Editor.ViewOptions.IsOoT);
        if (links.Count == 0) return;
        (float h, float v) P(OpenTK.Mathematics.Vector3 w) => _cam2D.Axis switch
        {
            ViewAxis.Top   => (w.X, -w.Z),
            ViewAxis.Front => (w.X,  w.Y),
            ViewAxis.Side  => (w.Z,  w.Y),
            _              => (w.X,  w.Y),
        };
        var segs = new List<(float, float, float, float, OpenTK.Mathematics.Vector4)>(links.Count);
        foreach (var l in links)
        {
            var (h1, v1) = P(l.From.Position); var (h2, v2) = P(l.To.Position);
            segs.Add((h1, v1, h2, v2, Rendering.SolidRenderer.ConnectionColor(l.Kind)));
        }
        _solidRenderer!.DrawConnections2D(segs, _cam2D, Width, Height);
    }

    private void DrawLogicConnections3D(Camera3D cam, int w, int h)
    {
        if (Document == null) return;
        var links = Editor.FlagConnectionAnalyzer.Links(Document.AllActors, Editor.ViewOptions.IsOoT);
        if (links.Count == 0) return;
        var segs = new List<(OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector4)>(links.Count);
        foreach (var l in links)
            segs.Add((l.From.Position, l.To.Position, Rendering.SolidRenderer.ConnectionColor(l.Kind)));
        _solidRenderer!.DrawConnections3D(segs, cam, w, h);
    }

    private void DrawToolOverlay2D()
    {
        if (_cam2D == null) return;

        if (ActiveTool is BrushTool bt && bt.HasPendingBox)
        {
            // Hammer: the pending box previews in every 2D view until Enter commits it, with
            // resize handles so it can be fine-tuned (e.g. set the height in the Front/Side views).
            var (h1, v1, h2, v2) = bt.GetBox2D(_cam2D.Axis);
            _solidRenderer!.DrawRubberBand2D(h1, v1, h2, v2, _cam2D, Width, Height);
            // Hammer shows the box's width/height as you drag it out (top & left edges).
            DrawDims2D(MathF.Min(h1, h2), MathF.Max(h1, h2), MathF.Min(v1, v2), MathF.Max(v1, v2));
            var handles = bt.GetHandles(this);
            if (handles != null)
                _solidRenderer!.DrawSelectionHandles2D(handles, 7f, _cam2D, Width, Height);
        }
        else if (ActiveTool is SelectTool st)
        {
            // Rubber-band marquee box while dragging empty space (Hammer touch-select).
            if (st.TryGetMarquee(this, out var mh1, out var mv1, out var mh2, out var mv2))
                _solidRenderer!.DrawRubberBand2D(mh1, mv1, mh2, mv2, _cam2D, Width, Height);
            var handles = st.GetHandles(this);
            if (handles != null)
            {
                // #13: the active transform mode must be clear at a glance. Distinguish by BOTH colour
                // AND size: Scale = small white squares; Rotate = larger bright-green; Skew = larger orange.
                OpenTK.Mathematics.Vector4? fill = st.Mode switch
                {
                    SelectTool.SelectMode.Rotate => new OpenTK.Mathematics.Vector4(0.20f, 1.00f, 0.35f, 1f),
                    SelectTool.SelectMode.Skew   => new OpenTK.Mathematics.Vector4(1.00f, 0.60f, 0.10f, 1f),
                    _                            => new OpenTK.Mathematics.Vector4(1.00f, 1.00f, 1.00f, 1f),
                };
                float hsz = st.Mode == SelectTool.SelectMode.Scale ? 7f : 10f;
                // #3: in Rotate mode also draw the Hammer circular-arrow ring through the corner handles,
                // so the mode is unmistakable (not just a colour change on the corner squares).
                if (st.Mode == SelectTool.SelectMode.Rotate && handles.Count > 0)
                {
                    float minH = handles.Min(p => p.h), maxH = handles.Max(p => p.h);
                    float minV = handles.Min(p => p.v), maxV = handles.Max(p => p.v);
                    float ch = (minH + maxH) * 0.5f, cv = (minV + maxV) * 0.5f;
                    float radius = 0.5f * MathF.Sqrt((maxH - minH) * (maxH - minH) + (maxV - minV) * (maxV - minV));
                    _solidRenderer!.DrawRotateRing2D(ch, cv, radius, _cam2D, Width, Height, fill!.Value);
                }
                _solidRenderer!.DrawSelectionHandles2D(handles, hsz, _cam2D, Width, Height, fill);
            }
        }
        else if (ActiveTool is ClipTool ct && ct.IsActiveDragViewport(this))
        {
            // Hammer-style keep-side preview: kept brush halves bright, discarded halves greyed.
            var preview = ct.PreviewSegments();
            if (preview != null)
                _solidRenderer!.DrawConnections2D(preview, _cam2D, Width, Height);
            var (h1, v1, h2, v2) = ct.GetLine();
            _solidRenderer!.DrawClipLine2D(h1, v1, h2, v2, _cam2D, Width, Height);
        }
        else if (ActiveTool is VertexTool vt)
        {
            if (vt.TryGetMarquee(this, out var qh1, out var qv1, out var qh2, out var qv2))
                _solidRenderer!.DrawRubberBand2D(qh1, qv1, qh2, qv2, _cam2D, Width, Height);
            var handles = vt.GetHandles(this);
            if (handles != null)
                _solidRenderer!.DrawSelectionHandles2D(handles, 7f, _cam2D, Width, Height);
            // Selected vertices highlighted (Hammer red/white selected handles).
            var sel = vt.GetSelectedHandles(this);
            if (sel != null && sel.Count > 0)
                _solidRenderer!.DrawSelectionHandles2D(sel, 7f, _cam2D, Width, Height,
                    new OpenTK.Mathematics.Vector4(0.98f, 0.30f, 0.30f, 1f));
        }
        else if (ActiveTool is CameraTool camt)
        {
            // Each camera gizmo: an eye→look line plus eye + look handles (active camera highlighted).
            var active = new OpenTK.Mathematics.Vector4(0.30f, 0.95f, 0.40f, 1f);
            foreach (var (eh, ev, lh, lv, isActive) in camt.Gizmos2D(_cam2D.Axis))
            {
                _solidRenderer!.DrawClipLine2D(eh, ev, lh, lv, _cam2D, Width, Height);
                _solidRenderer!.DrawSelectionHandles2D([(eh, ev), (lh, lv)], 7f, _cam2D, Width, Height, isActive ? active : null);
            }
        }
    }

    // Hammer draws the selected geometry's size along the top (width) and left (height) of the 2D
    // views, in world (Hammer) units. Shown whenever any brush is selected, regardless of tool.
    private void DrawSelectionDimensions2D()
    {
        if (_cam2D == null || _text == null || Document == null) return;

        bool any = false;
        Vector3 mn = new(1e9f), mx = new(-1e9f);
        foreach (var s in Document.Solids)
            if (s.IsSelected) { var (a, b) = s.GetAABB(); mn = Vector3.ComponentMin(mn, a); mx = Vector3.ComponentMax(mx, b); any = true; }
        if (!any) return;

        // Project the AABB into this view's plane (ortho h/v), matching the 2D world→ortho mapping.
        (float h, float v) Ortho(Vector3 w) => _cam2D.Axis switch
        {
            ViewAxis.Top   => (w.X, -w.Z),
            ViewAxis.Front => (w.X,  w.Y),
            ViewAxis.Side  => (w.Z,  w.Y),
            _              => (w.X,  w.Y),
        };
        var (h1, v1) = Ortho(mn); var (h2, v2) = Ortho(mx);
        DrawDims2D(MathF.Min(h1, h2), MathF.Max(h1, h2), MathF.Min(v1, v2), MathF.Max(v1, v2));
    }

    // Hammer-style dimension labels for an ortho rect: width centred along the top edge, height
    // along the left edge, in world units. Used for the selection AND the pending brush box.
    private void DrawDims2D(float loH, float hiH, float loV, float hiV)
    {
        if (_cam2D == null || _text == null) return;
        float ScrX(float h) => (h - _cam2D.PanX) / _cam2D.Zoom + Width * 0.5f;
        float ScrY(float v) => Height * 0.5f - (v - _cam2D.PanY) / _cam2D.Zoom;
        float left = ScrX(loH), right = ScrX(hiH), top = ScrY(hiV), bottom = ScrY(loV);

        var col = new OpenTK.Mathematics.Vector4(1f, 0.85f, 0.30f, 1f);   // Hammer's gold dimension text
        string wTxt = $"{hiH - loH:0}";
        string hTxt = $"{hiV - loV:0}";

        var (ww, wh) = _text.Measure(wTxt);
        _text.Draw(wTxt, (left + right) * 0.5f - ww * 0.5f, top - wh - 3, Width, Height, col);

        var (hw, hh) = _text.Measure(hTxt);
        _text.Draw(hTxt, left - hw - 5, (top + bottom) * 0.5f - hh * 0.5f, Width, Height, col);
    }

    public void RequestRedraw()
    {
        if (IsHandleCreated) Invalidate();
    }

    /// <summary>Raised by a tool that needs EVERY viewport redrawn, not just this one — e.g. the clip tool,
    /// whose 2D drag must refresh the 3D view's cut preview. The host wires this to redraw all panes.</summary>
    public event Action? RedrawAllRequested;
    public void RequestRedrawAll() { Invalidate(); RedrawAllRequested?.Invoke(); }

    // ── Input ──────────────────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _lastMouse = e.Location;

        if (e.Button == MouseButtons.Right)  { _rightMouseDown  = true; _rightDownPos = e.Location; }
        if (e.Button == MouseButtons.Middle) _middleMouseDown = true;
        if (e.Button == MouseButtons.Left)   _leftMouseDown   = true;
        Capture = true;
        InteractionDepth++;   // pause the idle anim timer while dragging (the active pane repaints on move)

        if (e.Button == MouseButtons.Left)
        {
            // A single click on a path waypoint (in the Select tool) selects it — like clicking an actor —
            // so the diamonds are clickable/discoverable, not just double-clickable. An actor under the cursor
            // wins (keeps normal selection); the Path tool handles its own node clicks.
            if (ActiveTool is Tools.SelectTool && HitTestActor(e.X, e.Y) == null &&
                HitTestPathNode(e.X, e.Y, out int cpi, out int cpt))
            { PathNodeClicked?.Invoke(cpi, cpt); return; }
            ActiveTool?.OnMouseDown(this, e);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        // Balance the OnMouseDown increment; self-heal to 0 once every button is released (in case a
        // down's up was swallowed, e.g. focus loss mid-drag) so the anim timer can never stay paused.
        if (Control.MouseButtons == MouseButtons.None) InteractionDepth = 0;
        else if (InteractionDepth > 0) InteractionDepth--;
        if (e.Button == MouseButtons.Right)
        {
            // If the mouse-down never registered (wasDown == false), this control wasn't the active window
            // when pressed — clicking the 3D view while the modeless Face Edit sheet was focused makes
            // Windows eat the down to activate the editor first. Treat that as a clean click (no drag) so
            // the right-click texture-apply still fires; otherwise the stale _rightDownPos read as a drag
            // and silently swallowed the apply.
            bool wasDown = _rightMouseDown;
            _rightMouseDown = false;
            bool noDrag = !wasDown ||
                (Math.Abs(e.X - _rightDownPos.X) < 3 && Math.Abs(e.Y - _rightDownPos.Y) < 3);
            if (noDrag && ViewportType != ViewportType.Perspective3D)
                // A right-click (no drag) in a 2D view opens the edit context menu (Hammer).
                ContextMenuRequested2D?.Invoke(e.Location);
            else if (noDrag && ViewportType == ViewportType.Perspective3D)
            {
                // With the Face Edit tool active, right-click applies the current material to a face;
                // otherwise it opens the same edit/room/group context menu the 2D views use.
                if (ActiveTool is Tools.TextureTool tt) tt.ApplyAt(this, e.X, e.Y);
                else ContextMenuRequested2D?.Invoke(e.Location);
            }
        }
        if (e.Button == MouseButtons.Middle) _middleMouseDown = false;
        if (e.Button == MouseButtons.Left)   { _leftMouseDown = false; _dragScrollTimer?.Stop(); }

        if (e.Button == MouseButtons.Left)
            ActiveTool?.OnMouseUp(this, e);

        if (!_rightMouseDown && !_middleMouseDown)
            Capture = false;
    }

    // ── Drag auto-scroll (2D) ──────────────────────────────────────────────

    // Start/stop the scroll timer based on whether the dragged cursor has left the 2D viewport bounds.
    private void UpdateDragScroll(Point pos)
    {
        bool outside = _leftMouseDown && _cam2D != null && Capture &&
                       (pos.X < 0 || pos.Y < 0 || pos.X >= Width || pos.Y >= Height);
        if (!outside) { _dragScrollTimer?.Stop(); return; }
        if (_dragScrollTimer == null)
        {
            _dragScrollTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _dragScrollTimer.Tick += DragScrollTick;
        }
        if (!_dragScrollTimer.Enabled) _dragScrollTimer.Start();
    }

    // Each tick: scroll the 2D view toward the off-screen cursor (constant on-screen speed) and re-drive
    // the active tool at the cursor's current position, so the marquee / brush-draw / move keeps tracking
    // past the edge — Hammer's CMapView2DBase::ToolScrollToPoint behaviour.
    private void DragScrollTick(object? sender, EventArgs e)
    {
        if (_cam2D == null || !_leftMouseDown || !Capture) { _dragScrollTimer?.Stop(); return; }
        var pos = PointToClient(Cursor.Position);
        float sp = DragScrollPixelsPerTick * _cam2D.Zoom;   // world units per tick = constant pixels/sec
        bool any = false;
        if (pos.X >= Width)  { _cam2D.PanX += sp; any = true; } else if (pos.X < 0) { _cam2D.PanX -= sp; any = true; }
        if (pos.Y >= Height) { _cam2D.PanY -= sp; any = true; } else if (pos.Y < 0) { _cam2D.PanY += sp; any = true; }
        if (!any) { _dragScrollTimer?.Stop(); return; }
        ActiveTool?.OnMouseMove(this, new MouseEventArgs(MouseButtons.Left, 0, pos.X, pos.Y, 0));
        Invalidate();
    }

    // ── Toggled fly mode (Z) ───────────────────────────────────────────────

    public void ToggleMouseLook()
    {
        if (ViewportType != ViewportType.Perspective3D) return;
        SetMouseLook(!_mouseLook);
    }

    public void SetMouseLook(bool on)
    {
        if (ViewportType != ViewportType.Perspective3D) return;
        if (on == _mouseLook) return;
        _mouseLook = on;
        if (_mouseLook)
        {
            Focus();
            Capture = true;
            Cursor.Hide();
            RecenterCursor();
        }
        else
        {
            Capture = false;
            _heldKeys.Clear();
            Cursor.Show();
        }
        Invalidate();
    }

    private void RecenterCursor()
    {
        if (!IsHandleCreated) return;
        Cursor.Position = PointToScreen(new Point(Width / 2, Height / 2));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // Focus-follows-mouse for 2D panes (Valve Hammer: CMapView2DBase::OnMouseMove SetFocus()):
        // hovering a 2D pane gives it keyboard focus, so arrow-key nudge and shortcuts act on the pane
        // under the cursor with no click needed. Gated on this window being the active form, so it
        // never steals focus from a separate dialog (texture browser / face edit sheet) or another app.
        if (_cam2D != null && !Focused && Form.ActiveForm == FindForm())
            Focus();

        // Locked fly mode: derive look delta from the cursor's offset from centre,
        // then snap the cursor back to centre for unbounded rotation.
        if (_mouseLook && _cam3D != null)
        {
            int ddx = e.X - Width / 2;
            int ddy = e.Y - Height / 2;
            if (ddx != 0 || ddy != 0)
            {
                _cam3D.ProcessMouseDelta(ddx, ddy);
                RecenterCursor();
                Invalidate();
            }
            _lastMouse = e.Location;
            return;
        }

        float dx = e.X - _lastMouse.X;
        float dy = e.Y - _lastMouse.Y;
        _lastMouse = e.Location;

        if (_rightMouseDown && ViewportType == ViewportType.Perspective3D && _cam3D != null)
            _cam3D.ProcessMouseDelta(dx, dy);

        if (_middleMouseDown && _cam2D != null)
            _cam2D.Pan(dx, dy);

        ActiveTool?.OnMouseMove(this, e);

        // While left-dragging a tool in a 2D view, auto-scroll the view when the cursor leaves the bounds.
        if (_leftMouseDown && _cam2D != null) UpdateDragScroll(e.Location);

        // Report the world position under the cursor for the status bar.
        if (CursorMoved != null)
        {
            if (_cam2D != null)
            {
                float oh = _cam2D.PanX + (e.X - Width * 0.5f) * _cam2D.Zoom;
                float ov = _cam2D.PanY - (e.Y - Height * 0.5f) * _cam2D.Zoom;
                float zoomPct = _cam2D.Zoom > 0 ? 100f / _cam2D.Zoom : 100f;   // 1 world/px = 100%
                CursorMoved(_cam2D.Axis switch
                {
                    ViewAxis.Top   => new(oh, 0, -ov),
                    ViewAxis.Front => new(oh, ov, 0),
                    ViewAxis.Side  => new(0, ov, oh),
                    _              => new(oh, ov, 0),
                }, zoomPct);
            }
            else if (_cam3D != null && Picking.PickPoint(Document?.Scene ?? new(),
                     Picking.RayFromScreen(_cam3D, e.X, e.Y, Width, Height), out var wp))
                CursorMoved(wp, -1f);
        }

        if (dx != 0 || dy != 0) Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        float delta = e.Delta / 120f;

        if (ViewportType == ViewportType.Perspective3D && _cam3D != null)
            _cam3D.MoveSpeed = MathHelper.Clamp(_cam3D.MoveSpeed * (1f + delta * 0.15f), 50f, 20000f);
        else if (_cam2D != null)
        {
            float factor = delta > 0 ? 0.85f : 1f / 0.85f;
            _cam2D.ZoomAt(e.X, e.Y, Width, Height, factor);
            Invalidate();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _heldKeys.Add(e.KeyCode);
        ActiveTool?.OnKeyDown(this, e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _heldKeys.Remove(e.KeyCode);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || Document == null) return;
        var hit = HitTestActor(e.X, e.Y);
        if (hit != null) { ActorDoubleClicked?.Invoke(hit); return; }
        // #6: a path waypoint under the cursor → let the host switch to the Path tool + select it.
        if (HitTestPathNode(e.X, e.Y, out int ppi, out int ppt)) { PathNodeDoubleClicked?.Invoke(ppi, ppt); return; }
        // A brush under the cursor (already selected by this double-click's first press) → open its warp/brush
        // properties pop-out. The Select tool clears the selection when the press lands on empty space, so a
        // stale selection can't trigger this on an empty double-click.
        if (ActiveTool is Tools.SelectTool && Document.SelectedSolid is { } sol)
        { SolidDoubleClicked?.Invoke(sol); return; }
        ActiveTool?.OnDoubleClick(this, e);   // e.g. Path tool → waypoint/path properties
    }

    // True if a path waypoint is under (sx,sy) in a 2D view (the planes where paths are edited).
    // Pot (Obj_Tsubo) "Drop contents" index → the En_Item00 type whose gameplay_keep model previews it
    // (rupees by colour, recovery heart). Bombs/arrows/magic/deku drops are 2D billboards in vanilla with no
    // standalone model, so they get no floating preview (-1). Mirrors FreestandingItemModel's coverage.
    // A pot's drop (Obj_Tsubo params & 0x1F) → the En_Item00 type whose 3D gameplay_keep model the editor can
    // render above the pot. -1 = no 3D model (billboard-only drop, or FLEXIBLE handled separately).
    private static int PotDropItem00(int drop) => drop switch
    {
        0x00 => 0x00, 0x01 => 0x01, 0x02 => 0x02,   // green / blue / red rupee
        0x13 => 0x13, 0x14 => 0x14,                  // orange (200) / purple (50) rupee
        0x03 or 0x0D => 0x03,                         // recovery heart (0x0D flexible-heart also → heart)
        0x06 => 0x06,                                 // heart piece
        0x07 => 0x07,                                 // heart container
        _ => -1,
    };

    private bool HitTestPathNode(int sx, int sy, out int pathIdx, out int ptIdx)
    {
        pathIdx = ptIdx = -1;
        if (Document == null) return false;

        // 3D view: ray-cast against each waypoint's ±16-unit box (matches the boxes SolidRenderer draws),
        // nearest hit wins — so a path node can be double-clicked in the 3D viewport just like an actor.
        if (ViewportType == ViewportType.Perspective3D && _cam3D != null)
        {
            var ray = Picking.RayFromScreen(_cam3D, sx, sy, Width, Height);
            var paths3 = Document.Scene.Paths;
            float bestT = float.MaxValue;
            var half = new OpenTK.Mathematics.Vector3(16, 16, 16);
            for (int pi = 0; pi < paths3.Count; pi++)
                for (int j = 0; j < paths3[pi].Points.Count; j++)
                {
                    var p = paths3[pi].Points[j];
                    if (Picking.RayAabb(ray, p - half, p + half, out float t) && t < bestT)
                    { bestT = t; pathIdx = pi; ptIdx = j; }
                }
            return pathIdx >= 0;
        }

        if (_cam2D == null) return false;
        var paths = Document.Scene.Paths;
        float oh = _cam2D.PanX + (sx - Width * 0.5f) * _cam2D.Zoom;
        float ov = _cam2D.PanY - (sy - Height * 0.5f) * _cam2D.Zoom;
        float r = MathF.Max(10f * _cam2D.Zoom, 8f);   // generous pick radius
        for (int pi = 0; pi < paths.Count; pi++)
            for (int j = 0; j < paths[pi].Points.Count; j++)
            {
                var p = paths[pi].Points[j];
                var (ch, cv) = _cam2D.Axis switch
                {
                    ViewAxis.Top   => (p.X, -p.Z),
                    ViewAxis.Front => (p.X,  p.Y),
                    ViewAxis.Side  => (p.Z,  p.Y),
                    _              => (0f, 0f),
                };
                if (MathF.Abs(oh - ch) <= r && MathF.Abs(ov - cv) <= r) { pathIdx = pi; ptIdx = j; return true; }
            }
        return false;
    }

    // Picks the actor under the cursor using the SAME model-footprint AABB test as single-click
    // selection (SelectTool), so double-click is consistent with what a click selects — the previous
    // point-distance test missed actors whose model is large but whose origin is far from the cursor,
    // which made opening properties by double-click flaky.
    private ZActor? HitTestActor(int sx, int sy)
    {
        if (Document == null) return null;

        if (ViewportType != ViewportType.Perspective3D && _cam2D != null)
        {
            float oh = _cam2D.PanX + (sx - Width * 0.5f) * _cam2D.Zoom;
            float ov = _cam2D.PanY - (sy - Height * 0.5f) * _cam2D.Zoom;
            float minHalf = MathF.Max(Picking.DefaultActorHalf, 8f * _cam2D.Zoom);
            ZActor? hit = null; float bestArea = float.MaxValue;
            foreach (var a in Document.AllActors)
            {
                var (mn, mx) = Picking.ActorBounds(a, Resolver, true);
                var (sh, sv, eh, ev) = AxisRect(mn, mx, _cam2D.Axis);
                var (ch, cv) = _cam2D.Axis switch
                {
                    ViewAxis.Top   => (a.XPos, -a.ZPos),
                    ViewAxis.Front => (a.XPos,  a.YPos),
                    ViewAxis.Side  => (a.ZPos,  a.YPos),
                    _              => (0f, 0f),
                };
                if (eh - sh < 2 * minHalf) { sh = MathF.Min(sh, ch - minHalf); eh = MathF.Max(eh, ch + minHalf); }
                if (ev - sv < 2 * minHalf) { sv = MathF.Min(sv, cv - minHalf); ev = MathF.Max(ev, cv + minHalf); }
                if (oh >= sh && oh <= eh && ov >= sv && ov <= ev)
                {
                    float area = (eh - sh) * (ev - sv);
                    if (area < bestArea) { bestArea = area; hit = a; }
                }
            }
            return hit;
        }

        if (ViewportType == ViewportType.Perspective3D && _cam3D != null)
        {
            var ray = Picking.RayFromScreen(_cam3D, sx, sy, Width, Height);
            return Picking.PickActor(Document.AllActors, ray, Resolver, adult: true);
        }
        return null;
    }

    // Projects a world AABB into 2D ortho (h,v) extents for the given view axis.
    private static (float sh, float sv, float eh, float ev) AxisRect(
        OpenTK.Mathematics.Vector3 mn, OpenTK.Mathematics.Vector3 mx, ViewAxis axis) => axis switch
    {
        ViewAxis.Top   => (mn.X, -mx.Z, mx.X, -mn.Z),
        ViewAxis.Front => (mn.X,  mn.Y, mx.X,  mx.Y),
        ViewAxis.Side  => (mn.Z,  mn.Y, mx.Z,  mx.Y),
        _              => (0f, 0f, 0f, 0f),
    };

    public void Tick(float dt)
    {
        if (!_initialized || ViewportType != ViewportType.Perspective3D || _cam3D == null) return;
        bool flying = _rightMouseDown || _mouseLook;
        if (!flying && !_heldKeys.Overlaps([Keys.W, Keys.A, Keys.S, Keys.D])) return;

        // Hammer-style modifier speed: Shift = fast, Ctrl = slow, Alt = very fine. They multiply,
        // so Shift+Ctrl cancels toward 1×. Applied by scaling the frame delta the camera integrates.
        var mods = Control.ModifierKeys;
        float mult = 1f;
        if ((mods & Keys.Shift)   != 0) mult *= 4f;
        if ((mods & Keys.Control) != 0) mult *= 0.25f;
        if ((mods & Keys.Alt)     != 0) mult *= 0.1f;
        dt *= mult;

        bool moved = false;
        if (_heldKeys.Contains(Keys.W)) { _cam3D.MoveForward(dt);  moved = true; }
        if (_heldKeys.Contains(Keys.S)) { _cam3D.MoveBack(dt);     moved = true; }
        if (_heldKeys.Contains(Keys.A)) { _cam3D.MoveLeft(dt);     moved = true; }
        if (_heldKeys.Contains(Keys.D)) { _cam3D.MoveRight(dt);    moved = true; }
        // Q/E up/down only while flying (otherwise Q = Select, E = Entity tool at form level)
        if (flying)
        {
            if (_heldKeys.Contains(Keys.Q)) { _cam3D.MoveDown(dt); moved = true; }
            if (_heldKeys.Contains(Keys.E)) { _cam3D.MoveUp(dt);   moved = true; }
        }
        if (moved) Invalidate();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_initialized)
        {
            _gl.MakeCurrent();
            _grid?.Dispose();
            _solidRenderer?.Dispose();
            _actorRenderer?.Dispose();
            _importedRenderer?.Dispose();
            _billboards?.Dispose();
            _sky?.Dispose();
            _text?.Dispose();
            _gl.ReleaseCurrent();
        }
        _gl.Dispose();
        base.OnHandleDestroyed(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
}
