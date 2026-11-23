using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal readonly record struct PoolRange(long Offset, long End, int UnpackedSize, int FrameCount);

internal static class PoolReader
{
    private static ReadOnlySpan<byte> Signature =>
        [0x79, 0xA8, 0xA9, 0x96, 0x45, 0xB4, 0x4A, 0xF3, 0x98, 0x38, 0x33, 0xC3, 0x3A, 0x06, 0x1B, 0x06];

    private const int PoolHeaderLength = 32;
    private const int MinFrameEntryLength = 7;

    private readonly record struct Frame(long PayloadOffset, int PayloadLength, int UnpackedSize, bool IsCompressed);

    public static List<PoolRange> Find(Stream stream, long length, IReadOnlyList<PlaBlock> blocks)
    {
        var pools = new List<PoolRange>();
        var buffer = new byte[64 * 1024];
        long pos = FdbFormat.FirstBlockOffset;
        foreach (var block in blocks)
        {
            pos = ScanRange(stream, length, pos, block.PayloadOffset, buffer, pools);
            pos = Math.Max(pos, block.End);
        }
        ScanRange(stream, length, pos, length, buffer, pools);
        return pools;
    }

    private static long ScanRange(Stream stream, long length, long pos, long end, byte[] buffer, List<PoolRange> pools)
    {
        while (end - pos >= Signature.Length)
        {
            long windowStart = pos;
            var window = buffer.AsSpan(0, (int)Math.Min(buffer.Length, end - windowStart));
            StreamReads.ReadExactlyAt(stream, windowStart, window, "pool markers");
            pos = windowStart + window.Length - Signature.Length + 1;
            int from = 0;
            while (window.Length - from >= Signature.Length)
            {
                int index = window[from..].IndexOf(Signature);
                if (index < 0) break;
                long offset = windowStart + from + index;
                try
                {
                    var (range, _) = ReadLayout(stream, length, offset);
                    pools.Add(range);
                    if (range.End >= windowStart + window.Length)
                    {
                        pos = range.End;
                        break;
                    }
                    from = (int)(range.End - windowStart);
                    pos = Math.Max(pos, range.End);
                }
                catch (InvalidDataException)
                {
                    from += index + 1;
                }
            }
        }
        return pos;
    }

    public static byte[] Decode(Stream stream, long length, long offset, long maxDecodedBytes)
    {
        var (range, frames) = ReadLayout(stream, length, offset);
        if (range.UnpackedSize > maxDecodedBytes)
            throw InvalidPool(offset, $"the pool decodes to {range.UnpackedSize} bytes, past the reader's cap of {maxDecodedBytes}");
        byte[] decoded = new byte[range.UnpackedSize];
        int written = 0;
        foreach (var frame in frames)
        {
            DecodeFrame(stream, frame, decoded.AsSpan(written, frame.UnpackedSize));
            written += frame.UnpackedSize;
        }
        return decoded;
    }

    public static byte[] ReadPrefix(Stream stream, long length, long offset, int size, long maxDecodedBytes)
    {
        var (range, frames) = ReadLayout(stream, length, offset);
        byte[] prefix = new byte[Math.Min(size, range.UnpackedSize)];
        int written = 0;
        foreach (var frame in frames)
        {
            if (written == prefix.Length) break;
            int count = Math.Min(prefix.Length - written, frame.UnpackedSize);
            var target = prefix.AsSpan(written, count);
            if (!frame.IsCompressed)
                StreamReads.ReadExactlyAt(stream, frame.PayloadOffset, target, "a stored pool frame prefix");
            else
            {
                if (frame.UnpackedSize > maxDecodedBytes)
                    throw InvalidPool(offset, $"a frame exceeds the reader's cap of {maxDecodedBytes} bytes");
                var decoded = new byte[frame.UnpackedSize];
                DecodeFrame(stream, frame, decoded);
                decoded.AsSpan(0, count).CopyTo(target);
            }
            written += count;
        }
        return prefix;
    }

    private static void DecodeFrame(Stream stream, Frame frame, Span<byte> output)
    {
        if (frame.IsCompressed)
        {
            byte[] input = new byte[frame.PayloadLength];
            StreamReads.ReadExactlyAt(stream, frame.PayloadOffset, input, "a compressed pool frame");
            QuickLzLevelOne.Decode(input, output);
        }
        else
        {
            StreamReads.ReadExactlyAt(stream, frame.PayloadOffset, output, "a stored pool frame");
        }
    }

    private static (PoolRange Range, List<Frame> Frames) ReadLayout(Stream stream, long length, long offset)
    {
        if (length - offset < PoolHeaderLength)
            throw InvalidPool(offset, "the pool header is cut short");

        Span<byte> header = stackalloc byte[PoolHeaderLength];
        StreamReads.ReadExactlyAt(stream, offset, header, "a pool header");
        if (!header[..Signature.Length].SequenceEqual(Signature))
            throw InvalidPool(offset, "the pool marker is missing");

        ulong total = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
        long pos = offset + PoolHeaderLength;
        if (total > (ulong)Array.MaxLength || count > int.MaxValue || count > (ulong)((length - pos) / MinFrameEntryLength))
            throw InvalidPool(offset, "the declared pool size or frame count is outside the supported bounds");

        var frames = new List<Frame>();
        ulong written = 0;
        Span<byte> entry = stackalloc byte[13];
        for (int index = 0; index < (int)count; index++)
        {
            if (length - pos < MinFrameEntryLength)
                throw InvalidPool(offset, $"frame {index} has a truncated header");

            StreamReads.ReadExactlyAt(stream, pos, entry[..MinFrameEntryLength], "a pool frame header");
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            byte flags = entry[4];
            if ((flags & 0xFC) != 0x44)
                throw InvalidPool(offset, $"frame {index} does not use standalone QuickLZ level 1");

            bool longHeader = (flags & 2) != 0;
            int headerBytes = longHeader ? 9 : 3;
            if (packed < headerBytes || packed > length - pos - 4 || packed > Array.MaxLength)
                throw InvalidPool(offset, $"frame {index} has an invalid packed size");

            if (longHeader)
                StreamReads.ReadExactlyAt(stream, pos + MinFrameEntryLength, entry[MinFrameEntryLength..], "a long pool frame header");
            uint innerSize = longHeader ? BinaryPrimitives.ReadUInt32LittleEndian(entry[5..]) : entry[5];
            uint unpacked = longHeader ? BinaryPrimitives.ReadUInt32LittleEndian(entry[9..]) : entry[6];
            if (innerSize != packed || unpacked > total - written)
                throw InvalidPool(offset, $"frame {index} declares inconsistent sizes");

            bool compressed = (flags & 1) != 0;
            int payloadLength = (int)packed - headerBytes;
            // The longest match emits 255 bytes for a three-byte reference.
            if (compressed ? unpacked > 85L * payloadLength : unpacked != payloadLength)
                throw InvalidPool(offset, $"frame {index} cannot produce its declared size");

            frames.Add(new Frame(pos + 4 + headerBytes, payloadLength, (int)unpacked, compressed));
            written += unpacked;
            pos += 4 + (long)packed;
        }

        if (written != total)
            throw InvalidPool(offset, "the frame sizes do not add up to the declared pool size");
        return (new PoolRange(offset, pos, (int)total, frames.Count), frames);
    }

    private static InvalidDataException InvalidPool(long offset, string reason) =>
        new($"Pool at 0x{offset:x}: {reason}.");
}
