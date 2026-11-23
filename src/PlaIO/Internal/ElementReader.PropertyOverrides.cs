using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid PropertyOverrideClass = new("8dd49051-b081-4f20-a4a7-f6375145640d");
    private static readonly Guid ElementPropertyOverrideLink = new("7306b00e-ff8d-4051-a8b3-f76cfc597704");

    private static void ReadPropertyOverrideRecords(ReadOnlySpan<byte> pool, List<ObjectDirectoryRecord> records,
        Dictionary<Guid, SavedPropertyOverride> overrides)
    {
        foreach (var record in records)
        {
            var body = ReadIndexedPayload(pool, record);
            if (body is null || body.Length < 46 || new Guid(body.AsSpan(0, 16)) != PropertyOverrideClass ||
                !body.AsSpan(39, 7).SequenceEqual(new byte[] { 2, 0, 1, 0, 3, 0, 0 }))
                continue;
            var fields = ReadRecordFields(body, 46, out var relationships);
            if (fields.Length < 16) throw new InvalidDataException("A property override is missing its value class.");
            var valueClass = new Guid(fields[..16]);
            Guid element = Guid.Empty, definition = Guid.Empty;
            while (relationships.Length >= 17)
            {
                var targets = ReadRelationshipEntry(ref relationships, out byte tag, out Guid role);
                if (role == ElementPropertyOverrideLink)
                {
                    if (tag != 0x12 || element != Guid.Empty)
                        throw new InvalidDataException("A property override has conflicting element links.");
                    element = ReadRelationshipTarget(targets);
                }
                else if (role == PropertyOverrideLink)
                {
                    if (tag != 0x12 || definition != Guid.Empty)
                        throw new InvalidDataException("A property override has conflicting definition links.");
                    definition = ReadRelationshipTarget(targets);
                }
            }
            CheckRelationshipEnd(relationships);
            if (element == Guid.Empty || definition == Guid.Empty) continue;
            var value = new SavedPropertyOverride(element, definition, valueClass, ReadPropertyValue(fields[16..], valueClass));
            if (!overrides.TryAdd(record.ObjectId, value) && overrides[record.ObjectId] != value)
                throw new InvalidDataException("A property override has conflicting records.");
        }
    }

    private static Dictionary<Guid, IReadOnlyList<PlaPropertyOverride>> ReadPropertyOverrides(PlaDocument document,
        IReadOnlyList<PlaObjectClass> classes, Dictionary<Guid, PlaObjectClass> elements,
        Dictionary<Guid, SavedPropertyOverride> records)
    {
        if (records.Count == 0) return [];
        var owners = new Dictionary<Guid, Guid>();
        var definitions = ReadPropertyCatalog(document, classes, owners).Definitions.ToDictionary(d => d.Guid);
        var byElement = new Dictionary<Guid, Dictionary<Guid, PlaPropertyOverride>>();
        foreach (var (id, record) in records)
        {
            if (!elements.ContainsKey(record.Element) || !owners.TryGetValue(id, out var owner) ||
                owner != record.Definition || !definitions.TryGetValue(owner, out var definition))
                continue;
            if (!byElement.TryGetValue(record.Element, out var entries)) byElement.Add(record.Element, entries = []);
            var value = record.ValueClass == PropertyValueClass(definition.DataType) ? record.Value : null;
            if (value?.Value is Guid choice && definition.Choices?.Any(o => o.Guid == choice) != true)
                value = null;
            var entry = new PlaPropertyOverride(owner, value);
            if (!entries.TryAdd(owner, entry) && entries[owner] != entry)
                throw new InvalidDataException("An element has conflicting overrides of the same property.");
        }
        return byElement.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<PlaPropertyOverride>)pair.Value.Values.OrderBy(v => v.DefinitionGuid).ToList().AsReadOnly());
    }

    private readonly record struct SavedPropertyOverride(Guid Element, Guid Definition, Guid ValueClass, PlaPropertyValue? Value);
}
