namespace PlaIO;

/// <summary>One object record inside a compressed pool: a BL-tagged header with the object's class id, a GUID and a
/// payload decoded from the pool's compression (raw or zero mask). The meaning of the class id and of the payload's
/// fields is not documented; observed payloads store text as a u32 length followed by UTF-16 little-endian data and
/// numbers as little-endian doubles and integers.</summary>
public sealed class PoolSubgroup
{
    internal PoolSubgroup(long offset, int kind, int version, int flags, PlaBlockMode mode, Guid? objectId, byte[] data, bool partial)
    {
        Offset = offset;
        Kind = kind;
        Version = version;
        Flags = flags;
        Mode = mode;
        ObjectId = objectId;
        Data = data;
        Partial = partial;
    }

    /// <summary>Offset of the subgroup header inside the decoded pool (see <see cref="CompressedPool.Decode"/>).</summary>
    public long Offset { get; }

    /// <summary>The object's class id (the kind field of the BL header). Values seen in real files run from small ids to
    /// 0xFFDC, which the table of contents uses as its free-block marker.</summary>
    public int Kind { get; }

    /// <summary>The header's version byte, 0 or 1.</summary>
    public int Version { get; }

    /// <summary>The header's flags: bit 15..14 pair is the 0x8080 marker, 0x20 marks the GUID, 0x100 and 0x200 select the
    /// payload modes, the low 4 bits are the packed size class.</summary>
    public int Flags { get; }

    /// <summary>The payload's storage: raw (0) or zero mask (1). Mode 2 (LZ) is reported but its payload is not decoded;
    /// it has not been met inside the pools examined so far.</summary>
    public PlaBlockMode Mode { get; }

    /// <summary>The object's GUID, present when flag 0x20 is set.</summary>
    public Guid? ObjectId { get; }

    /// <summary>The decoded payload. For mode 2 subgroups this is empty.</summary>
    public byte[] Data { get; }

    /// <summary>Whether the payload stream ended inside a group — a truncated literal. The decoded <see cref="Data"/> then
    /// holds the bytes the stream produced and the tail of the buffer is reconstructed with zeros; such a record is
    /// damaged and its tail must not be trusted.</summary>
    public bool Partial { get; }
}
