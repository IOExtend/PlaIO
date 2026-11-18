using System.Buffers.Binary;

namespace PlaIO.Internal;

/// <summary>The objects the object database keeps inside a compressed pool: BL-tagged records that follow the same header
/// layout as the file's blocks — tag, id, kind (the object's class id), version, flags (marker 0x8080, optional GUID,
/// mode bits), the packed size (a u16 for size classes 0..6, a u32 for 7..14, a u64 for 15) and, for modes 1 and 2, the
/// unpacked size as a u32. A mode 0 payload is stored raw; a mode 1 payload is a zero mask like the file blocks'; a mode
/// 2 payload is LZ-token coded — not met in the pools examined so far, reported with an empty payload. The records are
/// found by scanning for the tag and accepting the ones whose header parses; a rejected candidate is retried one byte
/// on. The decoded payloads tile the pool loosely: a mode 1 stream may end before its declared unpacked size is full —
/// the tail of the buffer stays zero, like the oversized buffers of the file's mode 2 blocks.</summary>
internal static class SubgroupParser
{
    public static List<PoolSubgroup> Parse(byte[] pool)
    {
        var result = new List<PoolSubgroup>();
        int pos = 0;
        while (pos + 11 <= pool.Length)
        {
            if (pool[pos] != 0x42 || pool[pos + 1] != 0x4C) { pos++; continue; } // "BL"
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(pos + 9));
            if ((flags & FdbFormat.MarkerMask) != FdbFormat.Marker) { pos++; continue; }
            int q = pos + 11;
            if ((flags & FdbFormat.FlagGuid) != 0)
            {
                if (q + 16 > pool.Length) break;
                q += 16;
            }
            int sizeClass = flags & 0xF;
            int width = sizeClass <= 6 ? 2 : sizeClass <= 14 ? 4 : 8;
            if (q + width > pool.Length) break;
            long packed = width switch
            {
                2 => BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(q)),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(pool.AsSpan(q)),
                _ => unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(pool.AsSpan(q))),
            };
            q += width;
            var mode = (flags & FdbFormat.FlagZeroMask) != 0 ? PlaBlockMode.ZeroMask
                : (flags & FdbFormat.FlagLz) != 0 ? PlaBlockMode.Lz
                : PlaBlockMode.Raw;
            long unpacked = packed;
            if (mode != PlaBlockMode.Raw)
            {
                if (q + 4 > pool.Length) break;
                unpacked = BinaryPrimitives.ReadUInt32LittleEndian(pool.AsSpan(q));
                q += 4;
            }
            if (packed < 0 || packed > pool.Length - q || unpacked < 0 || unpacked > (long)pool.Length * 32) { pos++; continue; }

            Guid? objectId = null;
            if ((flags & FdbFormat.FlagGuid) != 0)
                objectId = new Guid(pool.AsSpan(pos + 11, 16));

            var data = DecodePayload(pool.AsSpan(q, (int)packed), mode, unpacked);
            result.Add(new PoolSubgroup(pos, BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(pos + 6)),
                pool[pos + 8], flags, mode, objectId, data));
            pos = q + (int)packed;
        }
        return result;
    }

    private static byte[] DecodePayload(ReadOnlySpan<byte> body, PlaBlockMode mode, long unpacked)
    {
        if (unpacked > int.MaxValue) return [];
        var data = new byte[unpacked];
        switch (mode)
        {
            case PlaBlockMode.Raw:
                body[..Math.Min(body.Length, data.Length)].CopyTo(data);
                return data;
            case PlaBlockMode.ZeroMask:
                // The declared size is the buffer size; a stream that ends early leaves the tail zero.
                try
                {
                    PayloadDecoder.DecodeZeroMask(body, data);
                }
                catch (InvalidDataException)
                {
                    // Keep the partial fill; the tail stays zero.
                }
                return data;
            default:
                return []; // Mode 2 (LZ) is not met in the pools examined so far.
        }
    }
}
