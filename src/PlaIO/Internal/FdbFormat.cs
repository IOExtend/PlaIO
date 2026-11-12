namespace PlaIO.Internal;

/// <summary>Constants of the container layout. Values marked "reader limit" are choices of this reader that protect it
/// against hostile input; they are not facts of the format.</summary>
internal static class FdbFormat
{
    /// <summary>Bytes 0..7 of a .pla or .pln container.</summary>
    public static ReadOnlySpan<byte> FdbSignature => "ROF FDB "u8;

    /// <summary>Bytes 0..3 of a .bimx file.</summary>
    public static ReadOnlySpan<byte> BimxSignature => "BIMx"u8;

    /// <summary>The standard 8-byte PNG signature.</summary>
    public static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The header part that is always present before the first block.</summary>
    public const int FirstBlockOffset = 32;

    public const ushort TagBL = 0x4C42;
    public const ushort TagDY = 0x5944;

    public const ushort MarkerMask = 0xC0C0;
    public const ushort Marker = 0x8080;

    public const ushort FlagGuid = 0x20;
    public const ushort FlagZeroMask = 0x100;
    public const ushort FlagLz = 0x200;
    public const ushort FlagExtra = 0x400;

    /// <summary>tag, id, kind, ver, flags and a u16 packed size.</summary>
    public const int MinBlockHeaderLength = 13;

    /// <summary>Fixed part, GUID, u64 packed size, unpacked size and extra.</summary>
    public const int MaxBlockHeaderLength = 43;

    /// <summary>Plausibility limit of the block index: unpacked size at most 2^28.</summary>
    public const long MaxUnpacked = 1L << 28;

    /// <summary>Plausibility limit of the block index: unpacked at most packed * 300 + 65536.</summary>
    public const int ExpansionFactor = 300;

    public const int ExpansionSlack = 65536;

    /// <summary>Reader limit, not a format fact: the most blocks one index may hold.</summary>
    public const int MaxBlocks = 1 << 20;

    /// <summary>Reader limit, not a format fact: the largest .bimx cover image that is read into memory.</summary>
    public const long MaxCoverLength = 64L << 20;
}
