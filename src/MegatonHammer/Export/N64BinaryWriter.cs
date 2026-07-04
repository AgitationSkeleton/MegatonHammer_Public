namespace MegatonHammer.Export;

/// <summary>
/// Builds a big-endian byte buffer for N64/Zelda 64 binary formats.
/// </summary>
public sealed class N64BinaryWriter
{
    private readonly List<byte> _buf = [];

    public int Position => _buf.Count;

    public void WriteU8(byte v)        => _buf.Add(v);
    public void WriteS8(sbyte v)       => _buf.Add((byte)v);

    public void WriteU16(ushort v)
    {
        _buf.Add((byte)(v >> 8));
        _buf.Add((byte)(v & 0xFF));
    }
    public void WriteS16(short v) => WriteU16((ushort)v);

    public void WriteU32(uint v)
    {
        _buf.Add((byte)(v >> 24));
        _buf.Add((byte)(v >> 16));
        _buf.Add((byte)(v >> 8));
        _buf.Add((byte)(v & 0xFF));
    }
    public void WriteS32(int v)  => WriteU32((uint)v);

    public void WriteU64(ulong v) { WriteU32((uint)(v >> 32)); WriteU32((uint)(v & 0xFFFFFFFF)); }

    // Segment pointer: upper byte = segment number, lower 3 bytes = offset
    public void WriteSegPtr(byte seg, int offset)
        => WriteU32((uint)(((uint)seg << 24) | (uint)(offset & 0x00FFFFFF)));

    public void AlignTo(int align)
    {
        while (_buf.Count % align != 0) _buf.Add(0);
    }

    // Overwrite a u32 at a previously-written position (for pointer fix-ups)
    public void PatchU32(int offset, uint value)
    {
        _buf[offset]     = (byte)(value >> 24);
        _buf[offset + 1] = (byte)(value >> 16);
        _buf[offset + 2] = (byte)(value >> 8);
        _buf[offset + 3] = (byte)(value & 0xFF);
    }

    public void WriteBytes(byte[] data) => _buf.AddRange(data);

    public byte[] ToArray() => [.. _buf];
}
