using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private const ushort GenericBlockKind = 0xFFDC;
    private static readonly Guid LayerClass = new("6fda3fe2-8e75-4e3f-b8c0-d0c4777e5d4d");
    private static readonly Guid BuiltInLayer = new("4d22523e-c844-4209-a123-3608c8964de4");
    private static readonly Guid StoryTableClass = new("2fa7b8cb-ee33-48d6-8daa-8a3fe02bddea");
    private static readonly Guid StoryDescriptorClass = new("bc046b25-27d6-461a-a6e5-6fa0e8594a95");
    private static readonly Guid StoryPartitionRelation = new("baf95b80-f256-4932-b891-03ff66848591");
    private static readonly Guid DoorHostRelation = new("1f6bfe02-d60c-40ae-9ac8-c3953ce12814");
    private static readonly Guid OpeningHostRelation = new("49f5b35f-3e0e-4644-a659-d024c2c93228");
    private static readonly Guid WallTopStoryRelation = new("7b48cc47-7267-4cfd-9133-9214ea18fb99");
    private static readonly Guid ColumnTopStoryRelation = new("5ebebc2d-cf0b-4751-8f55-7c2353c3fd14");
    private static ReadOnlySpan<byte> LayerNameFrame => [0x83, 0, 3, 0, 0, 0, 1, 0];
    private static ReadOnlySpan<byte> StoryTableFrame => [1, 0, 3, 0, 0, 1, 0];
    private static ReadOnlySpan<byte> StoryDescriptorFrame => [5, 0, 0x83, 0, 3, 0, 0, 0, 1, 0];

    private readonly record struct ElementLocation(int? LayerIndex, int? HomeStoryIndex, Guid Host, Guid TopStoryPartition);

    private readonly record struct Story(string? Name, double? Elevation);

    internal static IReadOnlyList<PlaStory> ReadStoryCatalog(PlaDocument document, IReadOnlyList<PlaObjectClass> classes) =>
        ReadStories(document, classes).OrderBy(row => row.Key)
            .Select(row => new PlaStory(row.Key, row.Value.Name!, row.Value.Elevation)).ToList().AsReadOnly();

    private static ElementLocation ReadElementLocation(ReadOnlySpan<byte> data, string type, int frame, bool olderObject = false)
    {
        if (type == "Opening") return new ElementLocation(null, null, ReadSingleRelation(data, OpeningHostRelation), Guid.Empty);

        int offset = frame + (type switch
        {
            "Beam" or "Column" => 703,
            "Door" or "Roof" => 177,
            "Object" when olderObject => 45,
            "Lamp" or "Object" => 171,
            _ => 165,
        });
        if (offset > data.Length - 6 || BinaryPrimitives.ReadUInt16LittleEndian(data[(offset - 2)..]) != 1)
            return default;

        int layer = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        int story = BinaryPrimitives.ReadInt16LittleEndian(data[(offset + 4)..]);
        Guid host = type == "Door" ? ReadSingleRelation(data, DoorHostRelation) : Guid.Empty;
        Guid topStory = type switch
        {
            "Wall" => ReadSingleRelation(data[frame..], WallTopStoryRelation, requireSection: true),
            "Column" => ReadSingleRelation(data[frame..], ColumnTopStoryRelation, requireSection: true),
            _ => Guid.Empty,
        };
        return new ElementLocation(layer > 0 ? layer : null, story, host, topStory);
    }

    private static Guid ReadSingleRelation(ReadOnlySpan<byte> data, Guid relation, byte tag = 0x12, bool requireSection = false)
    {
        Span<byte> marker = stackalloc byte[17];
        marker[0] = tag;
        relation.TryWriteBytes(marker[1..]);
        int start = data.IndexOf(marker);
        if (start < 0 || data.Length - start < marker.Length + 16 || data[(start + 1)..].IndexOf(marker) >= 0)
            return Guid.Empty;
        if (requireSection && FindRelationshipSection(data, start) < 0) return Guid.Empty;
        return new Guid(data.Slice(start + marker.Length, 16));
    }

    private static int FindRelationshipSection(ReadOnlySpan<byte> data, int relationOffset)
    {
        int section = -1;
        for (int offset = 0; offset <= relationOffset - 6; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) != data.Length - offset - sizeof(uint) ||
                BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + sizeof(uint))..]) != 1) continue;
            if (section >= 0) return -1;
            section = offset + 6;
        }
        return section;
    }

    private static Dictionary<Guid, int?> ReadStoryPartitions(PlaDocument document, IReadOnlyList<PlaObjectClass> classes,
        Dictionary<int, Story> stories)
    {
        var classNumbers = classes.Where(c => c.Guid == StoryDescriptorClass).Select(c => c.Number).ToHashSet();
        var partitions = new Dictionary<Guid, int?>();
        foreach (var block in document.Blocks)
        {
            if (block.Tag != "BL" || block.Version != 1 || !classNumbers.Contains(block.Kind)) continue;
            byte[]? body = block.Mode == PlaBlockMode.Lz
                ? document.ReadStandalonePayload(block)
                : document.PayloadStartsWithClass(block, StoryDescriptorClass) ? document.ReadPayload(block) : null;
            if (body is null) continue;

            ReadOnlySpan<byte> data = body;
            if (data.Length < 64 || new Guid(data[..16]) != StoryDescriptorClass ||
                !data.Slice(39, StoryDescriptorFrame.Length).SequenceEqual(StoryDescriptorFrame)) continue;
            if (ReadElementId(data[49..], out int consumed) is null) continue;
            var tail = data[(49 + consumed)..];
            if (tail.Length < 11 || tail[2] != 1 ||
                BinaryPrimitives.ReadUInt32LittleEndian(tail[5..]) != tail.Length - 9 ||
                BinaryPrimitives.ReadUInt16LittleEndian(tail[9..]) != 1) continue;

            int story = BinaryPrimitives.ReadInt16LittleEndian(tail);
            if (!stories.ContainsKey(story)) continue;
            Guid partition = ReadSingleRelation(tail[11..], StoryPartitionRelation, 0x11);
            if (partition != Guid.Empty && !partitions.TryAdd(partition, story) && partitions[partition] != story)
                partitions[partition] = null;
        }
        return partitions;
    }

    private static Dictionary<int, string> ReadLayerNames(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var names = new Dictionary<int, string>();
        foreach (var layer in ReadLayerRecords(document, classes))
        {
            if (layer.Name is { } name && !names.TryAdd(layer.Index, name) && names[layer.Index] != name)
                throw new InvalidDataException($"Layer {layer.Index} has conflicting names.");
        }
        return names;
    }

    internal static IReadOnlyList<PlaLayer> ReadLayerCatalog(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var layers = new Dictionary<int, PlaLayer>();
        var indices = new Dictionary<Guid, int>();
        foreach (var layer in ReadLayerRecords(document, classes))
        {
            if (layer.Guid == Guid.Empty) continue;
            if (!layers.TryAdd(layer.Index, layer) && layers[layer.Index] != layer ||
                !indices.TryAdd(layer.Guid, layer.Index) && indices[layer.Guid] != layer.Index)
                throw new InvalidDataException($"Layer {layer.Index} has conflicting records.");
        }
        return layers.Values.OrderBy(layer => layer.Index).ToList().AsReadOnly();
    }

    private static IEnumerable<PlaLayer> ReadLayerRecords(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var classNumbers = classes.Where(c => c.Guid == LayerClass).Select(c => c.Number).ToHashSet();
        foreach (var block in document.Blocks)
        {
            if (block.Tag != "BL" || block.Version != 1 ||
                (block.Kind != GenericBlockKind && !classNumbers.Contains(block.Kind)) ||
                !document.PayloadStartsWithClass(block, LayerClass)) continue;

            ReadOnlySpan<byte> data = document.ReadPayload(block);
            if (data.Length < 55 || !data.Slice(47, LayerNameFrame.Length).SequenceEqual(LayerNameFrame)) continue;
            var name = ReadElementId(data[55..], out int consumed);
            // The built-in layer can store a resource token in place of a name.
            if (name is null)
            {
                if (block.Guid != BuiltInLayer || !data[55..].StartsWith(new byte[] { 1, 0, 0, 0, 0x14, 0 }))
                    continue;
                consumed = 6;
            }
            var tail = data[(55 + consumed)..];
            if (tail.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(tail) != 1) continue;
            int index = BinaryPrimitives.ReadInt32LittleEndian(tail[2..]);
            if (index > 0)
                yield return new PlaLayer(block.Guid.GetValueOrDefault(), index, name, ReadLayerGroup(tail));
        }
    }

    private static uint? ReadLayerGroup(ReadOnlySpan<byte> data) =>
        data.Length >= 39 ? BinaryPrimitives.ReadUInt32LittleEndian(data[35..]) : null;

    private static Dictionary<int, Story> ReadStories(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var classNumbers = classes.Where(c => c.Guid == StoryTableClass).Select(c => c.Number).ToHashSet();
        var stories = new Dictionary<int, Story>();
        foreach (var block in document.Blocks)
        {
            if (block.Tag != "BL" || block.Version != 1 || !classNumbers.Contains(block.Kind) ||
                !document.PayloadStartsWithClass(block, StoryTableClass)) continue;

            ReadOnlySpan<byte> data = document.ReadPayload(block);
            if (data.Length < 50 || !data.Slice(39, StoryTableFrame.Length).SequenceEqual(StoryTableFrame)) continue;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[46..]);
            var rows = data[50..];
            if (count < 2 || count > rows.Length / 19)
                throw new InvalidDataException("The story count is outside the story table.");

            var storedStories = new Story[count];
            for (int i = 0; i < storedStories.Length; i++)
            {
                if (rows.Length < 19 || BinaryPrimitives.ReadUInt16LittleEndian(rows) != 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(rows[10..]) != 1)
                    throw new InvalidDataException("A story table record has an invalid header.");
                var name = ReadElementId(rows[12..], out int consumed);
                if (name is null || consumed > rows.Length - 15)
                    throw new InvalidDataException("A story name exceeds its record.");
                double elevation = BinaryPrimitives.ReadDoubleLittleEndian(rows[2..]);
                storedStories[i] = new Story(name, double.IsFinite(elevation) ? elevation : null);
                rows = rows[(15 + consumed)..];
            }

            if (rows.Length < 6) throw new InvalidDataException("The story table is missing its index range.");
            int firstStory = BinaryPrimitives.ReadInt16LittleEndian(rows);
            int lastStory = BinaryPrimitives.ReadInt32LittleEndian(rows[2..]);
            // The last row is the upper boundary of the highest story.
            if (lastStory != firstStory + storedStories.Length - 2)
                throw new InvalidDataException("The story table index range does not match its story count.");
            for (int i = 0; i < storedStories.Length - 1; i++)
            {
                int index = firstStory + i;
                var story = storedStories[i];
                if (stories.TryAdd(index, story)) continue;
                if (stories[index].Name != story.Name)
                    throw new InvalidDataException($"Story {index} has conflicting names.");
                if (stories[index].Elevation != story.Elevation)
                    stories[index] = story with { Elevation = null };
            }
        }
        return stories;
    }
}
