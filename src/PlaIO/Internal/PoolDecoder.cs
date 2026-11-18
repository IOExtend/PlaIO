using System.Buffers.Binary;

namespace PlaIO.Internal;

/// <summary>The compressed storage pools that live in the gaps between blocks. A pool starts with the 16-byte compression
/// sign, then the decompressed total and the block count (both unsigned 64-bit little endian), then that many blocks, each
/// a u32 compressed size followed by the block. A block is a flag byte (bit 0: the payload is LZ77-triple coded, otherwise
/// copied; bit 1: the sizes are 32-bit, otherwise bytes), the packed size (equal to the block's compressed size), the
/// unpacked size and the payload. The blocks decode to exactly the declared total. The layout and the codec follow the
/// format notes of this repository and are confirmed against the sample files.</summary>
internal static class PoolDecoder
{
    /// <summary>The compression sign that opens every compressed pool.</summary>
    public static ReadOnlySpan<byte> Sign => [0x79, 0xA8, 0xA9, 0x96, 0x45, 0xB4, 0x4A, 0xF3, 0x98, 0x38, 0x33, 0xC3, 0x3A, 0x06, 0x1B, 0x06];

    /// <summary>At most one megabyte and change of packed payload per block; the reader allocates a buffer of this size
    /// plus a byte for the block.</summary>
    public const int MaxCompressedBlock = 1048976;

    /// <summary>The pools of the stream: every compression sign whose header parses and whose block headers tile the
    /// declared total inside the file. The scan continues past an accepted pool; a rejected sign is retried one byte on.
    /// The <paramref name="excluded"/> ranges are skipped entirely: a sign inside them is not considered (the document
    /// passes the byte ranges of its indexed blocks, whose payloads can carry the sign as data). Returns the pool
    /// descriptions in file order.</summary>
    public static List<(long Offset, long End, ulong Total, int BlockCount)> Find(Stream stream, long length, IReadOnlyList<(long Start, long End)>? excluded)
    {
        var pools = new List<(long Offset, long End, ulong Total, int BlockCount)>();
        var window = new byte[1 << 20];
        long pos = 0;
        while (pos + Sign.Length <= length)
        {
            // Step over an excluded range whole: its signs are data, not pool starts.
            if (excluded is not null)
                foreach (var (start, end) in excluded)
                {
                    if (pos >= start && pos < end) pos = end;
                }
            if (pos + Sign.Length > length) break;
            int count = (int)Math.Min(window.Length, length - pos);
            StreamReads.ReadExactlyAt(stream, pos, window.AsSpan(0, count), "the pool scan");
            int at = IndexOfSign(window.AsSpan(0, count));
            if (at < 0 || pos + at + Sign.Length > length)
            {
                // The next scan start keeps a full sign inside the next window; a sign can straddle the boundary.
                pos = count == window.Length ? pos + count - (Sign.Length - 1) : length;
                continue;
            }
            long signAt = pos + at;
            if (Inside(excluded, signAt))
            {
                pos = signAt + 1;
                continue;
            }
            if (TryValidate(stream, length, signAt, out var pool))
            {
                pools.Add(pool);
                pos = pool.End;
            }
            else
            {
                pos = signAt + 1;
            }
        }
        return pools;
    }

    /// <summary>Decodes the pool at <paramref name="offset"/>: its blocks, each memcpy or LZ77-triple, concatenated.
    /// Throws when the blocks do not decode or do not add up to the declared total.</summary>
    public static byte[] Decode(Stream stream, long offset, long length)
    {
        var (total, blocks) = ReadBlocks(stream, offset, length);
        if (total > int.MaxValue) throw Bad(offset, $"its declared total {total} exceeds the array limit");
        var output = new byte[total];
        long written = 0;
        foreach (var (payloadAt, packed, unpacked) in blocks)
        {
            var payload = new byte[packed];
            StreamReads.ReadExactlyAt(stream, payloadAt, payload, $"pool at 0x{offset:x}");
            int flag = payload[0];
            int header = (flag & 0b10) != 0 ? 9 : 3;
            int produced = (flag & 1) != 0
                ? DecodeTriple(payload, output.AsSpan((int)written, (int)unpacked), (int)unpacked)
                : CopyPayload(payload[header..], output.AsSpan((int)written, (int)unpacked));
            if (produced != (int)unpacked)
                throw new InvalidDataException($"Pool at 0x{offset:x}: block at 0x{payloadAt:x} produced {produced} of {unpacked} bytes.");
            written += unpacked;
        }
        if (written != (long)total)
            throw new InvalidDataException($"Pool at 0x{offset:x}: blocks produced {written} of {total} bytes.");
        return output;
    }

