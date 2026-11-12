using System.Buffers.Binary;

namespace PlaIO;

/// <summary>The 64-byte header of an FDB container. Bytes 0..31 are fixed; bytes 32..63 overlap the first block and their
/// meaning is not documented, so they are only kept raw.</summary>
public sealed class FdbHeader
{
    /// <summary>Size of the header in bytes.</summary>
    public const int Size = 64;

    private readonly byte[] _raw;

    internal FdbHeader(byte[] raw)
    {
        _raw = raw;
        LayoutVersion = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(8));
        FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(10));
        Field12 = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
    }

    /// <summary>The u16 at offset 8 (layout version; 0x0010 in every known file).</summary>
    public ushort LayoutVersion { get; }

    /// <summary>The u16 at offset 10 (format version; 0x0002 in every known file).</summary>
    public ushort FormatVersion { get; }

    /// <summary>The u32 at offset 12; its meaning is not documented.</summary>
    public uint Field12 { get; }

    /// <summary>The first min(64, file length) bytes, verbatim.</summary>
    public ReadOnlyMemory<byte> Raw => _raw;
}
