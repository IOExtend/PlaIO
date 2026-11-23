using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid LayoutRevisionStateClass = new("169571f5-0665-4fec-8070-5e14962eaf1a");
    private static readonly Guid LayoutRevisionOverrideClass = new("8535adec-7d22-4334-8dfa-d3318ff35d23");
    private static readonly Guid IssuedLayoutRevisionClass = new("2c2d6560-da21-4610-b3b8-fe05f4b5c431");
    private static readonly Guid LayoutTransmittalClass = new("ad6bafb9-a10c-4258-aad3-97f97ac5d1ae");
    private static readonly Guid LayoutRevisionStateLink = new("6c8ed16c-a969-4a25-9f40-1ab657dc6b50");
    private static readonly Guid LayoutRevisionOverrideLink = new("a0e2f86f-4496-4a59-bbbe-ef3a6f1267a9");
    private static readonly Guid IssuedLayoutRevisionLink = new("9d374292-829a-4553-a3e0-6631d9a0110c");
    private static readonly Guid IssuedTransmittalLink = new("3b14d41d-2596-4cb7-8767-21309d216a9c");
    private static readonly Guid OpenTransmittalLink = new("2421d43e-0a52-4d18-acfb-54e6262d9dbc");

    private static void ResolveLayoutRevisions(PlaDocument document, IReadOnlyList<PlaObjectClass> classes,
        Dictionary<Guid, byte[]> navigatorRecords, List<PlaLayoutBookItem> items)
    {
        var kinds = classes.Where(c => c.Guid == IssuedLayoutRevisionClass || c.Guid == LayoutTransmittalClass)
            .ToDictionary(c => c.Number, c => c.Guid);
        var records = kinds.Count == 0 ? new Dictionary<Guid, byte[]>() : ReadClassRecords(document, kinds);
        var transmittals = new Dictionary<Guid, (PlaLayoutTransmittal Value, HashSet<Guid> Members)>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Type is not ("LayoutItem" or "MasterLayoutItem") ||
                ReadLayoutSettings(navigatorRecords, item.Guid, navigatorRecords[item.Guid]) is not { } settings)
                continue;
            var stateLink = ReadNavigatorLink(settings.Body, LayoutRevisionStateLink, 0x11);
            if (stateLink.Length == 0) continue;
            if (stateLink.Length != 1 || !navigatorRecords.TryGetValue(stateLink[0], out var state) ||
                RecordClass(state) != LayoutRevisionStateClass ||
                !ReadNavigatorLink(state, LayoutRevisionStateLink, 0x12).SequenceEqual([settings.Id]))
                throw new InvalidDataException("A layout has a conflicting revision state.");

            var openLink = ReadNavigatorLink(state, OpenTransmittalLink, 0x12);
            var stateFields = ReadRecordFields(state, 49, out var stateRelations);
            if (!HasNavigatorEnvelope(state, 43) ||
                !state.AsSpan(39, 4).SequenceEqual(new byte[] { 1, 0, 1, 0 })) continue;
            string numberingBase = ReadFramedText(ref stateFields);
            if (ReadFramedText(ref stateFields).Length != 0 || !stateFields.IsEmpty ||
                !HasKnownRevisionStateLinks(stateRelations)) continue;

            var issuedLinks = ReadNavigatorLink(settings.Body, IssuedLayoutRevisionLink, 1, 0x11);
            if (issuedLinks.Distinct().Count() != issuedLinks.Length)
                throw new InvalidDataException("A layout has a repeated revision.");
            var issued = new List<PlaIssuedLayoutRevision>(issuedLinks.Length);
            foreach (Guid id in issuedLinks)
            {
                if (!records.TryGetValue(id, out var body) || body.Length < 46 ||
                    RecordClass(body) != IssuedLayoutRevisionClass ||
                    !ReadNavigatorLink(body, IssuedLayoutRevisionLink, 0x12).SequenceEqual([settings.Id]))
                    throw new InvalidDataException("An issued revision has a conflicting layout link.");
                var fields = ReadRecordFields(body, 49, out _);
                if (!HasNavigatorEnvelope(body, 43) || !body.AsSpan(39, 4).SequenceEqual(new byte[] { 2, 0, 1, 0 }))
                    throw new InvalidDataException("An issued layout revision has an invalid field frame.");
                string revisionId = ReadFramedText(ref fields);
                var transmittal = GetTransmittal(ReadNavigatorLink(body, IssuedTransmittalLink, 0x12), id, issued: true);
                issued.Add(new PlaIssuedLayoutRevision(id, revisionId, transmittal));
            }

            PlaLayoutTransmittal? open = openLink.Length == 0 ? null : GetTransmittal(openLink, stateLink[0], issued: false);
            string? current = open is null ? issued.Count == 0 ? "" : issued[^1].Id
                : open.OverridesRevisionIds ? open.Id
                : ResolveOpenRevisionId(ReadSavedRevisionOverride(navigatorRecords, stateLink[0], state) ?? numberingBase, issued);
            items[i] = item with
            {
                Revision = new PlaLayoutRevision(current, open is null ? current : null, issued.Count != 0, open is not null)
                {
                    IssuedRevisions = issued.AsReadOnly(),
                    OpenTransmittal = open,
                },
            };
        }

        PlaLayoutTransmittal GetTransmittal(Guid[] link, Guid member, bool issued)
        {
            if (link.Length != 1)
                throw new InvalidDataException("A layout revision has an invalid transmittal link.");
            Guid id = link[0];
            if (!transmittals.TryGetValue(id, out var transmittal))
            {
                if (!records.TryGetValue(id, out var body) || body.Length < 46 || RecordClass(body) != LayoutTransmittalClass)
                    throw new InvalidDataException("A layout revision references an invalid transmittal record.");
                var value = ReadLayoutTransmittal(id, body);
                bool closed = value.IssuedAtUtc.HasValue;
                var members = ReadNavigatorLink(body, closed ? IssuedTransmittalLink : OpenTransmittalLink, 1, 0x11);
                transmittal = (value, members.ToHashSet());
                transmittals.Add(id, transmittal);
            }
            if (transmittal.Value.IssuedAtUtc.HasValue != issued || !transmittal.Members.Contains(member))
                throw new InvalidDataException("A layout revision's transmittal does not confirm its status or membership.");
            return transmittal.Value;
        }
    }

    private static bool HasKnownRevisionStateLinks(ReadOnlySpan<byte> relationships)
    {
        while (relationships.Length >= 17)
        {
            ReadRelationshipEntry(ref relationships, out byte tag, out Guid role);
            if ((role, tag) == (LayoutRevisionOverrideLink, (byte)0x11) ||
                (role, tag) == (LayoutRevisionStateLink, (byte)0x12) ||
                (role, tag) == (OpenTransmittalLink, (byte)0x12)) continue;
            return false;
        }
        CheckRelationshipEnd(relationships);
        return true;
    }

    private static string? ReadSavedRevisionOverride(Dictionary<Guid, byte[]> records, Guid stateId, byte[] state)
    {
        if (ReadNavigatorLink(state, LayoutRevisionOverrideLink, 0x11).Length == 0) return null;
        var (_, record) = ReadLinkedNavigatorRecord(records, stateId, state, LayoutRevisionOverrideLink,
            LayoutRevisionOverrideClass, 0x11, 0x12);
        var fields = ReadRecordFields(record, 47, out _);
        if (!HasNavigatorEnvelope(record, 41) || !record.AsSpan(39, 2).SequenceEqual(new byte[] { 1, 0 }))
            throw new InvalidDataException("A saved layout revision override has an invalid field frame.");
        string value = ReadFramedText(ref fields);
        if (!fields.IsEmpty) throw new InvalidDataException("A saved layout revision override has unexpected fields.");
        return value;
    }

    private static string? ResolveOpenRevisionId(string numberingBase, IReadOnlyList<PlaIssuedLayoutRevision> issued)
    {
        if (numberingBase.Length == 0) return null;
        if (!issued.Any(revision => revision.Id == numberingBase)) return numberingBase;

        bool numeric = char.IsAsciiDigit(numberingBase[^1]);
        int start = RevisionSequenceStart(numberingBase, numeric);
        if (start == numberingBase.Length) return null;

        // After the saved ID is issued, numbering resumes after the highest ID in its sequence.
        string highest = numberingBase;
        foreach (var revision in issued)
        {
            string id = revision.Id;
            if (id.Length <= start || RevisionSequenceStart(id, numeric) != start ||
                !id.AsSpan(0, start).SequenceEqual(numberingBase.AsSpan(0, start))) continue;
            var a = id.AsSpan(start);
            var b = highest.AsSpan(start);
            if (numeric)
            {
                a = a.TrimStart('0');
                b = b.TrimStart('0');
            }
            if (a.Length > b.Length || a.Length == b.Length && a.SequenceCompareTo(b) > 0)
                highest = id;
        }
        return AdvanceRevisionId(highest);
    }

    private static int RevisionSequenceStart(string id, bool numeric)
    {
        int start = id.Length;
        if (numeric)
        {
            while (start > 0 && char.IsAsciiDigit(id[start - 1])) start--;
        }
        else if (start > 0 && id[^1] is >= 'A' and <= 'Z')
        {
            start--;
            while (start > 0 && id[start - 1] == 'Z') start--;
        }
        return start;
    }

    private static string AdvanceRevisionId(string id)
    {
        char last = id[^1];
        if (last is >= 'A' and < 'Z') return string.Concat(id.AsSpan(0, id.Length - 1), ((char)(last + 1)).ToString());
        if (last == 'Z') return id + "A";

        int carry = id.Length - 1;
        while (carry >= 0 && id[carry] == '9') carry--;
        string zeroes = new('0', id.Length - 1 - carry);
        return carry >= 0 && char.IsAsciiDigit(id[carry])
            ? string.Concat(id.AsSpan(0, carry), ((char)(id[carry] + 1)).ToString(), zeroes)
            : string.Concat(id.AsSpan(0, carry + 1), "1", zeroes);
    }

    private static PlaLayoutTransmittal ReadLayoutTransmittal(Guid id, byte[] body)
    {
        var fields = ReadRecordFields(body, 51, out _);
        if (!HasNavigatorEnvelope(body, 45) || !body.AsSpan(39, 2).SequenceEqual(new byte[] { 1, 2 }) ||
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(41)) != fields.Length + 6)
            throw new InvalidDataException("A layout transmittal has an invalid field frame.");
        string number = ReadFramedText(ref fields);
        string name = ReadFramedText(ref fields);
        if (fields.Length < 4) throw new InvalidDataException("A layout transmittal has a truncated timestamp.");
        uint seconds = BinaryPrimitives.ReadUInt32LittleEndian(fields);
        fields = fields[4..];
        string user = ReadFramedText(ref fields);
        if (fields.Length != 13 || fields[8] > 1 || fields[^1] > 1)
            throw new InvalidDataException("A layout transmittal has an invalid status frame.");
        bool closed = fields[^1] == 1;
        return new PlaLayoutTransmittal(id, number, name, closed ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null,
            closed ? user : null)
        {
            OverridesRevisionIds = fields[8] == 1,
        };
    }
}
