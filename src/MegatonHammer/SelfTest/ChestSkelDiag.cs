using MegatonHammer.Rom;

namespace MegatonHammer.SelfTest;

/// <summary>--diagchestskel : parse gTreasureChestSkel (object_box @0x47D8) and print each Standard limb's
/// jointPos / child / sibling / dList, plus the accumulated world translation of the limb that draws the
/// lid DL (0x10C0) — so the editor can place the lid on top of the chest (it's drawn flat at the origin).</summary>
public static class ChestSkelDiag
{
    private const string Oot = @"D:\Copilot_OOT\READ_ONLY_GameROMs\Legend of Zelda, The - Ocarina of Time (USA).z64";
    private static byte[] _o = System.Array.Empty<byte>();

    static ushort U16(int p) => (ushort)((_o[p] << 8) | _o[p + 1]);
    static uint U32(int p) => (uint)((_o[p] << 24) | (_o[p + 1] << 16) | (_o[p + 2] << 8) | _o[p + 3]);

    public static void Run()
    {
        var rom = new RomImage(Oot);
        var ot = ObjectTable.Build(rom);
        var bytes = ot.GetObjectBytes(rom, "object_box");
        if (bytes == null) { System.Console.WriteLine($"object_box not found; id={ot.IdOf("object_box")}"); return; }
        _o = bytes;

        int skel = 0x47D8;
        uint limbListSeg = U32(skel);
        int limbCount = _o[skel + 4];
        int limbList = (int)(limbListSeg & 0xFFFFFF);
        System.Console.WriteLine($"gTreasureChestSkel: limbList@0x{limbList:X} count={limbCount}");

        var jp = new (int x, int y, int z)[limbCount];
        var child = new int[limbCount];
        var sib = new int[limbCount];
        var dl = new int[limbCount];
        for (int i = 0; i < limbCount; i++)
        {
            int limbPtr = (int)(U32(limbList + i * 4) & 0xFFFFFF);
            jp[i] = ((short)U16(limbPtr), (short)U16(limbPtr + 2), (short)U16(limbPtr + 4));
            child[i] = _o[limbPtr + 6];
            sib[i] = _o[limbPtr + 7];
            dl[i] = (int)(U32(limbPtr + 8) & 0xFFFFFF);
            System.Console.WriteLine($"  limb {i}: jointPos=({jp[i].x},{jp[i].y},{jp[i].z}) child={child[i]} sib={sib[i]} dList=0x{dl[i]:X}");
        }

        // Walk the tree (DFS like SkelAnime), accumulating translation, to get each limb's world position.
        var world = new (int x, int y, int z)[limbCount];
        void Walk(int li, int ax, int ay, int az)
        {
            if (li == 0xFF || li >= limbCount) return;
            int wx = ax + jp[li].x, wy = ay + jp[li].y, wz = az + jp[li].z;
            world[li] = (wx, wy, wz);
            if (child[li] != 0xFF) Walk(child[li], wx, wy, wz);
            if (sib[li] != 0xFF) Walk(sib[li], ax, ay, az);
        }
        Walk(0, 0, 0, 0);

        System.Console.WriteLine($"limb1 (body) world=({world[1].x},{world[1].y},{world[1].z}); limb3 (lid) world=({world[3].x},{world[3].y},{world[3].z})");

        // Closed-pose joint table (gTreasureChestAnim_000128 @ 0x128). entry0 = root xlate, 1..4 = limb rots (binang).
        var jt = ObjectModelReader.ReadAnimFrame0(_o, 0x128, limbCount);
        if (jt != null)
        {
            System.Console.WriteLine($"closed anim 0x128 frame0: root xlate=({jt[0]},{jt[1]},{jt[2]})");
            for (int i = 0; i < limbCount; i++)
            {
                int j = 3 * (i + 1);
                double deg = jt[j] * 360.0 / 65536.0;
                System.Console.WriteLine($"  limb {i} rot binang=({jt[j]},{jt[j+1]},{jt[j+2]})  rotX={deg:F1} deg");
            }
        }

        foreach (int off in new[] { 0x6F0, 0x10C0 })
        {
            var t = ObjectModelReader.ReadDList(_o, -1, off);
            if (t.Count == 0) { System.Console.WriteLine($"DL 0x{off:X}: no tris"); continue; }
            float mnx=1e9f,mny=1e9f,mnz=1e9f,mxx=-1e9f,mxy=-1e9f,mxz=-1e9f;
            foreach (var tr in t) foreach (var p in new[]{tr.P0,tr.P1,tr.P2})
            { mnx=System.MathF.Min(mnx,p.X); mny=System.MathF.Min(mny,p.Y); mnz=System.MathF.Min(mnz,p.Z);
              mxx=System.MathF.Max(mxx,p.X); mxy=System.MathF.Max(mxy,p.Y); mxz=System.MathF.Max(mxz,p.Z); }
            System.Console.WriteLine($"DL 0x{off:X}: tris={t.Count} X[{mnx:F0},{mxx:F0}] Y[{mny:F0},{mxy:F0}] Z[{mnz:F0},{mxz:F0}]");
        }
    }
}
