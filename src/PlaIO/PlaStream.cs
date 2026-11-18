namespace PlaIO;

/// <summary>The decoded global stream of a document: every block's payload decoded in block order — raw payloads as
/// they are, zero-mask payloads as their full decode, mode 2 payloads as their token-stream output. The object
/// directories (<see cref="PlaDocument.FindDirectories"/>) address this stream: a record's
/// <see cref="ObjectDirectoryRecord.Position"/> and <see cref="ObjectDirectoryRecord.Count"/> name a byte extent of the
/// stream, the object's data. The stream is a snapshot: it holds the whole decoded file in memory and never touches the
/// document again, so the document may be disposed. Not thread-safe.</summary>
/// <remarks>Blocks of the scan that fail to decode are skipped and leave the stream without their bytes; every offset at
/// or past the first skipped block is then shifted by the bytes that are missing, and the reads report that: an extent
/// reaching into the shifted range throws, and <see cref="UnreliableFrom"/> names the boundary.</remarks>
public sealed class PlaStream
{
    private readonly List<byte> _bytes;

    /// <summary>The stream offset at or past which the offsets are shifted, because blocks failed to decode before it;
    /// null when every block decoded and all offsets are exact.</summary>
    public long? UnreliableFrom { get; }

    internal PlaStream(List<byte> bytes, long? unreliableFrom)
    {
        _bytes = bytes;
        UnreliableFrom = unreliableFrom;
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
