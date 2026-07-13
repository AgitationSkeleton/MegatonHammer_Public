using MegatonHammer.Editor;
using MegatonHammer.Export;
using MegatonHammer.Otr;
using OpenTK.Mathematics;

namespace MegatonHammer.SelfTest;

/// <summary>Headless check that a Translucent / Additive brush routes into the room's poly_xlu display list
/// with the correct vanilla render mode, on BOTH the N64 (DisplayListBuilder) and SoH/2Ship (OtrRoomGeometry)
/// paths — and that opaque brushes stay in the opaque list. Run: --blendtest</summary>
public static class BlendTest
{
    // Render-mode L-words (SetOtherMode_L E2 00 00 1C xx): XLU alpha-blend vs additive (B = G_BL_1).
    // N64 DisplayListBuilder writes big-endian; the OTR writer is little-endian, so the same words appear
    // byte-reversed per u32 in each stream.
    private static readonly byte[] RM_XLU = [0xE2, 0x00, 0x00, 0x1C, 0x00, 0x50, 0x49, 0xD8];
    private static readonly byte[] RM_ADD = [0xE2, 0x00, 0x00, 0x1C, 0x00, 0x5A, 0x49, 0xD8];
    private static readonly byte[] RM_XLU_OTR = [0x1C, 0x00, 0x00, 0xE2, 0xD8, 0x49, 0x50, 0x00];
    private static readonly byte[] RM_ADD_OTR = [0x1C, 0x00, 0x00, 0xE2, 0xD8, 0x49, 0x5A, 0x00];

    public static void Run()
    {
        Console.WriteLine("== brush blend / opacity export test ==");
        var doc = new MapDocument();
        var trans = Solid.CreateBox(new Vector3(-64, -64, -64), new Vector3(64, 64, 64));
        trans.Blend = BrushBlend.Translucent; trans.Opacity = 128;
        var add = Solid.CreateBox(new Vector3(100, -64, -64), new Vector3(200, 64, 64));
        add.Blend = BrushBlend.Additive; add.Opacity = 200;
        var opaque = Solid.CreateBox(new Vector3(300, -64, -64), new Vector3(400, 64, 64));
        doc.AddSolid(trans); doc.AddSolid(add); doc.AddSolid(opaque);
        var room = doc.Scene.ActiveRoom!;

        // ── N64 path ──
        var dl = DisplayListBuilder.Build(room, 0x03, 0, texResolver: null, n64Hw: true, lighting: doc.Scene.Settings);
        bool n64Xlu = dl.XluDlCommands.Length > 0;
        bool n64Trans = Contains(dl.XluDlCommands, RM_XLU);
        bool n64Add   = Contains(dl.XluDlCommands, RM_ADD);
        bool n64OpaqueClean = !Contains(dl.DlCommands, RM_XLU) && !Contains(dl.DlCommands, RM_ADD);
        // Opacity must be emitted as PRIM alpha (op 128 = 0x80, op 200 = 0xC8), and untextured xlu uses the
        // SHADE-colour/PRIM-alpha combiner. This is the actual fix (vertex alpha was ignored by Fast3D).
        bool n64Prim = Contains(dl.XluDlCommands, [0xFA, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x80])
                    && Contains(dl.XluDlCommands, [0xFA, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xC8]);
        bool n64FlatCc = Contains(dl.XluDlCommands, [0xFC, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE, 0x77, 0x3B]);
        Console.WriteLine($"  N64: xlu list={dl.XluDlCommands.Length}b translucentRM={n64Trans} additiveRM={n64Add} opaqueListClean={n64OpaqueClean} primAlpha={n64Prim} flatCombiner={n64FlatCc}");
        bool n64Ok = n64Xlu && n64Trans && n64Add && n64OpaqueClean && n64Prim && n64FlatCc && dl.DlCommands.Length > 0;
        Console.WriteLine(n64Ok ? "  PASS — N64 routes translucent+additive into poly_xlu with the right render modes."
                                : "  FAIL — N64 xlu routing wrong.");

        // ── SoH/2Ship OTR path ──
        var res = OtrRoomGeometry.Build(room, "scenes/x_vtx", "scenes/x_tex", texResolver: null);
        bool otrXlu = res.XluDl.Length > 0;
        bool otrTrans = Contains(res.XluDl, RM_XLU_OTR);
        bool otrAdd   = Contains(res.XluDl, RM_ADD_OTR);
        // PRIM alpha (LE): prim cmd 0xFA000000 / 0xFFFFFF80 (op128) and /0xFFFFFFC8 (op200); flat combiner LE.
        bool otrPrim = Contains(res.XluDl, [0x00, 0x00, 0x00, 0xFA, 0x80, 0xFF, 0xFF, 0xFF])
                    && Contains(res.XluDl, [0x00, 0x00, 0x00, 0xFA, 0xC8, 0xFF, 0xFF, 0xFF]);
        bool otrFlatCc = Contains(res.XluDl, [0xFF, 0xFF, 0xFF, 0xFC, 0x3B, 0x77, 0xFE, 0xFF]);
        Console.WriteLine($"  OTR: xlu list={res.XluDl.Length}b translucentRM={otrTrans} additiveRM={otrAdd} primAlpha={otrPrim} flatCombiner={otrFlatCc}");
        bool otrOk = otrXlu && otrTrans && otrAdd && otrPrim && otrFlatCc && res.Dl.Length > 0;
        Console.WriteLine(otrOk ? "  PASS — OTR routes translucent+additive into poly_xlu with the right render modes."
                                : "  FAIL — OTR xlu routing wrong.");

        // ── An all-opaque room must produce NO xlu list (no regression for normal maps) ──
        var doc2 = new MapDocument();
        doc2.AddSolid(Solid.CreateBox(new Vector3(-64, -64, -64), new Vector3(64, 64, 64)));
        var dl2 = DisplayListBuilder.Build(doc2.Scene.ActiveRoom!, 0x03, 0, texResolver: null, n64Hw: true, lighting: doc2.Scene.Settings);
        bool noXluWhenOpaque = dl2.XluDlCommands.Length == 0;
        Console.WriteLine(noXluWhenOpaque ? "  PASS — an all-opaque room emits no xlu list."
                                          : "  FAIL — opaque room wrongly emitted an xlu list.");

        Console.WriteLine(n64Ok && otrOk && noXluWhenOpaque
            ? "RESULT: PASS — brush opacity/blend commits to N64 + SoH/2Ship poly_xlu."
            : "RESULT: FAIL — see above.");
    }

    private static bool Contains(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            bool m = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { m = false; break; }
            if (m) return true;
        }
        return false;
    }
}
