using MegatonHammer.Editor;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MegatonHammer.Rendering;

public sealed class SolidRenderer : IDisposable
{
    // ── Face shader: per-vertex position, normal, color with flat diffuse shading ──
    // Shared GLSL fragment snippet: scene environment lighting + distance fog.
    private const string EnvUniforms = @"
uniform vec3 uAmbient; uniform vec3 uL1Dir; uniform vec3 uL1Col;
uniform vec3 uL2Dir; uniform vec3 uL2Col;
uniform vec3 uFogColor; uniform vec3 uCamPos;
uniform float uFogNear; uniform float uFogFar;
vec3 applyEnv(vec3 baseCol, vec3 n, vec3 worldPos) {
    vec3 light = uAmbient
        + uL1Col * max(0.0, dot(n, normalize(uL1Dir)))
        + uL2Col * max(0.0, dot(n, normalize(uL2Dir)));
    vec3 col = baseCol * light;
    float dist = length(worldPos - uCamPos);
    float fog = clamp((dist - uFogNear) / max(1.0, uFogFar - uFogNear), 0.0, 1.0);
    return mix(col, uFogColor, fog);
}";

    private static readonly string FaceVert = @"
#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec3 aColor;
uniform mat4 uMVP;
out vec3 vNormal;
out vec3 vColor;
out vec3 vWorld;
void main() {
    gl_Position = uMVP * vec4(aPos, 1.0);
    vNormal = aNormal;
    vColor  = aColor;
    vWorld  = aPos;
}";

    private static readonly string FaceFrag = @"
#version 330 core
in vec3 vNormal;
in vec3 vColor;
in vec3 vWorld;
out vec4 fragColor;
" + EnvUniforms + @"
void main() {
    fragColor = vec4(applyEnv(vColor, normalize(vNormal), vWorld), 1.0);
}";

    // ── Line shader: position only, uniform color ──────────────────────────────
    private static readonly string LineVert = @"
#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uMVP;
void main() { gl_Position = uMVP * vec4(aPos, 1.0); }";

    private static readonly string LineFrag = @"
#version 330 core
out vec4 fragColor;
uniform vec4 uColor;
void main() { fragColor = uColor; }";

    // ── Textured face shader: position, normal, uv; samples a 2D texture ─────────
    private static readonly string TexVert = @"
#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUV;
layout(location=3) in vec3 aColor;
uniform mat4 uMVP;
uniform vec2 uUVScroll;   // brush-authored animated-texture scroll (UV units, already time-scaled)
out vec3 vNormal;
out vec2 vUV;
out vec3 vColor;
out vec3 vWorld;
void main() {
    gl_Position = uMVP * vec4(aPos, 1.0);
    vNormal = aNormal;
    vUV     = aUV + uUVScroll;
    vColor  = aColor;
    vWorld  = aPos;
}";

    // #8: the fragment shader applies a 1-bit alpha cutout (discard texels with alpha < 0.5) so
    // transparent textures (foliage/tree/grate, ia16/RGBA16) don't draw opaque, matching the N64 5551
    // alpha / OTR export. Opaque wall textures (alpha 1.0) are unaffected. NOTE: the GLSL source below
    // is kept STRICTLY ASCII — a non-ASCII byte (e.g. an em-dash) in a shader comment made some GL
    // drivers' preprocessor swallow the rest of the shader, failing compile with "unexpected $end".
    private static readonly string TexFrag = @"
#version 330 core
in vec3 vNormal;
in vec2 vUV;
in vec3 vColor;
in vec3 vWorld;
out vec4 fragColor;
uniform sampler2D uTex;
uniform float uAlpha;   // 1.0 = opaque (cutout-tested); < 1.0 = translucent surface (water), no cutout
" + EnvUniforms + @"
void main() {
    vec4 tex = texture(uTex, vUV);
    if (uAlpha >= 0.999 && tex.a < 0.5) discard;
    fragColor = vec4(applyEnv(tex.rgb * vColor, normalize(vNormal), vWorld), uAlpha);
}";

    /// <summary>Seconds elapsed, driving brush-authored animated-texture (scroll) UV offsets. Set per frame.</summary>
    public float AnimTime;

    // ── Water-surface preview (matches the WATERBOX export: surface-only, translucent, scrolling) ──
    private const string WaterTexKey = "__water__";   // GL cache key for the real OoT water texture
    private const float WaterAlpha   = 0.62f;          // translucency in the preview (export uses prim α128)
    private const float WaterScrollU = 0.06f, WaterScrollV = 0.045f;   // gentle drift, tiles/sec

    // A brush is water if flagged IsWater OR painted with the WATERBOX special texture (mirrors the export).
    private static bool IsWaterBrush(Solid s) =>
        s.IsWater || s.Faces.Any(f => Textures.SpecialTextures.Classify(f.TextureName).HasFlag(Textures.SpecialKind.WaterSurface));

    private readonly Shader _faceShader;
    private readonly Shader _lineShader;
    private readonly Shader _texShader;

    private readonly int _faceVao, _faceVbo;  // 9 floats/vertex: pos+normal+color
    private readonly int _lineVao, _lineVbo;  // 3 floats/vertex: pos only
    private readonly int _texVao,  _texVbo;   // 8 floats/vertex: pos+normal+uv

    // GL texture handles by texture name (this renderer's GL context).
    private readonly Dictionary<string, int> _glTex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source of pixel data for textured faces (set by the owning viewport).</summary>
    public Textures.TextureLibrary? Library { get; set; }

    private static readonly Vector3 DefaultFaceColor  = new(0.50f, 0.50f, 0.52f);
    private static readonly Vector3 SelectedFaceColor = new(0.90f, 0.45f, 0.10f);
    private static readonly Vector4 DefaultEdgeColor  = new(0.45f, 0.45f, 0.50f, 1f);  // Hammer-style darker wireframe
    private static readonly Vector4 SelectedEdgeColor = new(1.00f, 0.55f, 0.00f, 1f);
    private static readonly Vector4 TriggerEdgeColor  = new(0.20f, 1.00f, 0.55f, 1f);  // warp/trigger zones
    private static readonly Vector4 RubberBandColor   = new(1.00f, 1.00f, 0.00f, 1f);

    private bool _disposed;

    public SolidRenderer()
    {
        _faceShader = new Shader(FaceVert, FaceFrag);
        _lineShader = new Shader(LineVert, LineFrag);

        // Face VAO: pos(3f) + normal(3f) + color(3f) = 9 floats, stride=36
        _faceVao = GL.GenVertexArray();
        _faceVbo = GL.GenBuffer();
        GL.BindVertexArray(_faceVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _faceVbo);
        const int faceStride = 9 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, faceStride, 0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, faceStride, 3 * sizeof(float));
        GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, faceStride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.EnableVertexAttribArray(2);
        GL.BindVertexArray(0);

