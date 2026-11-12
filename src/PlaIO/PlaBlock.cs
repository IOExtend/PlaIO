namespace PlaIO;

/// <summary>How the payload of a block is stored.</summary>
public enum PlaBlockMode
{
    /// <summary>Mode 0: the payload bytes as they are.</summary>
    Raw = 0,

    /// <summary>Mode 1 (flag 0x100): groups of a mask byte and one byte per set bit; clear bits stand for zero bytes.</summary>
    ZeroMask = 1,

    /// <summary>Mode 2 (flag 0x200): a compressed payload. Its compression is not documented, so it cannot be
    /// decoded.</summary>
    Lz = 2,
}

/// <summary>One block of the index: header fields as stored, plus where the payload lies. Fields whose meaning is not
/// documented (<see cref="Id"/>, <see cref="Kind"/>, <see cref="Extra"/>, flag 0x10) are exposed raw.</summary>
public sealed class PlaBlock
{
    internal PlaBlock(long offset, string tag, uint id, ushort kind, byte version, ushort flags, PlaBlockMode mode,
        Guid? guid, long packedSize, long unpackedSize, uint? extra, long payloadOffset)
    {
        Offset = offset;
        Tag = tag;
        Id = id;
        Kind = kind;
        Version = version;
        Flags = flags;
        Mode = mode;
        Guid = guid;
        PackedSize = packedSize;
        UnpackedSize = unpackedSize;
        Extra = extra;
        PayloadOffset = payloadOffset;
    }

    /// <summary>File offset of the block header.</summary>
    public long Offset { get; }

    /// <summary>"BL" or "DY".</summary>
    public string Tag { get; }

    /// <summary>The u32 at +2, raw; meaning not documented.</summary>
    public uint Id { get; }

    /// <summary>The u16 at +6, raw; meaning not documented.</summary>
    public ushort Kind { get; }

    /// <summary>The byte at +8: 0 or 1.</summary>
    public byte Version { get; }

    /// <summary>The u16 at +9, raw, including bits whose meaning is not documented.</summary>
    public ushort Flags { get; }

    /// <summary>The size class, <c>Flags &amp; 0xF</c>; it matches <see cref="PackedSize"/>.</summary>
    public int SizeClass => Flags & 0xF;

    /// <summary>The payload storage mode.</summary>
    public PlaBlockMode Mode { get; }

    /// <summary>The GUID stored when flag 0x20 is set.</summary>
    public Guid? Guid { get; }

    /// <summary>Size of the stored payload in bytes.</summary>
    public long PackedSize { get; }

    /// <summary>Size of the decoded payload in bytes; equals <see cref="PackedSize"/> for <see cref="PlaBlockMode.Raw"/>.</summary>
    public long UnpackedSize { get; }

    /// <summary>The u32 stored when flag 0x400 is set, raw; meaning not documented.</summary>
    public uint? Extra { get; }

    /// <summary>File offset of the payload.</summary>
    public long PayloadOffset { get; }

    /// <summary>Length of the block header in bytes.</summary>
    public int HeaderLength => (int)(PayloadOffset - Offset);

    /// <summary>File offset just past the payload.</summary>
    public long End => PayloadOffset + PackedSize;
}
