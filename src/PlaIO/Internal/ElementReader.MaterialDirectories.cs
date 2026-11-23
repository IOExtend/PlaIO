using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private const ushort FileDirectoryKind = 0xFDEB;
    private static readonly Guid FileDirectoryLayout = new("90b1e208-3b15-45e1-854c-23f5368b97cc");

    private static IEnumerable<(ObjectDirectoryRecord Record, PlaBlock Block)> FindUnindexedAttributeBlocks(
        PlaDocument document, Dictionary<ushort, Guid> kinds, HashSet<Guid> seen)
    {
        var indexed = new Lazy<ILookup<Guid, PlaBlock>>(() =>
            document.Blocks.Where(b => b.Tag == "BL" && b.Version == 1 && b.Guid.HasValue).ToLookup(b => b.Guid!.Value));
        foreach (var directory in document.Blocks.Where(b => IsRawTable(b, FileDirectoryKind)))
        {
            var records = ReadFileDirectory(document.ReadPayload(directory), document.Length);
            if (records.Count < 2) continue;
            var missing = records.Where(r => kinds.ContainsKey(r.ClassNumber) && !seen.Contains(r.ObjectId)).ToArray();
            if (missing.Length == 0) continue;

            // The indexed blocks locate the extent table. Its separate root verifies the chosen origin.
            var origins = records.Skip(1).SelectMany(record => indexed.Value[record.ObjectId]
                .Where(b => b.Kind == record.ClassNumber)
                .Select(b => b.Offset - (long)record.Position * ExtentUnit))
                .Where(offset => offset >= directory.End).Distinct()
                .Where(offset => document.ReadDirectoryBlock(offset, records[0]) is not null).Take(2).ToArray();
            if (origins.Length != 1) continue;
            foreach (var record in missing)
                if (document.ReadDirectoryBlock(origins[0], record) is { } block)
                    yield return (record, block);
        }
    }

    private static List<ObjectDirectoryRecord> ReadFileDirectory(ReadOnlySpan<byte> data, long fileLength)
    {
        if (data.Length < 70 || data[8..24].IndexOfAnyExcept((byte)0) >= 0 ||
            new Guid(data[24..40]) != FileDirectoryLayout) return [];
        ulong used = BinaryPrimitives.ReadUInt64LittleEndian(data);
        if (used == 0 || used > (ulong)fileLength / ExtentUnit) return [];

        var records = new List<ObjectDirectoryRecord>();
        ulong position = 0;
        for (int offset = 40; position < used && offset <= data.Length - DirectoryFinder.RecordSize; offset += DirectoryFinder.RecordSize)
        {
            var record = DirectoryFinder.ReadRecord(data[offset..]);
            if (record.ObjectId == Guid.Empty || record.Position != position ||
                record.Count == 0 || record.Count > used - position) return [];
            records.Add(record);
            position += record.Count;
        }
        return position == used ? records : [];
    }
}
