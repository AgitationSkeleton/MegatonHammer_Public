namespace MegatonHammer.Otr;

/// <summary>
/// Builds a libultraship/SoH <c>SOH_AudioSequence</c> ("OSEQ") V2 binary resource wrapping a raw N64
/// sequence binary (e.g. one extracted from the OTHER game's audioseq for cross-game music). The V2 factory
/// (<c>ResourceFactoryBinaryAudioSequenceV2</c>, shared by SoH and 2Ship) reads, after the 64-byte resource
/// header, little-endian:
///   u32 seqDataSize, u8[seqDataSize] seqData, u8 seqNumber, u8 medium, u8 cachePolicy, u32 numFonts, u8[numFonts] fonts
/// The raw sequence bytes are stored verbatim (they are the N64 sequence bytecode the audio engine runs).
/// <paramref name="fontId"/> is the soundfont the sequence plays through — cross-game tracks are restricted to
/// ones whose instruments exist in both games, so a target-game font renders them acceptably (the same
/// trade-off the N64 path makes by keeping the host slot's font).
/// </summary>
public static class OtrSequenceResource
{
    private const byte MEDIUM_CART = 2;   // z64audio.h SampleMedium: 0=RAM 1=UNK 2=CART — any non-UNK copies seqData
    private const byte CACHE_TEMPORARY = 2; // typical background-music cache policy (discarded on scene change)

    public static byte[] Build(byte[] rawSeq, int seqNumber, int fontId)
    {
        var w = new OtrResourceWriter(OtrResType.AudioSequence, version: 2);
        w.U32((uint)rawSeq.Length);          // seqDataSize
        w.Bytes(rawSeq);                     // seqData (raw N64 sequence bytecode, verbatim)
        w.U8((byte)seqNumber);               // seqNumber -> the seqId this resource claims
        w.U8(MEDIUM_CART);                   // medium
        w.U8(CACHE_TEMPORARY);               // cachePolicy
        w.U32(1);                            // numFonts
        w.U8((byte)fontId);                  // fonts[0]
        return w.ToArray();
    }
}
