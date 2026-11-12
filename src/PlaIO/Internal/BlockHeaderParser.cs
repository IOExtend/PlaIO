using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace PlaIO.Internal;

internal static class BlockHeaderParser
{
    /// <summary>Parses a block header that starts at <paramref name="at"/>[0], found at file offset <paramref name="offset"/>.
    /// The span may end before a full header near the end of the file. Returns false at the first rule that fails; never
    /// throws.</summary>
    public static bool TryParse(ReadOnlySpan<byte> at, long offset, long fileLength, [NotNullWhen(true)] out PlaBlock? block)
    {
        block = null;
        if (at.Length < 11) return false;
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(at);
        if (tag != FdbFormat.TagBL && tag != FdbFormat.TagDY) return false;
        byte version = at[8];
        if (version > 1) return false;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(at[9..]);
        if ((flags & FdbFormat.MarkerMask) != FdbFormat.Marker) return false;

        int p = 11;
        Guid? guid = null;
        if ((flags & FdbFormat.FlagGuid) != 0)
        {
            if (at.Length < p + 16) return false;
            guid = new Guid(at.Slice(p, 16));
            p += 16;
        }

        int sizeClass = flags & 0xF;
        int width = SizeClass.FieldWidth(sizeClass);
        if (at.Length < p + width) return false;
        ulong packed = width switch
        {
            2 => BinaryPrimitives.ReadUInt16LittleEndian(at[p..]),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(at[p..]),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(at[p..]),
        };
        p += width;
        if (packed > long.MaxValue) return false;
        if (SizeClass.Of(packed) != sizeClass) return false;

        var mode = (flags & FdbFormat.FlagZeroMask) != 0 ? PlaBlockMode.ZeroMask
            : (flags & FdbFormat.FlagLz) != 0 ? PlaBlockMode.Lz
            : PlaBlockMode.Raw;
        ulong unpacked = packed;
        if (mode != PlaBlockMode.Raw)
        {
            if (at.Length < p + 4) return false;
            unpacked = BinaryPrimitives.ReadUInt32LittleEndian(at[p..]);
            p += 4;
        }

        uint? extra = null;
        if ((flags & FdbFormat.FlagExtra) != 0)
        {
            if (at.Length < p + 4) return false;
            extra = BinaryPrimitives.ReadUInt32LittleEndian(at[p..]);
            p += 4;
        }

        long payloadOffset = offset + p;
        if (payloadOffset > fileLength || (long)packed > fileLength - payloadOffset) return false;
        if ((UInt128)unpacked > (UInt128)packed * FdbFormat.ExpansionFactor + FdbFormat.ExpansionSlack) return false;
        if (unpacked > FdbFormat.MaxUnpacked) return false;

        block = new PlaBlock(offset, tag == FdbFormat.TagBL ? "BL" : "DY",
            BinaryPrimitives.ReadUInt32LittleEndian(at[2..]), BinaryPrimitives.ReadUInt16LittleEndian(at[6..]),
            version, flags, mode, guid, (long)packed, (long)unpacked, extra, payloadOffset);
        return true;
    }
}
