using System.Text;

namespace MegatonHammer.Otr;

/// <summary>
/// Resource type codes (the 4-byte tag at header offset 0x04, stored as a little-endian
/// uint32). The ASCII reads reversed because of the LE store, e.g. "OTEX" → 0x4F544558.
/// Values verified against libultraship fast/ship ResourceType.h.
/// </summary>
public static class OtrResType
{
    public const uint DisplayList = 0x4F444C54; // "ODLT"
    public const uint Vertex      = 0x4F565458; // "OVTX"
    public const uint Texture     = 0x4F544558; // "OTEX"
    public const uint Array       = 0x4F415252; // "OARR"
    public const uint Blob        = 0x4F424C42; // "OBLB"
    public const uint Scene       = 0x4F53434E; // "OSCN" (SohResourceType SOH_Scene)
    public const uint TexAnim     = 0x4F54414E; // "OTAN" (2Ship ResourceType TSH_TexAnim) — animated materials
    public const uint AudioSequence = 0x4F534551; // "OSEQ" (SOH_AudioSequence) — custom / cross-game music
}

/// <summary>
/// Writes one libultraship/SoH binary resource: a fixed 64-byte header followed by the
/// factory-specific payload. All multi-byte values are little-endian (header byte 0 = 0),
/// matching what the runtime resource factories read after seeking past the header.
/// Strings use libultraship's BinaryReader.ReadString format: int32 length prefix + raw
/// ASCII bytes (no null terminator).
/// </summary>
public sealed class OtrResourceWriter
{
    private readonly MemoryStream _ms = new();
    private readonly BinaryWriter _w;   // BinaryWriter is little-endian on every .NET platform

    public const int HeaderSize = 64;

    public OtrResourceWriter(uint resourceType, uint version = 0)
    {
        _w = new BinaryWriter(_ms, Encoding.ASCII, leaveOpen: true);

        _w.Write((byte)0x00);                 // 0x00 endianness: 0 = little
        _w.Write((byte)0x00);                 // 0x01 isCustom: 0 (match vanilla exports)
        _w.Write((byte)0x00);                 // 0x02 padding
        _w.Write((byte)0x00);                 // 0x03 padding
        _w.Write(resourceType);               // 0x04 resource type tag (u32)
        _w.Write(version);                    // 0x08 resource version (u32)
        _w.Write(0xDEADBEEFDEADBEEFUL);       // 0x0C id (u64, reserved sentinel)
        while (_ms.Position < HeaderSize)     // 0x14..0x40 reserved, zero-filled
            _w.Write((byte)0x00);
    }

    public void U8(byte v)    => _w.Write(v);
    public void U16(ushort v) => _w.Write(v);
    public void S16(short v)  => _w.Write(v);
    public void U32(uint v)   => _w.Write(v);
    public void S32(int v)    => _w.Write(v);
    public void U64(ulong v)  => _w.Write(v);
    public void F32(float v)  => _w.Write(v);
    public void Bytes(ReadOnlySpan<byte> b) => _w.Write(b);

    /// <summary>Pads with zero bytes until the payload is aligned to <paramref name="align"/>.</summary>
    public void Align(int align)
    {
        while ((_ms.Position - HeaderSize) % align != 0) _w.Write((byte)0x00);
    }

    /// <summary>libultraship BinaryReader.ReadString: int32 length + ASCII bytes, no null.</summary>
    public void Str(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s ?? string.Empty);
        _w.Write(bytes.Length);
        _w.Write(bytes);
    }

    public byte[] ToArray()
    {
        _w.Flush();
        return _ms.ToArray();
    }
}
