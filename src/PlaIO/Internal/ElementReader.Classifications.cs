using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid ClassificationSystemClass = new("420f4c27-70b8-43c2-9d9d-f74ef1ee9b47");
    private static readonly Guid ClassificationItemClass = new("9be1f9d1-13d9-45ff-813e-01c7846cd0c2");
    private static readonly Guid ClassificationRootLink = new("e88a0cef-00c8-45d1-ae13-80c6616ad0f8");
    private static readonly Guid ClassificationParentChildLink = new("8d79244e-35bb-412f-8148-2b4a8839159a");
    private static readonly Guid ClassificationAssignmentLink = new("9534032d-da8d-4c44-b42b-6363688cafc6");

    internal static (IReadOnlyList<PlaClassificationSystem> Systems, Dictionary<Guid, List<PlaClassificationAssignment>> Assignments)
        ReadClassificationCatalog(PlaDocument document, IReadOnlyList<PlaObjectClass> classes,
            Dictionary<Guid, PlaObjectClass>? elements = null)
    {
        var kinds = classes.Where(c => c.Guid == ClassificationSystemClass || c.Guid == ClassificationItemClass)
            .ToDictionary(c => c.Number, c => c.Guid);
        var assignments = new Dictionary<Guid, List<PlaClassificationAssignment>>();
        if (!kinds.ContainsValue(ClassificationSystemClass)) return ([], assignments);
        var bodies = ReadClassRecords(document, kinds);

        var systems = new List<PlaClassificationSystem>();
        var visited = new HashSet<Guid>();
        foreach (var (systemId, body) in bodies.OrderBy(pair => pair.Key))
        {
            if (new Guid(body.AsSpan(0, 16)) != ClassificationSystemClass) continue;
            var fields = ReadClassificationFrame(body, 1, out var relations);
            if (fields.IsEmpty) continue;
            string name = ReadFramedText(ref fields);
            string version = ReadFramedText(ref fields);
            DateOnly? date = ReadClassificationDate(ref fields);
            string description = ReadFramedText(ref fields);
            string source = ReadFramedText(ref fields);
            CheckClassificationFieldEnd(fields);
            var links = ReadClassificationLinks(relations, rootTag: 0x11);
            var root = ReadItem(links.Root);
            if (root.Links.Root != systemId || root.Links.Parent != Guid.Empty || !visited.Add(links.Root))
                throw new InvalidDataException("The classification system and its root have conflicting links.");

            var items = new List<PlaClassificationItem>();
            var classified = new HashSet<Guid>();
            var pending = new Stack<(Guid Id, Guid Parent)>();
            QueueChildren(links.Root, root.Links.Children);
            while (pending.TryPop(out var entry))
            {
                if (!visited.Add(entry.Id)) throw new InvalidDataException("The classification tree repeats an item.");
                var item = ReadItem(entry.Id);
                if (item.Links.Parent != entry.Parent || item.Links.Root != Guid.Empty)
                    throw new InvalidDataException("A classification item and its parent have conflicting links.");
                items.Add(new PlaClassificationItem(entry.Id, entry.Parent == links.Root ? null : entry.Parent,
                    item.Id, item.Name, item.Description));
                QueueChildren(entry.Id, item.Links.Children);

                if (elements is null) continue;
                foreach (Guid element in item.Links.Elements)
                {
                    if (!elements.ContainsKey(element)) continue;
                    if (!classified.Add(element))
                        throw new InvalidDataException("An element has conflicting assignments within a classification system.");
                    if (!assignments.TryGetValue(element, out var saved)) assignments.Add(element, saved = []);
                    saved.Add(new PlaClassificationAssignment(systemId, entry.Id));
                }
            }
            systems.Add(new PlaClassificationSystem(systemId, name, description, source, version, date, items.AsReadOnly()));

            void QueueChildren(Guid parent, Guid[] children)
            {
                for (int i = children.Length - 1; i >= 0; i--)
                    pending.Push((children[i], parent));
            }
        }
        return (systems.AsReadOnly(), assignments);

        (string Id, string Name, string Description, ClassificationLinks Links) ReadItem(Guid id)
        {
            if (!bodies.TryGetValue(id, out var body) || new Guid(body.AsSpan(0, 16)) != ClassificationItemClass)
                throw new InvalidDataException($"Classification item {id} is missing.");
            var fields = ReadClassificationFrame(body, 3, out var relations);
            string code = ReadFramedText(ref fields);
            string name = ReadFramedText(ref fields);
            string description = ReadFramedText(ref fields);
            CheckClassificationFieldEnd(fields);
            return (code, name, description, ReadClassificationLinks(relations, rootTag: 0x12));
        }
    }

    private static ReadOnlySpan<byte> ReadClassificationFrame(ReadOnlySpan<byte> body, ushort fieldKind,
        out ReadOnlySpan<byte> relations)
    {
        relations = default;
        if (body.Length < 59 || BinaryPrimitives.ReadUInt16LittleEndian(body[39..]) != 1 ||
            body[41] is not (0x82 or 0x83) || !body[42..].StartsWith(RecordFrameTail) ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[57..]) != fieldKind) return default;
        return ReadRecordFields(body, 59, out relations);
    }

    private static DateOnly? ReadClassificationDate(ref ReadOnlySpan<byte> fields)
    {
        if (fields.Length < 6) throw new InvalidDataException("The classification date is truncated.");
        int year = BinaryPrimitives.ReadUInt16LittleEndian(fields);
        int month = BinaryPrimitives.ReadUInt16LittleEndian(fields[2..]);
        int day = BinaryPrimitives.ReadUInt16LittleEndian(fields[4..]);
        fields = fields[6..];
        if (year == 0 && month == 0 && day == 0) return null;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            throw new InvalidDataException("The classification date is invalid.");
        return new DateOnly(year, month, day);
    }

    private static void CheckClassificationFieldEnd(ReadOnlySpan<byte> fields)
    {
        if (fields.Length != 1 || fields[0] > 1)
            throw new InvalidDataException("The classification field frame has an invalid final flag.");
    }

    private static ClassificationLinks ReadClassificationLinks(ReadOnlySpan<byte> data, byte rootTag)
    {
        Guid root = Guid.Empty, parent = Guid.Empty;
        Guid[]? children = null, elements = null;
        while (data.Length >= 17)
        {
            var targets = ReadRelationshipEntry(ref data, out byte tag, out Guid key);
            if (key == ClassificationRootLink && tag == rootTag && root == Guid.Empty)
                root = ReadRelationshipTarget(targets);
            else if (key == ClassificationParentChildLink && tag == 0x12 && parent == Guid.Empty)
                parent = ReadRelationshipTarget(targets);
            else if (key == ClassificationParentChildLink && tag is (1 or 0x11) && children is null)
                children = ReadRelationshipTargets(targets);
            else if (key == ClassificationAssignmentLink && tag is (2 or 0x12) && elements is null)
                elements = ReadRelationshipTargets(targets);
            else if (key == ClassificationRootLink || key == ClassificationParentChildLink || key == ClassificationAssignmentLink)
                throw new InvalidDataException("A classification record has conflicting relationship roles.");
        }
        CheckRelationshipEnd(data);
        return new ClassificationLinks(root, parent, children ?? [], elements ?? []);
    }

    private readonly record struct ClassificationLinks(Guid Root, Guid Parent, Guid[] Children, Guid[] Elements);
}
