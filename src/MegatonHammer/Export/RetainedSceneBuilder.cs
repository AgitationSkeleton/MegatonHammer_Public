using MegatonHammer.Editor;

namespace MegatonHammer.Export;

/// <summary>
/// Faithful vanilla round-trip export: re-emits an imported scene from its ORIGINAL ROM bytes, applying
/// only the editor's actor edits (per room) and the common editable scene-header fields (spawn point,
/// environment lighting, skybox, music) in place. Everything the editor doesn't model — collision
/// surface types, the object dependency list (0x0B), the scene keep object (0x07), prerender cameras,
/// transition actors, paths, cutscenes, MM alternate setups — is preserved byte-for-byte. This is the
/// "edit a vanilla level" path that the template-rebuild exporter (SceneExporter) cannot keep faithful.
/// </summary>
public static class RetainedSceneBuilder
{
    /// <summary>Builds (scene, rooms) from the scene's imported original bytes, or null when the scene
    /// wasn't imported from a ROM (a from-blank build must use the template exporter).</summary>
    public static (byte[] Scene, List<byte[]> Rooms)? TryBuild(ZScene scene)
    {
        var imp = scene.Imported;
        if (imp == null) return null;
        var rom = imp.Rom;
        var src = imp.Scene;
        bool mm = rom.Game == Rom.RomGame.MM;

        byte[] sceneBytes;
        try { sceneBytes = (byte[])rom.GetFile(src.SceneFileIndex).Clone(); }
        catch { return null; }

        PatchSceneHeader(sceneBytes, scene.Settings);

        // One patched room per imported room, in order (the editor mirrors imported rooms 1:1).
        var rooms = new List<byte[]>(src.Rooms.Count);
        for (int i = 0; i < src.Rooms.Count; i++)
        {
            byte[] orig;
            try { orig = rom.GetFile(src.Rooms[i].FileIndex); } catch { return null; }
            var actors = i < scene.Rooms.Count ? scene.Rooms[i].Actors : (IReadOnlyList<ZActor>)[];
            // Transition actors export at the scene level, not the room 0x01 list — exclude them.
            var roomActors = actors.Where(a => !a.IsTransition).ToList();
            rooms.Add(RetainedRoomPatcher.PatchActors(orig, roomActors, mm));
        }
        return (sceneBytes, rooms);
    }

    // Applies the editable header fields onto the verbatim scene: sound (0x15) + skybox (0x11) inline,
    // spawn position (0x00 → spawn list) and the first environment entry (0x0F → env list) by pointer.
    private static void PatchSceneHeader(byte[] d, SceneSettings s)
    {
        for (int p = 0; p + 8 <= d.Length; p += 8)
        {
            byte op = d[p];
            if (op == 0x14) break;
            switch (op)
            {
                case 0x15:   // sound: [15 reverb 00 00 | 00 00 nightSfx music]
                    d[p + 6] = s.NightSfx; d[p + 7] = s.MusicSeq;
                    break;
                case 0x11:   // skybox: [11 00 00 00 | skybox cloudy indoor 00]
                    d[p + 4] = s.SkyboxId; d[p + 5] = (byte)(s.Cloudy ? 1 : 0); d[p + 6] = (byte)(s.IndoorLighting ? 1 : 0);
                    break;
                case 0x00:   // spawn list: patch the first (Link) spawn actor's position + yaw
                {
                    int off = Seg2(d, p + 4);
                    if (off >= 0 && off + 16 <= d.Length)
                    {
                        WriteU16(d, off + 2, (ushort)(short)MathF.Round(s.SpawnPos.X));
                        WriteU16(d, off + 4, (ushort)(short)MathF.Round(s.SpawnPos.Y));
                        WriteU16(d, off + 6, (ushort)(short)MathF.Round(s.SpawnPos.Z));
                        WriteU16(d, off + 10, (ushort)s.SpawnYaw);
                    }
                    break;
                }
                case 0x0F:   // environment list: patch entry 0 (22 bytes) with the edited lighting
                {
                    int off = Seg2(d, p + 4);
                    if (off >= 0 && off + 22 <= d.Length)
                    {
                        d[off] = s.Ambient.R; d[off + 1] = s.Ambient.G; d[off + 2] = s.Ambient.B;
                        d[off + 3] = (byte)s.Light1DirX; d[off + 4] = (byte)s.Light1DirY; d[off + 5] = (byte)s.Light1DirZ;
                        d[off + 6] = s.Light1Col.R; d[off + 7] = s.Light1Col.G; d[off + 8] = s.Light1Col.B;
                        d[off + 9] = (byte)s.Light2DirX; d[off + 10] = (byte)s.Light2DirY; d[off + 11] = (byte)s.Light2DirZ;
                        d[off + 12] = s.Light2Col.R; d[off + 13] = s.Light2Col.G; d[off + 14] = s.Light2Col.B;
                        d[off + 15] = s.FogColor.R; d[off + 16] = s.FogColor.G; d[off + 17] = s.FogColor.B;
                        WriteU16(d, off + 18, s.FogNear); WriteU16(d, off + 20, s.FogFar);
                    }
                    break;
                }
            }
        }
    }

    private static int Seg2(byte[] d, int o)
    {
        uint v = (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
        return (v >> 24) == 2 ? (int)(v & 0x00FFFFFF) : -1;
    }
    private static void WriteU16(byte[] d, int o, ushort v) { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }
}