        // Line VAO: pos(3f) = 3 floats, stride=12
        _lineVao = GL.GenVertexArray();
        _lineVbo = GL.GenBuffer();
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);

        // Textured VAO: pos(3f) + normal(3f) + uv(2f) = 8 floats, stride=32
        _texShader = new Shader(TexVert, TexFrag);
        _texVao = GL.GenVertexArray();
        _texVbo = GL.GenBuffer();
        GL.BindVertexArray(_texVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _texVbo);
        const int texStride = 11 * sizeof(float);   // pos(3) + normal(3) + uv(2) + color(3)
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, texStride, 0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, texStride, 3 * sizeof(float));
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, texStride, 6 * sizeof(float));
        GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, texStride, 8 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.EnableVertexAttribArray(2);
        GL.BindVertexArray(0);
    }

    // ── Texture upload / lookup (lazy, this GL context) ───────────────────────

    private int GetGlTexture(string name)
    {
        if (_glTex.TryGetValue(name, out int handle)) return handle;

        System.Drawing.Bitmap bmp;
        if (name == WaterTexKey)
            bmp = Textures.WaterTexture.Resolve();   // real gLakeHyliaWaterTex (or procedural fallback)
        else
        {
            var entry = Library?.Find(name);
            if (entry == null) { _glTex[name] = 0; return 0; }
            bmp = entry.Image;
        }

        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);

        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                      bmp.Width, bmp.Height, 0,
                      PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
        bmp.UnlockBits(data);

        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        TextureFilter.ApplyToBound();
        GL.BindTexture(TextureTarget.Texture2D, 0);

        _glTex[name] = tex;
        return tex;
    }

    // Re-applies the texture filter to cached textures after the trilinear toggle changes.
    private int _filterEpoch = -1;
    private void RefreshFiltersIfNeeded()
    {
        if (_filterEpoch == ViewOptions.FilterEpoch) return;
        _filterEpoch = ViewOptions.FilterEpoch;
        foreach (var h in _glTex.Values)
        {
            if (h == 0) continue;
            GL.BindTexture(TextureTarget.Texture2D, h);
            TextureFilter.ApplyToBound();
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    // Pushes scene environment lighting + fog into a shader (call after Use()).
    private static void SetEnv(Shader s, ZScene scene, Camera3D cam)
    {
        var st = scene.Settings;
        // Lighting method 1 (the pre-#9 look): render brush faces full-bright (ambient white, no
        // directional, no fog) so the editor view matches the full-bright export. Method 2 uses the
        // scene's environment lighting. (Imported geometry keeps its own baked vertex colours either way.)
        if (EditorSettings.LightingMethod < 2)
        {
            s.SetVector3("uAmbient", Vector3.One);
            s.SetVector3("uL1Dir", new Vector3(0, 1, 0)); s.SetVector3("uL1Col", Vector3.Zero);
            s.SetVector3("uL2Dir", new Vector3(0, -1, 0)); s.SetVector3("uL2Col", Vector3.Zero);
            s.SetVector3("uFogColor", Rgb(st.FogColor)); s.SetVector3("uCamPos", cam.Position);
            s.SetFloat("uFogNear", 1e9f); s.SetFloat("uFogFar", 2e9f);
            return;
        }
        s.SetVector3("uAmbient", Rgb(st.Ambient));
        s.SetVector3("uL1Dir", new Vector3(st.Light1DirX, st.Light1DirY, st.Light1DirZ));
        s.SetVector3("uL1Col", Rgb(st.Light1Col));
        s.SetVector3("uL2Dir", new Vector3(st.Light2DirX, st.Light2DirY, st.Light2DirZ));
        s.SetVector3("uL2Col", Rgb(st.Light2Col));
        s.SetVector3("uFogColor", Rgb(st.FogColor));
        s.SetVector3("uCamPos", cam.Position);
        // The N64 fog near/far units don't map to world space; use scene-scale distances
        // so only distant geometry takes the fog tint.
        s.SetFloat("uFogNear", 3000f);
        s.SetFloat("uFogFar", 50000f);
    }

    // Neutral lighting for the (legacy) flat solid-list overload.
    private static void SetEnvDefault(Shader s, Camera3D cam)
    {
        s.SetVector3("uAmbient", new Vector3(0.45f));
        s.SetVector3("uL1Dir", new Vector3(0.5f, 1f, 0.3f));
        s.SetVector3("uL1Col", new Vector3(0.7f));
        s.SetVector3("uL2Dir", new Vector3(-0.5f, -0.4f, -0.3f));
        s.SetVector3("uL2Col", new Vector3(0.15f));
        s.SetVector3("uFogColor", new Vector3(0.12f));
        s.SetVector3("uCamPos", cam.Position);
        s.SetFloat("uFogNear", 1e9f);
        s.SetFloat("uFogFar", 2e9f);
    }

    private static Vector3 Rgb(Editor.RgbColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    // Planar UV from the face's dominant normal axis (Hammer-style projection).
    private static Vector2 PlanarUV(Vector3 p, Vector3 n, float scale)
    {
        if (scale < 1e-3f) scale = 64f;
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        Vector2 uv = (ax >= ay && ax >= az) ? new(p.Z, p.Y)   // X-facing
                   : (ay >= ax && ay >= az) ? new(p.X, p.Z)   // Y-facing
                                            : new(p.X, p.Y);  // Z-facing
        return uv / scale;
    }

    /// <summary>Visgroup visibility predicate (set by the viewport): a solid in a hidden visgroup is
    /// skipped by the scene render paths. Null = everything visible.</summary>
    public Func<Solid, bool>? Hidden { get; set; }

    // ── 3D perspective rendering ───────────────────────────────────────────────

    public void Render3D(IReadOnlyList<Solid> solids, Camera3D cam, int w, int h)
    {
        if (solids.Count == 0) return;

        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);

        // Draw filled faces with polygon offset so edges appear on top
        var faceVerts = new List<float>(solids.Count * 24 * 9);
        foreach (var solid in solids)
        {
            var faceCol = solid.IsSelected ? SelectedFaceColor : DefaultFaceColor;
            foreach (var face in solid.Faces)
            {
                var verts = face.Vertices;
                if (verts.Count < 3) continue;
                var n = face.Plane.Normal;
                for (int i = 1; i < verts.Count - 1; i++)
                {
                    PushFaceVert(faceVerts, verts[0],   n, faceCol);
                    PushFaceVert(faceVerts, verts[i],   n, faceCol);
                    PushFaceVert(faceVerts, verts[i+1], n, faceCol);
                }
            }
        }

        if (faceVerts.Count > 0)
        {
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.PolygonOffsetFill);
            GL.PolygonOffset(1f, 1f);

            _faceShader.Use();
            _faceShader.SetMatrix4("uMVP", mvp);
            SetEnvDefault(_faceShader, cam);
            UploadDrawFaces(faceVerts);

            GL.Disable(EnableCap.PolygonOffsetFill);
        }

        // Wireframe edges. In the TEXTURED 3D view, sdk2013 Hammer outlines only SELECTED brushes
        // (and invisible trigger volumes) — drawing every brush's edges turned every face boundary
        // into a visible grey "seam" across the textures. Unselected solid brushes draw textured only.
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", mvp);
        GL.DepthFunc(DepthFunction.Lequal);

        foreach (var solid in solids)
        {
            if (!solid.IsSelected && !solid.IsTrigger) continue;
            var edgeCol = solid.IsSelected ? SelectedEdgeColor : TriggerEdgeColor;
            var pts = BuildEdges3D(solid);
            if (pts.Count == 0) continue;
            _lineShader.SetVector4("uColor", edgeCol);
            UploadDrawLines(pts);
        }

        // Face Edit: outline selected faces in bright yellow so the user sees the selection.
        var faceSel = new List<float>();
        foreach (var solid in solids)
            foreach (var face in solid.Faces)
                if (face.FaceSelected)
                {
                    var v = face.Vertices;
                    for (int i = 0; i < v.Count; i++)
                    { var a = v[i]; var b = v[(i + 1) % v.Count]; faceSel.AddRange([a.X,a.Y,a.Z, b.X,b.Y,b.Z]); }
                }
        if (faceSel.Count > 0)
        {
            GL.DepthFunc(DepthFunction.Always);
            GL.LineWidth(2f);
            _lineShader.SetVector4("uColor", new Vector4(1f, 1f, 0.1f, 1f));
            UploadDrawLines(faceSel);
            GL.LineWidth(1f);
        }

        GL.DepthFunc(DepthFunction.Less);
    }

    private static List<float> BuildEdges3D(Solid solid)
    {
        var pts = new List<float>();
        foreach (var face in solid.Faces)
        {
            var verts = face.Vertices;
            for (int i = 0; i < verts.Count; i++)
            {
                var a = verts[i]; var b = verts[(i + 1) % verts.Count];
                pts.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z]);
            }
        }
        return pts;
    }

    // ── 2D orthographic rendering (wireframe only) ────────────────────────────

    public void Render2D(IReadOnlyList<Solid> solids, Camera2D cam, int w, int h)
    {
        if (solids.Count == 0) return;

        var mvp = cam.GetProjectionMatrix(w, h);
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", mvp);

        foreach (var solid in solids)
        {
            var edgeCol = solid.IsSelected ? SelectedEdgeColor : solid.IsTrigger ? TriggerEdgeColor : DefaultEdgeColor;
            var pts = BuildEdges2D(solid, cam.Axis);
            if (pts.Count == 0) continue;
            _lineShader.SetVector4("uColor", edgeCol);
            UploadDrawLines(pts);
        }
    }

    private static List<float> BuildEdges2D(Solid solid, ViewAxis axis)
    {
        var pts = new List<float>();
        foreach (var face in solid.Faces)
        {
            var verts = face.Vertices;
            for (int i = 0; i < verts.Count; i++)
            {
                var a2 = WorldToOrtho(verts[i],                     axis);
                var b2 = WorldToOrtho(verts[(i + 1) % verts.Count], axis);
                pts.AddRange([a2.X, a2.Y, 0f, b2.X, b2.Y, 0f]);
            }
        }
        return pts;
    }

    // ── Scene-aware overloads (room-coloured faces) ───────────────────────

    public void Render3D(ZScene scene, Camera3D cam, int w, int h)
    {
        if (scene.Rooms.Count == 0) return;
        RefreshFiltersIfNeeded();
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);

        // Untextured (flat-coloured) faces; textured faces grouped by texture name. Selected solids
        // keep their real texture/colour — selection is shown as a translucent orange TINT overlay
        // (selOverlay) plus the wireframe outline, rather than a flat fill that hides the texture.
        var faceVerts  = new List<float>();
        var texBatches = new Dictionary<string, List<float>>(StringComparer.OrdinalIgnoreCase);
        var selOverlay = new List<float>();
        var waterVerts = new List<float>();   // water brushes' surface (translucent, scrolling — drawn last)
        var white = Vector3.One;

        foreach (var room in scene.Rooms.Where(r => r.Visible))   // room-tree eye toggle hides a room's brushes
        {
            foreach (var solid in room.Geometry)
            {
                if (Hidden?.Invoke(solid) == true) continue;   // visgroup hidden
                if (solid.IsTrigger) continue;   // trigger volumes draw as wireframe only (below)
                bool waterSolid = IsWaterBrush(solid);
                foreach (var face in solid.Faces)
                {
                    if (face.Vertices.Count < 3) continue;
                    var n = face.Plane.Normal;

                    // Water brushes render their up-facing SURFACE as translucent scrolling water (matching
                    // the export). Their SIDE/BOTTOM faces fall through to normal textured rendering below so
                    // the WATERBOX tool swatch shows the brush's vertical depth — EDITOR-ONLY (the export
                    // draws only the surface, so these faces never appear in-game).
                    if (waterSolid && n.Y >= 0.7f)
                    {
                        for (int i = 1; i < face.Vertices.Count - 1; i++)
                        {
                            PushTexVert(waterVerts, face.Vertices[0],   n, face, white);
                            PushTexVert(waterVerts, face.Vertices[i],   n, face, white);
                            PushTexVert(waterVerts, face.Vertices[i+1], n, face, white);
                        }
                        if (solid.IsSelected)
                            for (int i = 1; i < face.Vertices.Count - 1; i++)
                            {
                                var a = face.Vertices[0]; var b = face.Vertices[i]; var c = face.Vertices[i + 1];
                                selOverlay.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z]);
                            }
                        continue;
                    }

                    bool textured = Library != null &&
                                    face.TextureName != null && Library.Find(face.TextureName) != null;

                    // A shade-painted quad renders its dense grid (local spray) instead of the flat corners.
                    bool useGrid = face.ShadePaint is { } sg && face.Vertices.Count == 4
                                   && sg.Colors.Length == (sg.Nu + 1) * (sg.Nv + 1);
                    if (textured)
                    {
                        if (!texBatches.TryGetValue(face.TextureName!, out var buf))
                            texBatches[face.TextureName!] = buf = [];
                        if (useGrid) EmitShadeGrid(face, face.ShadePaint!, n, buf, null);
                        else for (int i = 1; i < face.Vertices.Count - 1; i++)
                        {
                            // Per-vertex shade modulates the texture (white = unpainted, no change).
                            PushTexVert(buf, face.Vertices[0],   n, face, face.ColorAt(0,   white));
                            PushTexVert(buf, face.Vertices[i],   n, face, face.ColorAt(i,   white));
                            PushTexVert(buf, face.Vertices[i+1], n, face, face.ColorAt(i+1, white));
                        }
                    }
                    else
                    {
                        if (useGrid) EmitShadeGrid(face, face.ShadePaint!, n, null, faceVerts);
                        else for (int i = 1; i < face.Vertices.Count - 1; i++)
                        {
                            // Per-vertex shade (painted) overrides the flat room colour.
                            PushFaceVert(faceVerts, face.Vertices[0],   n, face.ColorAt(0,   room.Color));
                            PushFaceVert(faceVerts, face.Vertices[i],   n, face.ColorAt(i,   room.Color));
                            PushFaceVert(faceVerts, face.Vertices[i+1], n, face.ColorAt(i+1, room.Color));
                        }
                    }

                    if (solid.IsSelected)
                        for (int i = 1; i < face.Vertices.Count - 1; i++)
                        {
                            var a = face.Vertices[0]; var b = face.Vertices[i]; var c = face.Vertices[i + 1];
                            selOverlay.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z]);
                        }
                }
            }
        }

        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(1f, 1f);

        if (faceVerts.Count > 0)
        {
            _faceShader.Use();
            _faceShader.SetMatrix4("uMVP", mvp);
            SetEnv(_faceShader, scene, cam);
            UploadDrawFaces(faceVerts);
        }

        if (texBatches.Count > 0)
        {
            _texShader.Use();
            _texShader.SetMatrix4("uMVP", mvp);
            SetEnv(_texShader, scene, cam);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Uniform1(GL.GetUniformLocation(_texShader.Handle, "uTex"), 0);
            GL.Uniform1(GL.GetUniformLocation(_texShader.Handle, "uAlpha"), 1.0f);   // opaque (cutout-tested)
            int scrollLoc = GL.GetUniformLocation(_texShader.Handle, "uUVScroll");
            // Brush-authored scroll: a texture marked animated in this scene scrolls its faces over time.
            var scrolls = scene.Settings.TextureScrolls;
            foreach (var (name, buf) in texBatches)
            {
                if (buf.Count == 0) continue;
                var sc = scrolls.FirstOrDefault(t => t.Name == name);
                if (sc != null) { float fx = sc.U * AnimTime, fy = sc.V * AnimTime; GL.Uniform2(scrollLoc, fx - MathF.Floor(fx), fy - MathF.Floor(fy)); }
                else GL.Uniform2(scrollLoc, 0f, 0f);
                int tex = GetGlTexture(name);
                GL.BindTexture(TextureTarget.Texture2D, tex);
                UploadDrawTexFaces(buf);
            }
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        // Water surfaces: translucent, scrolling, drawn AFTER the opaque geometry so they blend over the
        // pool floor. Depth test on (occluded by walls in front) but depth-write off (doesn't hide actors
        // or the floor beneath). Matches the WATERBOX export's XLU surface.
        if (waterVerts.Count > 0)
        {
            _texShader.Use();
            _texShader.SetMatrix4("uMVP", mvp);
            SetEnv(_texShader, scene, cam);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Uniform1(GL.GetUniformLocation(_texShader.Handle, "uTex"), 0);
            GL.Uniform1(GL.GetUniformLocation(_texShader.Handle, "uAlpha"), WaterAlpha);
            float fx = WaterScrollU * AnimTime, fy = WaterScrollV * AnimTime;
            GL.Uniform2(GL.GetUniformLocation(_texShader.Handle, "uUVScroll"), fx - MathF.Floor(fx), fy - MathF.Floor(fy));

            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            GL.BindTexture(TextureTarget.Texture2D, GetGlTexture(WaterTexKey));
            UploadDrawTexFaces(waterVerts);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        // Selection tint: a translucent orange wash over selected faces (keeps the texture readable).
        if (selOverlay.Count > 0)
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _lineShader.Use();
            _lineShader.SetMatrix4("uMVP", mvp);
            _lineShader.SetVector4("uColor", new Vector4(1.0f, 0.55f, 0.05f, 0.33f));
            UploadDrawTriangles(selOverlay);
            GL.Disable(EnableCap.Blend);
        }

        GL.Disable(EnableCap.PolygonOffsetFill);

        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", mvp);
        GL.DepthFunc(DepthFunction.Lequal);
        foreach (var room in scene.Rooms.Where(r => r.Visible))   // room-tree eye toggle hides a room's brushes
            foreach (var solid in room.Geometry)
            {
                if (Hidden?.Invoke(solid) == true) continue;   // visgroup hidden
                // Only SELECTED brushes (and invisible trigger volumes) get outlined — drawing every
                // brush's edges turns every face boundary into a visible grey "seam" across the
                // textures. (The solid-color Render3D overload already does this; the textured scene
                // path must too.) Unselected solid brushes draw textured-only.
                if (!solid.IsSelected && !solid.IsTrigger) continue;
                var ec = solid.IsSelected ? SelectedEdgeColor : TriggerEdgeColor;
                var pts = BuildEdges3D(solid);
                if (pts.Count == 0) continue;
                _lineShader.SetVector4("uColor", ec);
                UploadDrawLines(pts);
            }

        // Face Edit: highlight selected faces with a translucent yellow fill + bright outline, drawn
        // on top (depth Always) so the selection is clearly visible while the Face Edit sheet is open.
        var hlFill = new List<float>();
        var hlEdge = new List<float>();
        foreach (var room in scene.Rooms.Where(r => r.Visible))   // room-tree eye toggle hides a room's brushes
            foreach (var solid in room.Geometry)
                foreach (var face in solid.Faces)
                    if (face.FaceSelected)
                    {
                        var v = face.Vertices;
                        for (int i = 1; i + 1 < v.Count; i++)
                            hlFill.AddRange([v[0].X, v[0].Y, v[0].Z, v[i].X, v[i].Y, v[i].Z, v[i + 1].X, v[i + 1].Y, v[i + 1].Z]);
                        for (int i = 0; i < v.Count; i++)
                        { var a = v[i]; var b = v[(i + 1) % v.Count]; hlEdge.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z]); }
                    }
        if (hlEdge.Count > 0)
        {
            GL.DepthFunc(DepthFunction.Always);
            if (hlFill.Count > 0)
            {
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                _lineShader.SetVector4("uColor", new Vector4(1f, 0.92f, 0.1f, 0.35f));
                UploadDrawTris(hlFill);
                GL.Disable(EnableCap.Blend);
            }
            GL.LineWidth(2.5f);
            _lineShader.SetVector4("uColor", new Vector4(1f, 1f, 0.15f, 1f));
            UploadDrawLines(hlEdge);
            GL.LineWidth(1f);
        }

        GL.DepthFunc(DepthFunction.Less);
    }

    public void Render2D(ZScene scene, Camera2D cam, int w, int h)
    {
        if (scene.Rooms.Count == 0) return;
        var mvp = cam.GetProjectionMatrix(w, h);
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", mvp);
        float r = 5f * cam.Zoom;   // brush-centre X half-size, constant in screen pixels

        // Selected brushes: a faint translucent orange wash inside their 2D bounding box so the selection
        // is easy to spot at a glance (it was just a same-colour outline before). Drawn under the wireframe.
        var selFill = new List<float>();
        foreach (var room in scene.Rooms.Where(r => r.Visible))   // room-tree eye toggle hides a room's brushes
            foreach (var solid in room.Geometry)
            {
                if (!solid.IsSelected || Hidden?.Invoke(solid) == true) continue;
                var (mn, mx) = solid.GetAABB();
                var a = WorldToOrtho(mn, cam.Axis); var b = WorldToOrtho(mx, cam.Axis);
                float l = MathF.Min(a.X, b.X), rr = MathF.Max(a.X, b.X), bo = MathF.Min(a.Y, b.Y), t = MathF.Max(a.Y, b.Y);
                selFill.AddRange([l, bo, 0f, rr, bo, 0f, rr, t, 0f,  l, bo, 0f, rr, t, 0f, l, t, 0f]);
            }
        if (selFill.Count > 0)
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _lineShader.SetVector4("uColor", new Vector4(1.0f, 0.55f, 0.05f, 0.18f));
            UploadDrawTris(selFill);
            GL.Disable(EnableCap.Blend);
        }

        foreach (var room in scene.Rooms.Where(r => r.Visible))   // room-tree eye toggle hides a room's brushes
            foreach (var solid in room.Geometry)
            {
                if (Hidden?.Invoke(solid) == true) continue;   // visgroup hidden
                var ec  = solid.IsSelected ? SelectedEdgeColor : solid.IsTrigger ? TriggerEdgeColor : DefaultEdgeColor;
                var pts = BuildEdges2D(solid, cam.Axis);
                if (pts.Count == 0) continue;

                // Hammer marks each brush's centre with a small X in the 2D views.
                var (mn, mx) = solid.GetAABB();
                var c = WorldToOrtho((mn + mx) * 0.5f, cam.Axis);
                pts.AddRange([c.X - r, c.Y - r, 0f, c.X + r, c.Y + r, 0f,
                              c.X - r, c.Y + r, 0f, c.X + r, c.Y - r, 0f]);

                _lineShader.SetVector4("uColor", ec);
                UploadDrawLines(pts);
            }
    }

    // ── Rubber-band rectangle (drawn during brush drag) ───────────────────────

    public void DrawRubberBand2D(float h1, float v1, float h2, float v2, Camera2D cam, int w, int h)
    {
        float[] pts =
        [
            h1, v1, 0f,  h2, v1, 0f,
            h2, v1, 0f,  h2, v2, 0f,
            h2, v2, 0f,  h1, v2, 0f,
            h1, v2, 0f,  h1, v1, 0f,
        ];
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", cam.GetProjectionMatrix(w, h));
        _lineShader.SetVector4("uColor", RubberBandColor);

        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, pts.Length * sizeof(float), pts, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Lines, 0, pts.Length / 3);
        GL.BindVertexArray(0);
    }

    /// <summary>Wireframe box preview in the 3D view (the pending brush box, before Enter).</summary>
    public void DrawWireBox3D(Vector3 mn, Vector3 mx, Camera3D cam, int w, int h)
    {
        Vector3[] c =
        [
            new(mn.X, mn.Y, mn.Z), new(mx.X, mn.Y, mn.Z), new(mx.X, mn.Y, mx.Z), new(mn.X, mn.Y, mx.Z),
            new(mn.X, mx.Y, mn.Z), new(mx.X, mx.Y, mn.Z), new(mx.X, mx.Y, mx.Z), new(mn.X, mx.Y, mx.Z),
        ];
        int[,] edges = { {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4}, {0,4},{1,5},{2,6},{3,7} };
        var pts = new List<float>(72);
        for (int i = 0; i < 12; i++)
        {
            var a = c[edges[i, 0]]; var b = c[edges[i, 1]];
            pts.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z]);
        }
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h));
        _lineShader.SetVector4("uColor", RubberBandColor);
        GL.Disable(EnableCap.DepthTest);
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        var arr = pts.ToArray();
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Lines, 0, arr.Length / 3);
        GL.BindVertexArray(0);
        GL.Enable(EnableCap.DepthTest);
    }

    // ── Selection resize handles (2D) ─────────────────────────────────────────

    private static readonly Vector4 HandleFill   = new(1.00f, 1.00f, 1.00f, 1f);
    private static readonly Vector4 HandleBorder = new(0.00f, 0.55f, 0.95f, 1f);

    /// <summary>
    /// Draws filled square handles at the given ortho-space positions, sized to a
    /// constant pixel extent regardless of zoom.
    /// </summary>
    public void DrawSelectionHandles2D(IReadOnlyList<(float h, float v)> handles, float pixelSize, Camera2D cam, int w, int h,
                                       Vector4? fillOverride = null)
    {
        if (handles.Count == 0) return;
        var fillColor = fillOverride ?? HandleFill;
        float half = pixelSize * cam.Zoom;          // ortho half-extent
        var mvp = cam.GetProjectionMatrix(w, h);

        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", mvp);

        // Filled squares (two triangles each)
        var fill = new List<float>(handles.Count * 18);
        foreach (var (hx, vy) in handles)
        {
            float l = hx - half, r = hx + half, b = vy - half, t = vy + half;
            fill.AddRange([l, b, 0f,  r, b, 0f,  r, t, 0f,
                           l, b, 0f,  r, t, 0f,  l, t, 0f]);
        }
        _lineShader.SetVector4("uColor", fillColor);
        UploadDrawTriangles(fill);

        // Borders (line loop per square)
        var border = new List<float>(handles.Count * 24);
        foreach (var (hx, vy) in handles)
        {
            float l = hx - half, r = hx + half, b = vy - half, t = vy + half;
            border.AddRange([l, b, 0f, r, b, 0f,  r, b, 0f, r, t, 0f,
                             r, t, 0f, l, t, 0f,  l, t, 0f, l, b, 0f]);
        }
        _lineShader.SetVector4("uColor", HandleBorder);
        UploadDrawLines(border);
    }

    private static readonly Vector4 ClipColor = new(1.00f, 0.30f, 0.30f, 1f);

    /// <summary>Draws the clip line (extended across the view) during a clip drag.</summary>
    public void DrawClipLine2D(float h1, float v1, float h2, float v2, Camera2D cam, int w, int h)
    {
        float dh = h2 - h1, dv = v2 - v1;
        float len = MathF.Sqrt(dh * dh + dv * dv);
        if (len < 1e-3f) return;
        dh /= len; dv /= len;
        float ext = (w + h) * cam.Zoom;           // long enough to span the viewport
        float[] pts =
        [
            h1 - dh * ext, v1 - dv * ext, 0f,
            h2 + dh * ext, v2 + dv * ext, 0f,
        ];
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", cam.GetProjectionMatrix(w, h));
        _lineShader.SetVector4("uColor", ClipColor);
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, pts.Length * sizeof(float), pts, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Lines, 0, pts.Length / 3);
        GL.BindVertexArray(0);
    }

    // #3: rotate-mode indicator — a ring through the selection's corner handles with two tangent
    // arrowheads, the Hammer "this is rotate mode" cue. World-ortho coords; radius in world units.
    public void DrawRotateRing2D(float ch, float cv, float radius, Camera2D cam, int w, int h, Vector4 col)
    {
        if (radius < 1e-3f) return;
        const int SEG = 48;
        var pts = new List<float>((SEG + 1) * 3);
        for (int i = 0; i <= SEG; i++)
        {
            float a = (float)(i * 2.0 * Math.PI / SEG);
            pts.Add(ch + radius * MathF.Cos(a)); pts.Add(cv + radius * MathF.Sin(a)); pts.Add(0f);
        }
        // Two arrowheads 180° apart, each a small chevron opening along the tangent (CCW sense).
        float head = radius * 0.18f;
        void Arrow(float ang)
        {
            float th = ch + radius * MathF.Cos(ang), tv = cv + radius * MathF.Sin(ang);
            float tanH = -MathF.Sin(ang), tanV = MathF.Cos(ang);    // CCW tangent
            float nH = MathF.Cos(ang), nV = MathF.Sin(ang);         // outward normal
            // chevron: from two back-and-side points into the tip
            pts.AddRange([th - (tanH * head) + (nH * head * 0.6f), tv - (tanV * head) + (nV * head * 0.6f), 0f, th, tv, 0f]);
            pts.AddRange([th - (tanH * head) - (nH * head * 0.6f), tv - (tanV * head) - (nV * head * 0.6f), 0f, th, tv, 0f]);
        }
        Arrow(0f); Arrow(MathF.PI);

        float[] arr = pts.ToArray();
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", cam.GetProjectionMatrix(w, h));
        _lineShader.SetVector4("uColor", col);
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.LineStrip, 0, SEG + 1);          // the ring
        GL.DrawArrays(PrimitiveType.Lines, SEG + 1, 8);              // 2 arrowheads × 2 segments × 2 verts
        GL.BindVertexArray(0);
    }

    // Logic-connection wires (Hammer entity I/O): colored segments between actor positions. 2D ortho.
    public void DrawConnections2D(IReadOnlyList<(float h1, float v1, float h2, float v2, Vector4 col)> lines,
                                  Camera2D cam, int w, int h)
    {
        if (lines.Count == 0) return;
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", cam.GetProjectionMatrix(w, h));
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        foreach (var (h1, v1, h2, v2, col) in lines)
        {
            _lineShader.SetVector4("uColor", col);
            float[] pts = [h1, v1, 0f, h2, v2, 0f];
            GL.BufferData(BufferTarget.ArrayBuffer, pts.Length * sizeof(float), pts, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Lines, 0, 2);
        }
        GL.BindVertexArray(0);
    }

    // Logic-connection wires in the 3D perspective view.
    public void DrawConnections3D(IReadOnlyList<(Vector3 a, Vector3 b, Vector4 col)> lines, Camera3D cam, int w, int h)
    {
        if (lines.Count == 0) return;
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", mvp);
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        foreach (var (a, b, col) in lines)
        {
            _lineShader.SetVector4("uColor", col);
            float[] pts = [a.X, a.Y, a.Z, b.X, b.Y, b.Z];
            GL.BufferData(BufferTarget.ArrayBuffer, pts.Length * sizeof(float), pts, BufferUsageHint.DynamicDraw);
            GL.DrawArrays(PrimitiveType.Lines, 0, 2);
        }
        GL.BindVertexArray(0);
    }

    /// <summary>Wire colour for a flag namespace (matches the Flag Connections dialog's grouping).</summary>
    public static Vector4 ConnectionColor(Editor.ActorParamSchema.FlagKind kind) => kind switch
    {
        Editor.ActorParamSchema.FlagKind.Switch        => new(0.20f, 0.85f, 1.00f, 1f),  // cyan
        Editor.ActorParamSchema.FlagKind.Chest         => new(1.00f, 0.82f, 0.15f, 1f),  // gold
        Editor.ActorParamSchema.FlagKind.Collectible   => new(0.40f, 0.95f, 0.45f, 1f),  // green
        Editor.ActorParamSchema.FlagKind.Clear         => new(0.98f, 0.40f, 0.35f, 1f),  // red
        Editor.ActorParamSchema.FlagKind.Event         => new(0.85f, 0.45f, 1.00f, 1f),  // magenta
        Editor.ActorParamSchema.FlagKind.GoldSkulltula => new(1.00f, 0.95f, 0.30f, 1f),  // yellow
        _                                              => new(0.80f, 0.80f, 0.80f, 1f),
    };

    /// <summary>The wire colour as a GDI Color (for the I/O list rows in the entity dialog), matching the
    /// viewport wire colour per flag namespace.</summary>
    public static System.Drawing.Color ConnectionColorRgb(Editor.ActorParamSchema.FlagKind kind)
    {
        var c = ConnectionColor(kind);
        return System.Drawing.Color.FromArgb(255, (int)(c.X * 255), (int)(c.Y * 255), (int)(c.Z * 255));
    }

    // ── Scene paths (0x0D waypoint polylines) ─────────────────────────────────
    private static readonly Vector4 PathColor     = new(1.00f, 0.65f, 0.10f, 1f);   // track orange
    private static readonly Vector4 PathHandle    = new(1.00f, 0.80f, 0.30f, 1f);
    private static readonly Vector4 PathHandleHot  = new(0.20f, 0.95f, 1.00f, 1f);  // active waypoint

    /// <summary>Draws every path as an orange polyline with square waypoint handles in a 2D view;
    /// the active (hiPath,hiPoint) waypoint is drawn larger in cyan.</summary>
    public void DrawPaths2D(IReadOnlyList<IReadOnlyList<Vector3>> paths, Camera2D cam, int w, int h, int hiPath = -1, int hiPoint = -1)
    {
        if (paths.Count == 0) return;
        var mvp = cam.GetProjectionMatrix(w, h);
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", mvp);
        _lineShader.SetVector4("uColor", PathColor);
        var seg = new List<float>();
        foreach (var pts in paths)
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = WorldToOrtho(pts[i], cam.Axis); var b = WorldToOrtho(pts[i + 1], cam.Axis);
                seg.AddRange([a.X, a.Y, 0f, b.X, b.Y, 0f]);
            }
        GL.Disable(EnableCap.DepthTest);
        UploadDrawLines(seg);
        GL.Enable(EnableCap.DepthTest);

        var handles = new List<(float, float)>();
        foreach (var pts in paths) foreach (var p in pts) { var o = WorldToOrtho(p, cam.Axis); handles.Add((o.X, o.Y)); }
        DrawSelectionHandles2D(handles, 4.5f, cam, w, h, PathHandle);
        if (hiPath >= 0 && hiPath < paths.Count && hiPoint >= 0 && hiPoint < paths[hiPath].Count)
        {
            var o = WorldToOrtho(paths[hiPath][hiPoint], cam.Axis);
            DrawSelectionHandles2D([(o.X, o.Y)], 7f, cam, w, h, PathHandleHot);
        }
    }

    /// <summary>Draws every path as an orange polyline with small boxes at waypoints in the 3D view.</summary>
    public void DrawPaths3D(IReadOnlyList<IReadOnlyList<Vector3>> paths, Camera3D cam, int w, int h)
    {
        if (paths.Count == 0) return;
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h));
        // Depth test ON so brush walls OCCLUDE the track (it was drawn through walls before).
        GL.Enable(EnableCap.DepthTest);

        _lineShader.SetVector4("uColor", PathColor);
        var seg = new List<float>();
        foreach (var pts in paths)
            for (int i = 0; i + 1 < pts.Count; i++)
                seg.AddRange([pts[i].X, pts[i].Y, pts[i].Z, pts[i + 1].X, pts[i + 1].Y, pts[i + 1].Z]);
        UploadDrawLines(seg);
        // (Waypoint nodes are now drawn as billboard sprites by BillboardRenderer.RenderPathMarkers — the
        // bare wire boxes were replaced so each node reads as a clickable entity.)
    }

    // Appends a wire box's 12 edges (24 vertices) to a line-segment list.
    private static void AppendWireBox(List<float> seg, Vector3 mn, Vector3 mx)
    {
        Vector3[] c =
        [
            new(mn.X, mn.Y, mn.Z), new(mx.X, mn.Y, mn.Z), new(mx.X, mn.Y, mx.Z), new(mn.X, mn.Y, mx.Z),
            new(mn.X, mx.Y, mn.Z), new(mx.X, mx.Y, mn.Z), new(mx.X, mx.Y, mx.Z), new(mn.X, mx.Y, mx.Z),
        ];
        int[,] edges = { {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4}, {0,4},{1,5},{2,6},{3,7} };
        for (int i = 0; i < 12; i++)
        {
            var a = c[edges[i, 0]]; var b = c[edges[i, 1]];
            seg.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z]);
        }
    }

    /// <summary>Draws each decal as a textured, alpha-blended quad floating just off its surface (an overlay,
    /// not a face retexture). Depth-tested but not depth-written so it never occludes the world; selected
    /// decals get an orange outline. Hidden rooms are skipped.</summary>
    public void DrawDecals3D(ZScene scene, Camera3D cam, int w, int h)
    {
        var decals = scene.Rooms.Where(r => r.Visible).SelectMany(r => r.Decals).ToList();
        if (decals.Count == 0) return;
        var mvp = cam.GetViewMatrix() * cam.GetProjectionMatrix(w, h);
        var textured = decals.Where(d => d.TextureName != null && Library?.Find(d.TextureName) != null).ToList();

        if (textured.Count > 0)
        {
            _texShader.Use();
            _texShader.SetMatrix4("uMVP", mvp);
            SetEnv(_texShader, scene, cam);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Uniform1(GL.GetUniformLocation(_texShader.Handle, "uTex"), 0);
            GL.Uniform1(GL.GetUniformLocation(_texShader.Handle, "uAlpha"), 1.0f);
            GL.Uniform2(GL.GetUniformLocation(_texShader.Handle, "uUVScroll"), 0f, 0f);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.CullFace);
            GL.DepthMask(false);
            foreach (var grp in textured.GroupBy(d => d.TextureName!))
            {
                var buf = new List<float>();
                foreach (var d in grp)
                {
                    var c = d.Corners();
                    var n = d.Normal.LengthSquared > 1e-6f ? Vector3.Normalize(d.Normal) : Vector3.UnitY;
                    void V(Vector3 p, float u, float vv) => buf.AddRange([p.X, p.Y, p.Z, n.X, n.Y, n.Z, u, vv, 1f, 1f, 1f]);
                    V(c[0], 0, 1); V(c[1], 1, 1); V(c[2], 1, 0);        // BL, BR, TR
                    V(c[0], 0, 1); V(c[2], 1, 0); V(c[3], 0, 0);        // BL, TR, TL
                }
                GL.BindTexture(TextureTarget.Texture2D, GetGlTexture(grp.Key));
                UploadDrawTexFaces(buf);
            }
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
        }

        // Outlines: selected decals bright orange, others a faint wireframe so unplaced/untextured decals
        // are still visible and grabbable.
        var lines = new List<float>();
        var selLines = new List<float>();
        foreach (var d in decals)
        {
            var c = d.Corners(1.0f);
            var dst = d.IsSelected ? selLines : lines;
            for (int i = 0; i < 4; i++) { var a = c[i]; var b = c[(i + 1) % 4]; dst.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z]); }
        }
        _lineShader.Use(); _lineShader.SetMatrix4("uMVP", mvp);
        if (lines.Count > 0)    { _lineShader.SetVector4("uColor", new Vector4(0.55f, 0.75f, 1f, 0.7f)); UploadDrawLines(lines); }
        if (selLines.Count > 0) { _lineShader.SetVector4("uColor", SelectedEdgeColor); UploadDrawLines(selLines); }
    }

    /// <summary>Draws each decal's footprint rectangle in a 2D ortho view (projected onto the view axis),
    /// so decals are visible/selectable in the top/front/side panes like brushes. Hidden rooms skipped.</summary>
    public void DrawDecals2D(ZScene scene, Camera2D cam, int w, int h)
    {
        var decals = scene.Rooms.Where(r => r.Visible).SelectMany(r => r.Decals).ToList();
        if (decals.Count == 0) return;
        var (hDir, vDir) = Axis2D(cam.Axis);
        var lines = new List<float>(); var sel = new List<float>();
        foreach (var d in decals)
        {
            var c = d.Corners(0f);
            var dst = d.IsSelected ? sel : lines;
            for (int i = 0; i < 4; i++)
            {
                var a = c[i]; var b = c[(i + 1) % 4];
                dst.AddRange([Vector3.Dot(a, hDir), Vector3.Dot(a, vDir), 0f, Vector3.Dot(b, hDir), Vector3.Dot(b, vDir), 0f]);
            }
        }
        _lineShader.Use();
        _lineShader.SetMatrix4("uMVP", cam.GetProjectionMatrix(w, h));
        if (lines.Count > 0) { _lineShader.SetVector4("uColor", new Vector4(0.55f, 0.75f, 1f, 0.9f)); UploadDrawLines(lines); }
        if (sel.Count > 0)   { _lineShader.SetVector4("uColor", SelectedEdgeColor); UploadDrawLines(sel); }
    }

    private static (Vector3 h, Vector3 v) Axis2D(ViewAxis axis) => axis switch
    {
        ViewAxis.Top   => (new(1, 0, 0), new(0, 0, -1)),
        ViewAxis.Front => (new(1, 0, 0), new(0, 1, 0)),
        ViewAxis.Side  => (new(0, 0, 1), new(0, 1, 0)),
        _              => (new(1, 0, 0), new(0, 1, 0)),
    };

    private void UploadDrawTriangles(List<float> data)
    {
        if (data.Count == 0) return;
        var arr = data.ToArray();
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, arr.Length / 3);
        GL.BindVertexArray(0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Vector2 WorldToOrtho(Vector3 w, ViewAxis axis) => axis switch
    {
        ViewAxis.Top   => new(w.X, -w.Z),
        ViewAxis.Front => new(w.X,  w.Y),
        ViewAxis.Side  => new(w.Z,  w.Y),
        _              => Vector2.Zero
    };

    private static void PushFaceVert(List<float> buf, Vector3 pos, Vector3 normal, Vector3 color)
    {
        buf.AddRange([pos.X, pos.Y, pos.Z, normal.X, normal.Y, normal.Z, color.X, color.Y, color.Z]);
    }

    // Emits a shade-painted quad's grid cells (2 tris each) with per-node painted colour, into the textured
    // buffer (texBuf) or the flat-colour buffer (flatBuf). Local spray shading, unlike the 4-corner face.
    private static void EmitShadeGrid(SolidFace face, SolidFace.ShadeGrid g, Vector3 n, List<float>? texBuf, List<float>? flatBuf)
    {
        for (int j = 0; j < g.Nv; j++)
            for (int i = 0; i < g.Nu; i++)
            {
                Vector3 p00 = face.ShadeGridPos(i, j, g.Nu, g.Nv), p10 = face.ShadeGridPos(i + 1, j, g.Nu, g.Nv);
                Vector3 p11 = face.ShadeGridPos(i + 1, j + 1, g.Nu, g.Nv), p01 = face.ShadeGridPos(i, j + 1, g.Nu, g.Nv);
                Vector3 c00 = g.Colors[g.Index(i, j)], c10 = g.Colors[g.Index(i + 1, j)];
                Vector3 c11 = g.Colors[g.Index(i + 1, j + 1)], c01 = g.Colors[g.Index(i, j + 1)];
                if (texBuf != null)
                {
                    PushTexVert(texBuf, p00, n, face, c00); PushTexVert(texBuf, p10, n, face, c10); PushTexVert(texBuf, p11, n, face, c11);
                    PushTexVert(texBuf, p00, n, face, c00); PushTexVert(texBuf, p11, n, face, c11); PushTexVert(texBuf, p01, n, face, c01);
                }
                else
                {
                    PushFaceVert(flatBuf!, p00, n, c00); PushFaceVert(flatBuf!, p10, n, c10); PushFaceVert(flatBuf!, p11, n, c11);
                    PushFaceVert(flatBuf!, p00, n, c00); PushFaceVert(flatBuf!, p11, n, c11); PushFaceVert(flatBuf!, p01, n, c01);
                }
            }
    }

    private static void PushTexVert(List<float> buf, Vector3 pos, Vector3 normal, SolidFace face, Vector3 shade)
    {
        var uv = face.UVAt(pos);   // full Hammer-style transform: scale/shift/rotation/align
        buf.AddRange([pos.X, pos.Y, pos.Z, normal.X, normal.Y, normal.Z, uv.X, uv.Y, shade.X, shade.Y, shade.Z]);
    }

    private void UploadDrawTexFaces(List<float> data)
    {
        var arr = data.ToArray();
        GL.BindVertexArray(_texVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _texVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, arr.Length / 11);
        GL.BindVertexArray(0);
    }

    private void UploadDrawFaces(List<float> data)
    {
        var arr = data.ToArray();
        GL.BindVertexArray(_faceVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _faceVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, arr.Length / 9);
        GL.BindVertexArray(0);
    }

    private void UploadDrawLines(List<float> data)
    {
        var arr = data.ToArray();
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Lines, 0, arr.Length / 3);
        GL.BindVertexArray(0);
    }

    // Draws position-only (3 floats/vertex) triangles with the line shader's uniform colour — used
    // for the translucent selected-face fill.
    private void UploadDrawTris(List<float> data)
    {
        var arr = data.ToArray();
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, arr.Length / 3);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        GL.DeleteVertexArray(_faceVao);
        GL.DeleteBuffer(_faceVbo);
        GL.DeleteVertexArray(_lineVao);
        GL.DeleteBuffer(_lineVbo);
        GL.DeleteVertexArray(_texVao);
        GL.DeleteBuffer(_texVbo);
        foreach (var tex in _glTex.Values)
            if (tex != 0) GL.DeleteTexture(tex);
        _faceShader.Dispose();
        _lineShader.Dispose();
        _texShader.Dispose();
        _disposed = true;
    }
}
