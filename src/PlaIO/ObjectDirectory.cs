namespace PlaIO;

/// <summary>One 30-byte directory record: <c>[u16 class][u64 pos][u32 count][GUID16]</c>. The units of
/// <see cref="Position"/> and <see cref="Count"/> are not documented.</summary>
/// <param name="ClassNumber">The class number of the object, raw. It is not mapped to a name.</param>
/// <param name="Position">Start of the object's extent, raw.</param>
/// <param name="Count">Length of the object's extent, raw.</param>
/// <param name="ObjectId">The object GUID.</param>
public readonly record struct ObjectDirectoryRecord(ushort ClassNumber, ulong Position, uint Count, Guid ObjectId);

/// <summary>A run of directory records found inside a raw block. Where a directory starts and ends is not documented: the
/// run is an estimate (the longest stretch whose extents mostly abut), and the pair counts are reported so callers can judge
/// it. The run can be shorter than the real directory: it stops at a stretch of more than 8 consecutive records without an
/// abutting neighbour. It can also take in records with a count of 0 whose position repeats another record's or falls inside
/// another extent; such records show up in <see cref="OverlappingPairs"/> and <see cref="DuplicatePositions"/>.</summary>
public sealed class ObjectDirectory
{
    internal ObjectDirectory(PlaBlock block, int recordOffset, ObjectDirectoryRecord[] records, int abuttingPairs,
        int overlappingPairs, int duplicatePositions)
    {
        Block = block;
        RecordOffset = recordOffset;
        Records = Array.AsReadOnly(records);
        AbuttingPairs = abuttingPairs;
        OverlappingPairs = overlappingPairs;
        DuplicatePositions = duplicatePositions;
    }

    /// <summary>The raw block that holds the directory.</summary>
    public PlaBlock Block { get; }

    /// <summary>Payload offset of the first reported record.</summary>
    public int RecordOffset { get; }

    /// <summary>The record alignment within the payload, <c>RecordOffset % 30</c>.</summary>
    public int Phase => RecordOffset % 30;

    /// <summary>The records in file order.</summary>
    public IReadOnlyList<ObjectDirectoryRecord> Records { get; }

    /// <summary>Adjacent pairs of the records sorted by position where one extent ends exactly where the next starts.</summary>
    public int AbuttingPairs { get; }

    /// <summary>Adjacent sorted pairs whose extents overlap.</summary>
    public int OverlappingPairs { get; }

    /// <summary>Adjacent sorted pairs with the same position.</summary>
    public int DuplicatePositions { get; }
}
