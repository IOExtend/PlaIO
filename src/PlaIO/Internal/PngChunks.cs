using System.Buffers.Binary;

namespace PlaIO.Internal;

internal static class PngChunks
{
    /// <summary>Length of the PNG image at <paramref name="start"/>: the signature and every chunk through IEND, CRC
    /// included. Chunk lengths are big-endian (PNG standard); CRCs are not checked.</summary>
    public static long Measure(Stream stream, long start, long fileLength)
    {
        // Chunk headers come from a 64 KiB buffer instead of one read call each. Not disposed: that would close the caller's
        // stream.
        stream = new BufferedStream(stream, 1 << 16);
        Span<byte> buffer = stackalloc byte[8];
        if (fileLength - start < buffer.Length)
            throw new InvalidDataException($"No PNG signature at offset 0x{start:x}: the file ends there.");
        StreamReads.ReadExactlyAt(stream, start, buffer, "the PNG signature");
        if (!buffer.SequenceEqual(FdbFormat.PngSignature))
            throw new InvalidDataException($"No PNG signature at offset 0x{start:x}.");

        long pos = start + buffer.Length;
        while (true)
        {
            if (fileLength - pos < buffer.Length)
                throw new InvalidDataException($"The PNG chunk walk reached the end of the file at offset 0x{pos:x} without an IEND chunk.");
            StreamReads.ReadExactlyAt(stream, pos, buffer, "a PNG chunk header");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            if (length > int.MaxValue)
                throw new InvalidDataException($"The PNG chunk at offset 0x{pos:x} declares {length} bytes; PNG allows at most 2^31-1.");
            long chunkLength = 12L + length;
            if (chunkLength > fileLength - pos)
                throw new InvalidDataException($"The PNG chunk at offset 0x{pos:x} declares {length} bytes, past the end of the file.");
            pos += chunkLength;
            if (buffer[4..].SequenceEqual("IEND"u8)) return pos - start;
        }
    }
}