    /// <summary>The block layout of the pool whose sign sits at <paramref name="offset"/>: the u64 total and block count
    /// after the sign, then one entry per block with its payload offset, packed and unpacked size. The declared total can
    /// exceed the container (the codec is compressing); only the array limit and the file bounds of every block header
    /// and payload are checked. The sizes must tile the declared total exactly.</summary>
    private static (ulong Total, List<(long PayloadAt, int Packed, int Unpacked)> Blocks) ReadBlocks(Stream stream, long offset, long length)
    {
        var header = new byte[16];
        if (offset + Sign.Length + header.Length > length) throw Bad(offset, "its header runs past the file");
        StreamReads.ReadExactlyAt(stream, offset + Sign.Length, header, $"pool at 0x{offset:x}");
        ulong total = BinaryPrimitives.ReadUInt64LittleEndian(header);
        ulong blockCount = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        if (total > int.MaxValue) throw Bad(offset, $"its declared total {total} exceeds the array limit");
        var blocks = new List<(long, int, int)>();
        long pos = offset + Sign.Length + header.Length;
        ulong sum = 0;
        for (ulong i = 0; i < blockCount; i++)
        {
            var sizeBytes = new byte[4];
            if (pos + 4 > length) throw Bad(offset, "the block list runs past the file");
            StreamReads.ReadExactlyAt(stream, pos, sizeBytes, $"pool at 0x{offset:x}");
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);
            pos += 4;
            if (packed < 3 || packed > MaxCompressedBlock || pos + packed > length) throw Bad(offset, $"block {i} declares {packed} packed bytes");
            var flagBytes = new byte[1];
            StreamReads.ReadExactlyAt(stream, pos, flagBytes, $"pool at 0x{offset:x}");
            int flag = flagBytes[0];
            bool big = (flag & 0b10) != 0;
            int headerLength = big ? 9 : 3;
            if (packed < headerLength) throw Bad(offset, $"block {i} declares {packed} packed bytes, less than its {headerLength}-byte header");
            var head = new byte[headerLength];
            StreamReads.ReadExactlyAt(stream, pos, head, $"pool at 0x{offset:x}");
            uint declaredPacked = big ? BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1)) : head[1];
            if (declaredPacked != packed) throw Bad(offset, $"block {i} header declares {declaredPacked} packed bytes, the pool lists {packed}");
            uint unpacked = big ? BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(5)) : head[2];
            if (sum + unpacked > total) throw Bad(offset, $"block {i} overruns the declared total");
            blocks.Add((pos, (int)packed, (int)unpacked));
            sum += unpacked;
            pos += packed;
        }
        if (sum != total) throw Bad(offset, $"its blocks add up to {sum}, not the declared {total}");
        return (total, blocks);
    }

    /// <summary>Whether the header and every block header of the pool at <paramref name="offset"/> parse and the declared
    /// sizes tile the total inside the file.</summary>
    private static bool TryValidate(Stream stream, long length, long offset, out (long Offset, long End, ulong Total, int BlockCount) pool)
    {
        pool = default;
        try
        {
            var (total, blocks) = ReadBlocks(stream, offset, length);
            pool = (offset, blocks.Count > 0 ? blocks[^1].PayloadAt + blocks[^1].Packed : offset + Sign.Length + 16, total, blocks.Count);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>The first offset of the compression sign in <paramref name="window"/>, or -1.</summary>
    private static int IndexOfSign(ReadOnlySpan<byte> window)
    {
        int at = 0;
        while (true)
        {
            int next = window[at..].IndexOf(Sign[0]);
            if (next < 0) return -1;
            at += next;
            if (at + Sign.Length > window.Length) return -1;
            if (window.Slice(at, Sign.Length).SequenceEqual(Sign)) return at;
            at++;
        }
    }

    /// <summary>Whether <paramref name="at"/> falls inside one of the excluded ranges.</summary>
    private static bool Inside(IReadOnlyList<(long Start, long End)>? excluded, long at)
    {
        if (excluded is null) return false;
        foreach (var (start, end) in excluded)
            if (start <= at && at < end) return true;
        return false;
    }

    /// <summary>The payload of a memcpy block (the bytes after the block header, already sliced), at most
    /// <paramref name="dst"/> bytes.</summary>
    private static int CopyPayload(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        int source = Math.Min(block.Length, dst.Length);
        block[..source].CopyTo(dst);
        return source;
    }

    /// <summary>The LZ77-triple codec. The stream is a little-endian flag word (a 1 starts a new word) and then, per flag
    /// bit: a set bit takes a token whose low 16 bits name a slot of the pointer table (the top 12 bits, shifted right by
    /// 4) and a length ((the low 4 bits) + 2, or a following byte when 0); the copy reads from that slot and runs in
    /// 3-byte steps over unsigned 32-bit reads, so the last step may write up to two bytes past the length. A clear bit
    /// takes a token whose first four bytes go to the output, of which the literal-length table keeps the first. The last
    /// ten bytes go out one by one. Every position up to a few bytes behind the output position is registered into the
    /// 4096-slot table, hashed from the four output bytes at position+1. An out-of-range slot or length stops the
    /// stream.</summary>
    private static int DecodeTriple(ReadOnlySpan<byte> block, Span<byte> output, int unpacked)
    {
        var table = new long[4096];
        Array.Fill(table, -1);

        bool big = (block[0] & 0b10) != 0;
        int packedSize = big ? (int)BinaryPrimitives.ReadUInt32LittleEndian(block[1..]) : block[1];
        int endIn = packedSize;                    // the last valid input index is endIn - 1
        int src = 3 + (big ? 6 : 0);               // the payload starts after the header
        int outPos = 0;
        uint flags = 1;
        long cursor = -1;
        int outEnd = unpacked - 1;

        uint token = 0;
        while (true)
        {
            while (true)
            {
                if (flags == 1)
                {
                    if (src + 3 > endIn - 1) return outPos;
                    flags = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(src));
                    src += 4;
                }
                if (src + 3 > endIn - 1) return outPos;
                token = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(src));
                if ((flags & 1) == 0) break;

                flags >>= 1;
                src += 2;
                long source = table[(ushort)token >> 4];
                int length;
                int low = (int)(token & 0xF);
                if (low != 0) length = low + 2;
                else
                {
                    length = block[src];
                    src += 1;
                }
                if (source < 0 || source > outPos - 3 || length > outEnd - outPos - 3) return outPos;
                int step = 0;
                while (step < length)
                {
                    // One 32-bit read per 3-byte step; the tail may run up to two bytes past the length.
                    output[outPos + step] = output[(int)source + step];
                    output[outPos + step + 1] = output[(int)source + step + 1];
                    output[outPos + step + 2] = output[(int)source + step + 2];
                    output[outPos + step + 3] = output[(int)source + step + 3];
                    step += 3;
                }
                outPos += length;
                Register(table, ref cursor, outPos - length, output);
                cursor = outPos - 1;
            }

            if (outPos >= outEnd - 10) break;
            output[outPos] = (byte)token;
            output[outPos + 1] = (byte)(token >> 8);
            output[outPos + 2] = (byte)(token >> 16);
            output[outPos + 3] = (byte)(token >> 24);
            int literals = LiteralLengths[flags & 0xF];
            outPos += literals;
            flags >>= literals;
            src += literals;
            Register(table, ref cursor, outPos - 3, output);
        }

        while (outPos <= outEnd)
        {
            uint f = flags;
            if (flags == 1) flags = 0x80000000;
            int at = f == 1 ? src + 4 : src;
            if (at >= endIn) return outPos;
            output[outPos] = block[at];
            src = at + 1;
            outPos++;
            flags >>= 1;
        }
        Register(table, ref cursor, outEnd - 3, output);
        return unpacked;
    }

    private static readonly int[] LiteralLengths = [4, 0, 1, 0, 2, 0, 1, 0, 3, 0, 1, 0, 2, 0, 1, 0];

    /// <summary>Registers every output position below <paramref name="limit"/> that the cursor has not reached yet: the
    /// hash of the four output bytes at position+1 indexes the slot that stores the position.</summary>
    private static void Register(long[] table, ref long cursor, long limit, ReadOnlySpan<byte> output)
    {
        while (cursor < limit)
        {
            int at = (int)(cursor + 1);
            uint word = (uint)(output[at] | output[at + 1] << 8 | output[at + 2] << 16 | output[at + 3] << 24);
            int hash = (int)(((ushort)word ^ (ushort)(word >> 12)) & 0xFFF);
            table[hash] = cursor + 1;
            cursor++;
        }
    }

    private static InvalidDataException Bad(long offset, string what) => new($"Pool at 0x{offset:x}: {what}.");
}
