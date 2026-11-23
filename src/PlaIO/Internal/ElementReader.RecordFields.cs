using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static ReadOnlySpan<byte> RecordFrameTail => [0, 3, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static Dictionary<Guid, byte[]> ReadClassRecords(PlaDocument document, Dictionary<ushort, Guid> kinds)
    {
        var records = new Dictionary<Guid, byte[]>();
        foreach (var block in document.Blocks)
        {
            if (block.Tag != "BL" || block.Version != 1 || block.Guid is not { } id || id == Guid.Empty ||
                !kinds.TryGetValue(block.Kind, out var type)) continue;
            var body = block.Mode == PlaBlockMode.Lz ? document.ReadStandalonePayload(block) :
                document.PayloadStartsWithClass(block, type) ? document.ReadPayload(block) : null;
            Add(id, type, body);
        }
        foreach (var (record, block) in FindUnindexedAttributeBlocks(document, kinds, records.Keys.ToHashSet()))
            Add(record.ObjectId, kinds[record.ClassNumber], document.ReadDirectoryPayload(block));
        return records;

        void Add(Guid id, Guid type, byte[]? body)
        {
            if (body is null || body.Length < 35 || new Guid(body.AsSpan(0, 16)) != type) return;
            if (!records.TryAdd(id, body) &&
                (!body.AsSpan(0, 16).SequenceEqual(records[id].AsSpan(0, 16)) ||
                 !body.AsSpan(35).SequenceEqual(records[id].AsSpan(35))))
                throw new InvalidDataException($"Record {id} has conflicting copies.");
        }
    }

    private static ReadOnlySpan<byte> ReadRecordFields(ReadOnlySpan<byte> body, int fieldStart,
        out ReadOnlySpan<byte> relationships)
    {
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(body[35..]);
        if (size <= fieldStart - 39 || size > body.Length - 46)
            throw new InvalidDataException("The field frame has an invalid length.");
        int relationOffset = 39 + (int)size;
        var tail = body[relationOffset..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(tail) != tail.Length - 4 ||
            BinaryPrimitives.ReadUInt16LittleEndian(tail[4..]) != 1)
            throw new InvalidDataException("The relationship frame is invalid.");
        relationships = tail[6..];
        return body[fieldStart..relationOffset];
    }

    private static string ReadFramedText(ref ReadOnlySpan<byte> fields)
    {
        if (fields.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(fields) != 1 ||
            ReadCountedUnicode(fields[2..], out int consumed) is not { } value)
            throw new InvalidDataException("A framed text field is invalid.");
        fields = fields[(2 + consumed)..];
        return value;
    }

    private static ReadOnlySpan<byte> ReadRelationshipEntry(ref ReadOnlySpan<byte> data, out byte tag, out Guid key)
    {
        tag = data[0];
        key = new Guid(data[1..17]);
        data = data[17..];
        uint count = 1;
        if (tag is 1 or 2 && data.Length >= 4)
        {
            count = BinaryPrimitives.ReadUInt32LittleEndian(data);
            data = data[4..];
        }
        else if (tag is not (0x11 or 0x12))
            throw new InvalidDataException("A relationship has an invalid tag.");
        if (count > data.Length / 16)
            throw new InvalidDataException("A relationship is truncated.");
        var targets = data[..((int)count * 16)];
        data = data[targets.Length..];
        return targets;
    }

    private static void CheckRelationshipEnd(ReadOnlySpan<byte> data)
    {
        if (data.Length != 1 || data[0] != 0)
            throw new InvalidDataException("The relationship frame has an invalid terminator.");
    }

    private static Guid ReadRelationshipTarget(ReadOnlySpan<byte> target)
    {
        var id = new Guid(target);
        if (id == Guid.Empty) throw new InvalidDataException("A relationship has an empty target.");
        return id;
    }

    private static Guid[] ReadRelationshipTargets(ReadOnlySpan<byte> targets)
    {
        var ids = new Guid[targets.Length / 16];
        for (int i = 0; i < ids.Length; i++)
            ids[i] = ReadRelationshipTarget(targets.Slice(16 * i, 16));
        return ids;
    }
}
