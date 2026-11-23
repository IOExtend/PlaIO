using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid ProfileModifierClass = new("09f84a8c-dcf6-45b4-96aa-e23380acf496");
    private static readonly Guid ProfileModifierDefinitionClass = new("7fd67a75-4b36-4be8-bf9a-6ce013ab9247");
    private static readonly Guid ProfileModifierOwnerRelation = new("d98b1d57-b28c-44bf-92a3-a67c8881ed56");
    private static readonly Guid ProfileModifierDefinitionRelation = new("43f52d63-b8d0-4659-b981-4401a14823b1");
    private static ReadOnlySpan<byte> ProfileStretchFrame =>
        [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];

    private static ReadOnlySpan<byte> ReadProfileStretchSettings(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> result = default;
        int offset = 0;
        while (data[offset..].IndexOf(SectionVectorFrame) is >= 0 and var relative)
        {
            int start = offset + relative;
            offset = start + SectionVectorFrame.Length;
            var section = ReadSectionVector(data, start);
            if (section.IsEmpty) continue;
            var tail = data[(start + 8 + section.Length)..];
            if (tail.Length < 68 || !tail.StartsWith(ProfileStretchFrame) || tail[60] != 2 ||
                tail[61] != 0 || tail[62] > 1 || BinaryPrimitives.ReadUInt32LittleEndian(tail[63..]) != 4 ||
                tail[67] != 1)
                continue;
            if (!result.IsEmpty) return default;
            result = tail;
        }
        return result;
    }

    private static PlaProfileStretch? ReadProfileStretch(ReadOnlySpan<byte> data, double? sectionStart, double? sectionEnd)
    {
        double start = ReadDouble(data, 1), end = ReadDouble(data, 9);
        if (data[0] == 0 && start == 0 && end == 0)
            return new PlaProfileStretch(false, null, null, sectionEnd - sectionStart);
        if (data[0] != 1 || !double.IsFinite(start) || !double.IsFinite(end) || end < start)
            return null;
        double? minimum = null;
        if (sectionStart is { } first && sectionEnd is { } last)
            minimum = (last - first) - Math.Max(0, Math.Min(end, last) - Math.Max(start, first));
        return new PlaProfileStretch(true, start, end, minimum);
    }

    private static IReadOnlyList<PlaProfileModifier>? ReadProfileModifiers(ReadOnlySpan<byte> profile, Guid id,
        ReadOnlySpan<byte> data, ReadOnlySpan<byte> section, IReadOnlyList<PlaProfileComponent>? components,
        Lazy<Dictionary<Guid, byte[]?>> records)
    {
        if (data.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 1 ||
            !TryReadRelationTargets(profile, ProfileModifierOwnerRelation, out var ids) ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[2..]) != ids.Length / 16)
            return null;
        if (ids.IsEmpty) return [];
        var values = components is null ? null : ReadProfileModifierValues(section, components, data);
        var modifiers = new List<PlaProfileModifier>();
        var seen = new HashSet<Guid>();
        for (int i = 0; i < ids.Length; i += 16)
        {
            Guid modifierId = new(ids.Slice(i, 16));
            if (modifierId == Guid.Empty || !seen.Add(modifierId) ||
                records.Value.GetValueOrDefault(modifierId) is not { } modifier ||
                new Guid(modifier.AsSpan(0, 16)) != ProfileModifierClass ||
                ReadSingleRelation(modifier, ProfileModifierOwnerRelation, requireSection: true) != id)
                return null;
            Guid definitionId = ReadSingleRelation(modifier, ProfileModifierDefinitionRelation, requireSection: true);
            if (definitionId == Guid.Empty || records.Value.GetValueOrDefault(definitionId) is not { Length: >= 53 } definition ||
                new Guid(definition.AsSpan(0, 16)) != ProfileModifierDefinitionClass ||
                !definition.AsSpan(41).StartsWith(LayerNameFrame) ||
                !HasGeometryRelation(definition, ProfileModifierDefinitionRelation, modifierId) ||
                ReadElementId(definition.AsSpan(49), out _) is not { } name)
                return null;
            double? value = modifier.Length >= 63 && modifier.AsSpan(39).StartsWith(new byte[] { 1, 0, 0x83, 0, 3, 0, 0, 0 }) &&
                values is not null && values.TryGetValue(new Guid(modifier.AsSpan(47, 16)), out double resolved)
                ? resolved : null;
            modifiers.Add(new PlaProfileModifier(modifierId, definitionId, name) { DefaultValue = value });
        }
        return modifiers.AsReadOnly();
    }

    private static Dictionary<Guid, byte[]?> ReadProfileModifierRecords(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var kinds = classes.Where(c => c.Guid == ProfileModifierClass || c.Guid == ProfileModifierDefinitionClass)
            .ToDictionary(c => c.Number, c => c.Guid);
        var result = new Dictionary<Guid, byte[]?>();
        if (kinds.Count == 0) return result;
        foreach (var block in document.Blocks)
        {
            if (block.Tag != "BL" || block.Version != 1 || block.Guid is not { } id || id == Guid.Empty ||
                !kinds.TryGetValue(block.Kind, out Guid type)) continue;
            var payload = block.Mode == PlaBlockMode.Lz ? document.ReadStandalonePayload(block) :
                document.PayloadStartsWithClass(block, type) ? document.ReadPayload(block) : null;
            Add(id, type, payload);
        }

        foreach (var (record, block) in FindUnindexedAttributeBlocks(document, kinds, result.Keys.ToHashSet()))
            Add(record.ObjectId, kinds[record.ClassNumber], document.ReadDirectoryPayload(block));
        return result;

        void Add(Guid id, Guid type, byte[]? payload)
        {
            if (payload is not { Length: >= 16 } || new Guid(payload.AsSpan(0, 16)) != type) return;
            if (!result.TryAdd(id, payload) && !payload.AsSpan().SequenceEqual(result[id]))
                result[id] = null;
        }
    }
}
