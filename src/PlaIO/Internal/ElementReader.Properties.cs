using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid PropertyGroupClass = new("e9cdf1dc-9117-46b6-bd32-5adba41a52c2");
    private static readonly Guid PropertyDefinitionClass = new("1f11a777-d5f5-4336-9b5a-963800153712");
    private static readonly Guid PropertyGroupLink = new("6f061922-6cfd-40c7-bb63-bcb4ed6c577a");
    private static readonly Guid PropertyAvailabilityLink = new("b0b13d7f-53e9-444e-81aa-a11e9b3227fd");
    private static readonly Guid PropertyOverrideLink = new("6d8a4425-a61e-4dd0-8978-9f90fac91e16");
    private static readonly Guid ScalarPropertyType = new("512896ad-f784-49fb-b45e-b741e45ccc7a");
    private static readonly Guid EnumPropertyType = new("87fecb96-0931-4ca1-a911-ac6ba507502a");
    private static readonly Guid IntegerPropertyValue = new("bea7c323-acfb-4de4-83fa-22f8439fb713");
    private static readonly Guid RealPropertyValue = new("e0bf337e-40c2-43c4-86e5-fb8f2186d7f1");
    private static readonly Guid TextPropertyValue = new("b0485a4f-f7a8-4798-a06a-8efa6ec97e59");
    private static readonly Guid BooleanPropertyValue = new("a2ce25b9-2c60-4852-8ac0-62fefe90d34f");
    private static readonly Guid SingleEnumPropertyValue = new("7f8ba35b-ed11-4752-aff1-d4bcf0a0317d");
    private static readonly Guid MultiEnumPropertyValue = new("deb5c8a0-7fd8-4428-a1d4-1ef876cbf0be");

    internal static PlaPropertyCatalog ReadPropertyCatalog(PlaDocument document, IReadOnlyList<PlaObjectClass> classes,
        Dictionary<Guid, Guid>? overrideOwners = null)
    {
        var kinds = classes.Where(c => c.Guid == PropertyGroupClass || c.Guid == PropertyDefinitionClass)
            .ToDictionary(c => c.Number, c => c.Guid);
        if (kinds.Count == 0) return new PlaPropertyCatalog([], []);
        var groups = new List<PlaPropertyGroup>();
        var definitions = new List<PlaPropertyDefinition>();
        var groupMembership = new Dictionary<Guid, Guid>();
        foreach (var (id, body) in ReadClassRecords(document, kinds).OrderBy(pair => pair.Key))
        {
            bool isGroup = new Guid(body.AsSpan(0, 16)) == PropertyGroupClass;
            var fields = ReadPropertyRecord(body, isGroup, out var relationships);
            if (fields.IsEmpty) continue;
            string name = ReadFramedText(ref fields);
            string description = ReadFramedText(ref fields);
            if (isGroup)
            {
                if (fields.Length != 1 || fields[0] > 1)
                    throw new InvalidDataException("A property group has an invalid field frame.");
                groups.Add(new PlaPropertyGroup(id, name));
                ReadPropertyLinks(relationships, true, out var members, out _, out _);
                foreach (var member in members)
                    if (!groupMembership.TryAdd(member, id))
                        throw new InvalidDataException("A property definition has conflicting group membership.");
            }
            else
            {
                ReadPropertyLinks(relationships, false, out var owners, out var availability, out var overrides);
                if (owners.Length != 1)
                    throw new InvalidDataException("A property definition is missing its group.");
                var (type, choices, value) = ReadPropertyType(fields);
                definitions.Add(new PlaPropertyDefinition(id, owners[0], name, description, type,
                    Array.AsReadOnly(availability), choices, value));
                if (overrideOwners is null) continue;
                foreach (Guid target in overrides)
                    if (!overrideOwners.TryAdd(target, id))
                        throw new InvalidDataException("A property override has conflicting definitions.");
            }
        }
        foreach (var definition in definitions)
            if (groupMembership.GetValueOrDefault(definition.Guid) != definition.GroupGuid)
                throw new InvalidDataException("A property group and its definition have conflicting links.");
        if (groupMembership.Count != definitions.Count)
            throw new InvalidDataException("A property group has an unreadable definition.");
        return new PlaPropertyCatalog(groups.AsReadOnly(), definitions.AsReadOnly());
    }

    private static ReadOnlySpan<byte> ReadPropertyRecord(ReadOnlySpan<byte> body, bool isGroup,
        out ReadOnlySpan<byte> relationships)
    {
        relationships = default;
        int versionOffset = isGroup ? 45 : 41;
        int fieldOffset = isGroup ? 61 : 57;
        if (body.Length < fieldOffset || BinaryPrimitives.ReadUInt16LittleEndian(body[39..]) != (isGroup ? 0x0102 : 3) ||
            body[versionOffset] is not (0x82 or 0x83) || !body[(versionOffset + 1)..].StartsWith(RecordFrameTail))
            return default;
        var fields = ReadRecordFields(body, fieldOffset, out relationships);
        if (isGroup && BinaryPrimitives.ReadUInt32LittleEndian(body[41..]) != fields.Length + 16)
            throw new InvalidDataException("A property group has an invalid nested frame size.");
        return fields;
    }

    private static void ReadPropertyLinks(ReadOnlySpan<byte> data, bool isGroup, out Guid[] members,
        out Guid[] availability, out Guid[] overrides)
    {
        Guid[]? groupTargets = null, classTargets = null, overrideTargets = null;
        while (data.Length >= 17)
        {
            var targets = ReadRelationshipEntry(ref data, out byte tag, out Guid key);
            if (key == PropertyGroupLink)
            {
                if (groupTargets is not null || (isGroup ? tag is not (1 or 0x11) : tag != 0x12))
                    throw new InvalidDataException("A property record has conflicting group links.");
                groupTargets = ReadRelationshipTargets(targets);
            }
            if (key == PropertyAvailabilityLink && !isGroup)
            {
                if (classTargets is not null || tag is not (1 or 0x11))
                    throw new InvalidDataException("A property definition has conflicting availability links.");
                classTargets = ReadRelationshipTargets(targets);
            }
            if (key == PropertyOverrideLink && !isGroup)
            {
                if (overrideTargets is not null || tag is not (1 or 0x11))
                    throw new InvalidDataException("A property definition has conflicting override links.");
                overrideTargets = ReadRelationshipTargets(targets);
            }
        }
        CheckRelationshipEnd(data);
        members = groupTargets ?? [];
        availability = classTargets ?? [];
        overrides = overrideTargets ?? [];
    }

    private static (string? Type, IReadOnlyList<PlaPropertyChoice>? Choices, PlaPropertyValue? Default)
        ReadPropertyType(ReadOnlySpan<byte> data)
    {
        if (data.Length < 17 || data[^1] > 1)
            throw new InvalidDataException("A property definition has an invalid type frame.");
        var encoding = new Guid(data[..16]);
        data = data[16..^1];
        if (encoding == EnumPropertyType) return ReadPropertyChoices(data);
        if (encoding != ScalarPropertyType || data.Length < 10 ||
            BinaryPrimitives.ReadUInt16LittleEndian(data) != 1 ||
            !data[6..10].SequenceEqual(new byte[] { 0, 0, 1, 0 })) return default;
        int kind = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        int unit = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        string? type = (kind, unit) switch
        {
            (1, 0) => "integer",
            (2, 0) => "number",
            (2, 1) => "length",
            (2, 2) => "area",
            (2, 3) => "volume",
            (3, 0) => "string",
            (4, 0) => "boolean",
            _ => null,
        };
        return (type, null, type is null ? null : ReadPropertyDefault(data[10..], PropertyValueClass(type)));
    }

    private static (string? Type, IReadOnlyList<PlaPropertyChoice>? Choices, PlaPropertyValue? Default)
        ReadPropertyChoices(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> header = [2, 0, 2, 0, 1, 0, 3, 0, 1, 0];
        if (!data.StartsWith(header) || data.Length < 14) return default;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);
        data = data[14..];
        if (count > data.Length / 47) throw new InvalidDataException("An enumeration's choices are truncated.");
        var choices = new List<PlaPropertyChoice>((int)count);
        var keys = new HashSet<Guid>();
        for (int i = 0; i < count; i++)
        {
            if (data.Length < 47 || data[0] != 2 || new Guid(data[17..33]) != TextPropertyValue ||
                !data[33..41].SequenceEqual(new byte[] { 2, 0, 2, 0, 0, 0, 0, 0 }))
                throw new InvalidDataException("An enumeration choice has an invalid text frame.");
            var id = ReadRelationshipTarget(data[1..17]);
            if (!keys.Add(id)) throw new InvalidDataException("An enumeration repeats a choice GUID.");
            data = data[41..];
            choices.Add(new PlaPropertyChoice(id, ReadFramedText(ref data)));
        }
        if (data.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 1)
            throw new InvalidDataException("An enumeration is missing its choice index.");
        uint indexCount = BinaryPrimitives.ReadUInt32LittleEndian(data[2..]);
        data = data[6..];
        if (indexCount > data.Length / 16) throw new InvalidDataException("An enumeration's choice index is truncated.");
        data = data[((int)indexCount * 16)..];
        if (data.Length < 13 || !data[..6].SequenceEqual(new byte[] { 1, 0, 0, 0, 0, 0 }) ||
            !data[7..13].SequenceEqual(new byte[] { 0, 0, 0, 0, 1, 0 }) || data[6] > 1)
            return (null, choices.AsReadOnly(), null);
        bool multiple = data[6] == 1;
        var value = ReadPropertyDefault(data[13..], multiple ? MultiEnumPropertyValue : SingleEnumPropertyValue);
        if (value?.Value is Guid selected && !keys.Contains(selected))
            throw new InvalidDataException("An enumeration's default points to a missing choice.");
        return (multiple ? "multiEnum" : "singleEnum", choices.AsReadOnly(), value);
    }

    private static Guid PropertyValueClass(string? type) => type switch
    {
        "integer" => IntegerPropertyValue,
        "number" or "length" or "area" or "volume" => RealPropertyValue,
        "string" => TextPropertyValue,
        "boolean" => BooleanPropertyValue,
        "singleEnum" => SingleEnumPropertyValue,
        "multiEnum" => MultiEnumPropertyValue,
        _ => Guid.Empty,
    };

    private static PlaPropertyValue? ReadPropertyDefault(ReadOnlySpan<byte> data, Guid valueClass)
    {
        if (data.Length >= 29 && data[..4].SequenceEqual(new byte[] { 2, 0, 2, 0 }) &&
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) is > 0 &&
            data[8..13].SequenceEqual(new byte[] { 1, 0, 1, 1, 0 }))
            return new PlaPropertyValue("expression", null);
        if (data.Length < 18 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 1 ||
            new Guid(data[2..18]) != valueClass)
            return null;
        return ReadPropertyValue(data[18..], valueClass);
    }

    private static PlaPropertyValue? ReadPropertyValue(ReadOnlySpan<byte> data, Guid valueClass)
    {
        if (data.Length < 8 || !data[..4].SequenceEqual(new byte[] { 2, 0, 2, 0 }))
            return null;
        uint status = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        data = data[8..];
        if (status == 3 && data.IsEmpty) return new PlaPropertyValue("userUndefined", null);
        if (status != 0) return null;
        object? value = null;
        if (valueClass == IntegerPropertyValue && data.Length == 4)
            value = BinaryPrimitives.ReadInt32LittleEndian(data);
        else if (valueClass == RealPropertyValue && data.Length == 8 &&
            double.IsFinite(BinaryPrimitives.ReadDoubleLittleEndian(data)))
            value = BinaryPrimitives.ReadDoubleLittleEndian(data);
        else if (valueClass == TextPropertyValue)
        {
            var text = ReadFramedText(ref data);
            if (data.IsEmpty) value = text;
        }
        else if (valueClass == SingleEnumPropertyValue && data.Length == 16)
            value = ReadRelationshipTarget(data);
        return value is null ? null : new PlaPropertyValue("normal", value);
    }
}
