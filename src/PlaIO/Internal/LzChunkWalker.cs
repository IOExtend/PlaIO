using System.Buffers.Binary;

namespace PlaIO.Internal;

/// <summary>Walks the chunk chain of a mode 2 (LZ) block payload: chunk headers only, no decompression.</summary>
internal static class LzChunkWalker
{
    public static List<PlaLzChunk> Walk(ReadOnlySpan<byte> payload, long blockOffset)
    {
        var chunks = new List<PlaLzChunk>();
        int pos = 0;
        long sum = 0;
        while (pos < payload.Length)
        {
            bool longHeader = (payload[pos] & 0x02) != 0;
            int headerLength = longHeader ? 9 : 3;
            if (pos + headerLength > payload.Length)
                throw new InvalidDataException($"Mode-2 block at 0x{blockOffset:x}: the chunk at payload offset {pos} has no room for its {headerLength}-byte header.");
            long packed = longHeader
                ? BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos + 1, 4))
                : payload[pos + 1];
            long unpacked = longHeader
                ? BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos + 5, 4))
                : payload[pos + 2];
            if (packed < headerLength)
                throw new InvalidDataException($"Mode-2 block at 0x{blockOffset:x}: the chunk at payload offset {pos} declares {packed} packed bytes, less than its {headerLength}-byte header.");
            if (pos + packed > payload.Length)
                throw new InvalidDataException($"Mode-2 block at 0x{blockOffset:x}: the chunk at payload offset {pos} runs past the payload by {pos + packed - payload.Length} bytes.");
            sum += unpacked;
            chunks.Add(new PlaLzChunk(pos, headerLength, packed - headerLength, unpacked, (payload[pos] & 0x01) != 0));
            pos += (int)packed;
        }
        return chunks;
    }
}
