namespace MegatonHammer.SelfTest;

/// <summary>
/// Extract the baked room texture(s) from a generated N64 playtest ROM and report their average colour —
/// the DEFINITIVE check for "PJ64 textures are grey": is the texel data we wrote actually grey, or coloured
/// (→ a real-RDP render/TMEM problem)? Finds our injected room by its header signature, walks
/// header→mesh→shape→DL, then each SETTIMG's segment-relative texture.
/// Run: MegatonHammer --diagrom &lt;path.z64&gt;
/// </summary>
public static class DiagRom
{
    public static void Run(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("usage: --diagrom <rom.z64>"); return; }
        byte[] d = File.ReadAllBytes(a[1]);
        Console.WriteLine($"== {Path.GetFileName(a[1])} ({d.Length} bytes) ==");

        // Find our injected room header: 0x08(behavior) @0, 0x10 @8, 0x12 @16, 0x16 @24, 0x0A(mesh) @32.
        int room = -1;
        for (int p = 0; p + 40 < d.Length; p += 8)
            if (d[p] == 0x08 && d[p + 8] == 0x10 && d[p + 16] == 0x12 && d[p + 24] == 0x16 && d[p + 32] == 0x0A)
            { room = p; break; }
        if (room < 0) { Console.WriteLine("room header signature not found"); return; }
        Console.WriteLine($"room header @ file 0x{room:X}");

        // 0x0A mesh command (at room+32): data ptr (seg3) → mesh header.
        int meshHdr = room + (int)(U32(d, room + 36) & 0xFFFFFF);
        // mesh header type 0: [type u8][nEntries u8][pad u16][startPtr u32][endPtr u32]; shape entry @ startPtr.
        int shape = room + (int)(U32(d, meshHdr + 4) & 0xFFFFFF);
        int dlOff = (int)(U32(d, shape) & 0xFFFFFF);   // opaPtr → display list (room-relative)
        int dl = room + dlOff;
        Console.WriteLine($"mesh@0x{meshHdr - room:X} shape@0x{shape - room:X} dl@0x{dlOff:X} (file 0x{dl:X})");

        // Vertex block starts right after the 8-byte RoomShapeEntry. Each vtx = 16 bytes; bytes 12..14 = R,G,B
        // shade. Report the first few so we can see whether scene lighting was baked (non-white) or flat white.
        int vtx = shape + 8;
        Console.Write("first vertex shades (R,G,B): ");
        for (int i = 0; i < 6 && vtx + i * 16 + 15 < d.Length; i++)
            Console.Write($"({d[vtx + i * 16 + 12]},{d[vtx + i * 16 + 13]},{d[vtx + i * 16 + 14]}) ");
        Console.WriteLine();

        // Walk the DL; track SETTIMG (current texture) and G_VTX (current vertex block) so each tile's
        // texture can be tied to the geometry Y-range it covers (floor = low Y, walls = vertical span).
        int texPtr = -1, n = 0, vtxBlk = -1; string curTexLabel = "";
        var texYmin = new System.Collections.Generic.Dictionary<int, float>();
        var texYmax = new System.Collections.Generic.Dictionary<int, float>();
        for (int p = dl; p + 8 <= d.Length && p < dl + 0x4000; p += 8)
        {
            byte op = d[p];
            if (op == 0xDF) break;                                  // EndDL
            if (op == 0xFD) texPtr = (int)(U32(d, p + 4) & 0xFFFFFF);
            else if (op == 0x01)                                    // G_VTX: load a vertex block (segptr in w1)
                vtxBlk = room + (int)(U32(d, p + 4) & 0xFFFFFF);
            else if (op == 0xF2 && texPtr >= 0)                     // SetTileSize → w,h then dump colour
            {
                int lrs = (int)(U32(d, p + 4));
                int w = ((lrs >> 12) >> 2) + 1, h = ((lrs & 0xFFF) >> 2) + 1;
                var (r, g, b, gray) = Avg5551(d, room + texPtr, w * h);
                Console.WriteLine($"  tex#{n} @0x{texPtr:X} {w}x{h} avgRGB=({r},{g},{b}) " +
                                  $"{(gray ? "<-- GREY" : "COLOURED")}");
                curTexLabel = $"tex#{n}"; n++; texPtr = -1;
            }
            else if ((op == 0x05 || op == 0x06) && vtxBlk >= 0 && curTexLabel != "")  // G_TRI1/G_TRI2: a drawn face
            {
                // sample the Y of vertex index in byte p+1 (idx/2), Y at vtx+ofs+2 (s16)
                for (int bi = 1; bi <= (op == 0x06 ? 7 : 3); bi++)
                {
                    if (bi == 4 && op == 0x06) continue;            // the 0 separator byte in TRI2
                    int vi = d[p + bi] / 2;
                    short y = (short)((d[vtxBlk + vi * 16 + 2] << 8) | d[vtxBlk + vi * 16 + 3]);
                    int key = n - 1;
                    if (!texYmin.ContainsKey(key)) { texYmin[key] = y; texYmax[key] = y; }
                    texYmin[key] = Math.Min(texYmin[key], y); texYmax[key] = Math.Max(texYmax[key], y);
                }
            }
        }
        Console.WriteLine("texture -> geometry Y range (floor sits at the spawn floor Y; walls span vertically):");
        foreach (var k in texYmin.Keys) Console.WriteLine($"  tex#{k}: Y {texYmin[k]:F0}..{texYmax[k]:F0}");
        if (n == 0) Console.WriteLine("  (no textured tiles found in the DL — geometry is untextured/flat)");
    }

    // Average RGB of `count` RGBA16 (5-5-5-1, big-endian) texels at file offset off.
    private static (int r, int g, int b, bool gray) Avg5551(byte[] d, int off, int count)
    {
        long r = 0, g = 0, b = 0; int n = 0;
        for (int i = 0; i < count && off + i * 2 + 1 < d.Length; i++)
        {
            ushort p = (ushort)((d[off + i * 2] << 8) | d[off + i * 2 + 1]);
            r += ((p >> 11) & 0x1F) << 3; g += ((p >> 6) & 0x1F) << 3; b += ((p >> 1) & 0x1F) << 3; n++;
        }
        if (n == 0) return (0, 0, 0, true);
        int ar = (int)(r / n), ag = (int)(g / n), ab = (int)(b / n);
        int chroma = Math.Max(Math.Max(ar, ag), ab) - Math.Min(Math.Min(ar, ag), ab);
        return (ar, ag, ab, chroma < 8);
    }

    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
}
