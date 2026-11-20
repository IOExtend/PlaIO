namespace PlaIO.Model;

/// <summary>The decoded global stream of a document: every block's payload decoded in block order — raw payloads as
/// they are, zero-mask payloads as their full decode, mode 2 payloads as their token-stream output. The object
/// directories (<see cref="PlaDocument.FindDirectories"/>) address this stream: a record's
/// <see cref="ObjectDirectoryRecord.Position"/> and <see cref="ObjectDirectoryRecord.Count"/> name a byte extent of the
/// stream, the object's data. The stream is a snapshot: it holds the whole decoded file in memory and never touches the
/// document again, so the document may be disposed. Not thread-safe.</summary>
/// <remarks>When an indexed block fails to decode, offsets at and past that block may be shifted. An extent that
/// reaches the unreliable range throws, and <see cref="UnreliableFrom"/> names the boundary.</remarks>
public sealed class PlaStream
{
    private readonly List<byte> _bytes;

    /// <summary>The offset of the first indexed block that failed to decode, or null when every indexed block decoded.
    /// Extent reads that reach this boundary throw because the following offsets may be shifted.</summary>
    public long? UnreliableFrom { get; }

    /// <summary>The indexed blocks that failed to decode to their declared size (false entries of the scan), in file
    /// order; their payloads are missing from the stream, which shifts the offsets that follow
    /// <see cref="UnreliableFrom"/>.</summary>
    public IReadOnlyList<PlaBlock> SkippedBlocks { get; }

    internal PlaStream(List<byte> bytes, IReadOnlyList<(PlaBlock Block, long WindowPosition)> skipped)
    {
        _bytes = bytes;
        SkippedBlocks = skipped.Select(pair => pair.Block).ToArray();
        UnreliableFrom = skipped.Count == 0 ? null : skipped[0].WindowPosition;
    }

    /// <summary>Length of the decoded stream in bytes.</summary>
    public long Length => _bytes.Count;

    /// <summary>The bytes of one extent of the stream.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The bounds are negative or reach past the stream.</exception>
    /// <exception cref="InvalidDataException">The extent reaches into the range whose offsets are shifted, because
    /// blocks before it failed to decode (<see cref="UnreliableFrom"/>).</exception>
    public byte[] ReadExtent(long position, long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (position > Length || count > Length - position)
            throw new ArgumentOutOfRangeException(nameof(count), $"The extent at {position}+{count} reaches past the decoded stream ({Length} bytes).");
        if (UnreliableFrom is { } from && (position >= from || position + count > from))
            throw new InvalidDataException($"The extent at {position}+{count} reaches the offsets shifted by skipped blocks (from {from} on the offsets are not reliable).");
        var extent = new byte[count];
        for (long i = 0; i < count; i++) extent[i] = _bytes[(int)(position + i)];
        return extent;
    }

    /// <summary>The bytes of the extent one directory record addresses.</summary>
    /// <exception cref="InvalidDataException">The record's extent reaches past the decoded stream, or reaches into the
    /// range whose offsets are shifted because blocks before it failed to decode.</exception>
    public byte[] ReadExtent(ObjectDirectoryRecord record)
    {
        // A record position wider than the stream's long is past the stream by definition; cast after the check.
        if (record.Position > (ulong)Length)
            throw new InvalidDataException($"The directory record of {record.ObjectId} (class {record.ClassNumber}) addresses {record.Position}+{record.Count}, past the decoded stream ({Length} bytes).");
        long position = (long)record.Position;
        long count = record.Count;
        if (count > Length - position)
            throw new InvalidDataException($"The directory record of {record.ObjectId} (class {record.ClassNumber}) addresses {position}+{count}, past the decoded stream ({Length} bytes).");
        if (UnreliableFrom is { } from && (position >= from || position + count > from))
            throw new InvalidDataException($"The directory record of {record.ObjectId} (class {record.ClassNumber}) addresses {position}+{count}, into the offsets shifted by skipped blocks (from {from} on the offsets are not reliable).");
        return ReadExtent(position, count);
    }
}
