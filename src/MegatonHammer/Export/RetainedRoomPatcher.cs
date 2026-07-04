using MegatonHammer.Editor;

namespace MegatonHammer.Export;

/// <summary>
/// Faithful vanilla round-trip: re-emits an imported room's ORIGINAL file bytes with only its actor
/// list (header command 0x01) replaced by the editor's actors. Everything else — the mesh, collision
/// references, object-dependency list (0x0B), behaviour/echo/time, and any commands the editor doesn't
/// model — is preserved byte-for-byte. The scene file itself can be re-injected verbatim because actors
/// live in the rooms, so this single patch makes "edit/place actors in a vanilla level" round-trip
/// without losing collision surface types, the keep object, prerender cameras or MM spawn flags.
/// </summary>
public static class RetainedRoomPatcher
{
    private const byte CmdActorList = 0x01, CmdEnd = 0x14;

    /// <summary>Returns a copy of <paramref name="roomBytes"/> with its 0x01 actor list rewritten from
    /// <paramref name="actors"/>. If the count is unchanged the array is overwritten in place; otherwise
    /// a new array is appended and the command's count + pointer are repointed. Returns the original
    /// bytes unchanged if the room has no actor-list command (nothing to patch).</summary>
    public static byte[] PatchActors(byte[] roomBytes, IReadOnlyList<ZActor> actors, bool mm)
    {
        // Locate the 0x01 command in the room header (commands are 8 bytes until the 0x14 terminator).
        int cmdOff = -1, listSeg = -1, listOff = -1, oldCount = 0;
        for (int p = 0; p + 8 <= roomBytes.Length; p += 8)
        {
            byte op = roomBytes[p];
            if (op == CmdEnd) break;
            if (op == CmdActorList)
            {
                cmdOff = p;
                oldCount = roomBytes[p + 1];
                listSeg = roomBytes[p + 4];
                listOff = (int)(U32(roomBytes, p + 4) & 0x00FFFFFF);
                break;
            }
        }
        // Room shipped with NO actor list (0x01) — boss rooms, Death Mountain Crater, etc. If the editor
        // placed actors there, INSERT a 0x01 command so the edit sticks (else return the room unchanged).
        if (cmdOff < 0)
        {
            if (actors.Count == 0) return (byte[])roomBytes.Clone();
            return InsertActorList(roomBytes, actors, mm);
        }
        if (listSeg != 3) return (byte[])roomBytes.Clone();   // malformed pointer — don't touch

        int newCount = Math.Min(actors.Count, 0xFF);

        if (newCount == oldCount && listOff + newCount * 16 <= roomBytes.Length)
        {
            // Same count → overwrite the actor array in place; the rest of the file is untouched.
            var outBytes = (byte[])roomBytes.Clone();
            WriteActors(outBytes, listOff, actors, newCount, mm);
            return outBytes;
        }

        // Count changed → append a fresh 16-aligned actor array at the end and repoint the command.
        int appendOff = AlignUp(roomBytes.Length, 16);
        var grown = new byte[appendOff + newCount * 16];
        Array.Copy(roomBytes, grown, roomBytes.Length);
        WriteActors(grown, appendOff, actors, newCount, mm);
        grown[cmdOff + 1] = (byte)newCount;
        WriteU32(grown, cmdOff + 4, (uint)((3 << 24) | (appendOff & 0x00FFFFFF)));
        return grown;
    }

    // Inserts a fresh 0x01 actor-list command into a room header that had none. The new command is spliced
    // in just before the 0x14 terminator; the terminator + all data after it shift +8 bytes, and every
    // seg-3 (room-internal) pointer in the other header commands is bumped +8 so the mesh / object list /
    // etc. still resolve. The actor array is appended (16-aligned) and the new command points at it. All
    // non-header data is preserved byte-for-byte (just relocated), keeping the round-trip faithful.
    private static byte[] InsertActorList(byte[] roomBytes, IReadOnlyList<ZActor> actors, bool mm)
    {
        int endOff = -1;
        for (int p = 0; p + 8 <= roomBytes.Length; p += 8) { if (roomBytes[p] == CmdEnd) { endOff = p; break; } }
        if (endOff < 0) return (byte[])roomBytes.Clone();   // malformed header — leave untouched

        int newCount = Math.Min(actors.Count, 0xFF);
        int actorArrayOff = AlignUp(roomBytes.Length + 8, 16);
        var outBytes = new byte[actorArrayOff + newCount * 16];

        Array.Copy(roomBytes, 0, outBytes, 0, endOff);                    // header commands before 0x14
        outBytes[endOff] = CmdActorList; outBytes[endOff + 1] = (byte)newCount;   // new 0x01 command
        Array.Copy(roomBytes, endOff, outBytes, endOff + 8, roomBytes.Length - endOff);   // 0x14 + data, +8

        // Bump every seg-3 pointer in the (original) header commands by +8 — their data targets moved.
        for (int p = 0; p < endOff; p += 8)
        {
            if (outBytes[p + 4] == 0x03)
            {
                int off = (int)(U32(outBytes, p + 4) & 0x00FFFFFF);
                if (off >= endOff) WriteU32(outBytes, p + 4, (uint)((3u << 24) | (uint)((off + 8) & 0x00FFFFFF)));
            }
        }
        WriteU32(outBytes, endOff + 4, (uint)((3u << 24) | (uint)(actorArrayOff & 0x00FFFFFF)));
        WriteActors(outBytes, actorArrayOff, actors, newCount, mm);
        return outBytes;
    }

    private static void WriteActors(byte[] d, int off, IReadOnlyList<ZActor> actors, int count, bool mm)
    {
        for (int i = 0; i < count; i++)
        {
            var a = actors[i];
            int e = off + i * 16;
            // Re-OR the MM spawn-condition flags back into the id word so time/event gating survives.
            ushort idWord = mm ? (ushort)((a.Number & 0x1FFF) | (a.IdFlags & 0xE000)) : a.Number;
            WriteU16(d, e, idWord);
            WriteU16(d, e + 2, (ushort)(short)MathF.Round(a.XPos));
            WriteU16(d, e + 4, (ushort)(short)MathF.Round(a.YPos));
            WriteU16(d, e + 6, (ushort)(short)MathF.Round(a.ZPos));
            WriteU16(d, e + 8, (ushort)a.XRot);
            WriteU16(d, e + 10, (ushort)a.YRot);
            WriteU16(d, e + 12, (ushort)a.ZRot);
            WriteU16(d, e + 14, a.Variable);
        }
    }

    private static int AlignUp(int v, int a) => (v + a - 1) & ~(a - 1);
    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
    private static void WriteU32(byte[] d, int o, uint v) { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }
    private static void WriteU16(byte[] d, int o, ushort v) { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }
}
