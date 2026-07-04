using System.IO.Compression;
using MegatonHammer.Otr;

namespace MegatonHammer.Export;

/// <summary>
/// Editor-side custom-music injection for the OTR/O2R (SoH / 2Ship) path: packs an N64 sequence binary
/// (a user-picked .seq/.aseq, or one extracted from the OTHER game's audioseq for cross-game music) into a
/// playtest O2R as a proper <c>SOH_AudioSequence</c> ("OSEQ") V2 resource, then records it in mh/info's
/// "music" object.
///
/// The engine maps seqId -> resource via <c>sequenceMap[seqNumber]</c>, which is sized to the vanilla
/// sequence count — so a brand-new high id (the old 0x7F) is out of bounds and silently dropped. Instead we
/// claim a valid VANILLA host id and have the fork's boot hook force <c>sequenceMap[hostId]</c> to point at
/// our resource (deterministic override; the level is custom, so displacing that vanilla track is harmless).
/// The scene's 0x15 SetSoundSettings then references <paramref name="hostSeqId"/> and plays our sequence
/// through the chosen font.
/// </summary>
public static class SequenceInjector
{
    /// <summary>Fixed resource path the boot hook force-maps the host seqId to.</summary>
    public const string ResourcePath = "audio/sequences/mh_cross";

    /// <summary>A valid vanilla seqId to host the injected sequence (OoT Hyrule Field / MM Termina Field —
    /// both in range and harmless to displace inside a custom playtest level).</summary>
    public static int HostSeqId(bool mm) => 0x02;

    /// <summary>Wraps <paramref name="rawSeq"/> in an OSEQ V2 resource that claims <paramref name="hostSeqId"/>,
    /// stores it in the O2R, and merges a "music" object into mh/info so the boot hook can bind it.
    /// <paramref name="fontId"/> is the soundfont the sequence plays through (default 0 = primary bank).</summary>
    public static void PackInto(string o2rPath, int hostSeqId, byte[] rawSeq, int fontId = 0)
    {
        if (!File.Exists(o2rPath)) throw new FileNotFoundException("playtest O2R not found", o2rPath);
        byte[] resource = OtrSequenceResource.Build(rawSeq, hostSeqId, fontId);

        using var zip = ZipFile.Open(o2rPath, ZipArchiveMode.Update);

        zip.GetEntry(ResourcePath)?.Delete();
        using (var s = zip.CreateEntry(ResourcePath, CompressionLevel.Optimal).Open()) s.Write(resource, 0, resource.Length);

        // Merge a "music" object into mh/info so the boot hook can force sequenceMap[hostSeqId] -> ResourcePath.
        var info = zip.GetEntry(O2RPacker.InfoEntry);
        string json;
        using (var r = new StreamReader(info!.Open())) json = r.ReadToEnd();
        string music = $"\"music\":{{\"seqId\":{hostSeqId},\"fontId\":{fontId},\"path\":\"{ResourcePath}\",\"size\":{rawSeq.Length}}}";
        json = json.TrimEnd().EndsWith('}')
            ? json.TrimEnd()[..^1] + (json.Contains("\"music\"") ? "" : "," + music) + "}"
            : json;
        info.Delete();
        using (var w = new StreamWriter(zip.CreateEntry(O2RPacker.InfoEntry, CompressionLevel.Optimal).Open()))
            w.Write(json);
    }
}
