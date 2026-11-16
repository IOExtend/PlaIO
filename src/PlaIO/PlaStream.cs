namespace PlaIO;

/// <summary>The decoded global stream of a document: every block's payload decoded in block order — raw payloads as
/// they are, zero-mask payloads as their full decode, mode 2 payloads as their token-stream output. The object
/// directories (<see cref="PlaDocument.FindDirectories"/>) address this stream: a record's
/// <see cref="ObjectDirectoryRecord.Position"/> and <see cref="ObjectDirectoryRecord.Count"/> name a byte extent of the
/// stream, the object's data. The stream is a snapshot: it holds the whole decoded file in memory and never touches the
/// document again, so the document may be disposed. Not thread-safe.</summary>
public sealed class PlaStream
{
    private readonly List<byte> _bytes;

    internal PlaStream(List<byte> bytes) => _bytes = bytes;

    /// <summary>Length of the decoded stream in bytes.</summary>
    public long Length => _bytes.Count;

    /// <summary>The bytes of one extent of the stream.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The bounds are negative or reach past the stream.</exception>
    public byte[] ReadExtent(long position, long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (position > Length || count > Length - position)
            throw new ArgumentOutOfRangeException(nameof(count), $"The extent at {position}+{count} reaches past the decoded stream ({Length} bytes).");
        var extent = new byte[count];
        for (long i = 0; i < count; i++) extent[i] = _bytes[(int)(position + i)];
        return extent;
    }

    /// <summary>The bytes of the extent one directory record addresses.</summary>
    /// <exception cref="InvalidDataException">The record's extent reaches past the decoded stream.</exception>
    public byte[] ReadExtent(ObjectDirectoryRecord record)
    {
        long position = (long)record.Position;
        long count = record.Count;
        if (position > Length || count > Length - position)
            throw new InvalidDataException($"The directory record of {record.ObjectId} (class {record.ClassNumber}) addresses {position}+{count}, past the decoded stream ({Length} bytes).");
        return ReadExtent(position, count);
    }
}
