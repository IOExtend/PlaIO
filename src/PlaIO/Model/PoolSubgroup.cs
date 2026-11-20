namespace PlaIO.Model;

/// <summary>A BL-tagged record in a decoded compressed pool.</summary>
public sealed class PoolSubgroup
{
    internal PoolSubgroup(PlaBlock header, byte[] data, bool partial)
    {
        Offset = header.Offset;
        Kind = header.Kind;
        Version = header.Version;
        Flags = header.Flags;
        Mode = header.Mode;
        ObjectId = header.Guid;
        Data = data;
        Partial = partial;
    }

    /// <summary>The offset of the header in the decoded pool.</summary>
    public long Offset { get; }

    /// <summary>The raw class number from the header.</summary>
    public int Kind { get; }

    /// <summary>The version byte.</summary>
    public int Version { get; }

    /// <summary>The raw header flags.</summary>
    public int Flags { get; }

    /// <summary>The storage mode of the payload.</summary>
    public PlaBlockMode Mode { get; }

    /// <summary>The object GUID when present in the header.</summary>
    public Guid? ObjectId { get; }

    /// <summary>The decoded payload. Mode 2 returns empty data pending a supported decoder.</summary>
    public byte[] Data { get; }

    /// <summary>True when decoding is unavailable or the zero-mask stream ends inside a literal group.
    /// A group-boundary ending can have a valid zero-filled tail with this property false.</summary>
    public bool Partial { get; }
}
