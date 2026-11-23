using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid NavigatorDirectoryLayout = new("06957baa-872a-40ea-b18b-2d4d1bcef8f0");
    private static readonly Guid NavigatorRootClass = new("2bbbecb3-0ecd-4228-897b-cc6ffb35d37f");
    private static readonly Guid LayoutBookClass = new("983b7466-b012-43db-a637-bc6f93076c34");
    private static readonly Guid LayoutSubsetClass = new("cf1797c1-69ec-4648-b5c5-57f898ab530b");
    private static readonly Guid LayoutItemClass = new("973bdcc9-e2f6-464f-85fb-833be20f5de5");
    private static readonly Guid MasterFolderClass = new("6253793e-0a1f-4691-811b-4900cba006d1");
    private static readonly Guid MasterLayoutClass = new("9d76eef2-a7cf-43b2-b09b-6d83d9607baa");
    private static readonly Guid LayoutDrawingClass = new("10440840-10b2-4cab-99d7-54f8af93da44");

    private static readonly Guid LayoutSourceClass = new("af0e45a5-fb37-4d8e-9132-d739c85efb91");
    private static readonly Guid MasterSourceClass = new("2092ceaa-500d-4520-b62a-63403a643735");
    private static readonly Guid DrawingSourceClass = new("31f5f16f-4b9e-41a8-9a32-f006ed168560");
    private static readonly Guid LayoutMetadataClass = new("39b16f73-c419-4640-8ceb-682d7a0e8ff9");
    private static readonly Guid MasterMetadataClass = new("93de1151-dc0a-4fe6-b394-54d7430988b5");
    private static readonly Guid DrawingMetadataClass = new("8c3b244d-a5a4-45d6-b917-ce005c1c6161");
    private static readonly Guid LayoutSettingsClass = new("f080c777-1d2e-40df-91b4-c6169b14e516");
    private static readonly Guid LayoutDatabaseClass = new("32acaf3c-26b8-4593-926c-f81a205550ba");
    private static readonly Guid MasterDatabaseClass = new("041e23dd-9fca-4a53-9633-8865f82a3a40");
    private static readonly Guid DrawingElementClass = new("2dbe8c52-93b3-46b5-a0a9-2492c45c2ab3");
    private static readonly Guid DrawingViewMemoClass = new("ba3407e3-8afd-4eb7-9f53-f197ff35c20e");
    private static readonly Guid SavedViewClass = new("89967df1-d45d-4078-9278-9b637f992e7b");
    private static readonly Guid SavedIndependentViewClass = new("b853520a-b01f-40b4-ad9c-8e855cb09a72");

    private static readonly Guid NavigatorTreeLink = new("33788fbb-8e6c-4a8d-a816-705f8d1ce5c6");
    private static readonly Guid NavigatorSettingLink = new("22a736de-07dc-49d4-9ab1-df90d9118b23");
    private static readonly Guid NavigatorSourceLink = new("fe91e636-9138-4f5d-9d85-b5d11ed2387b");
    private static readonly Guid NavigatorMetadataLink = new("6cbbd184-9a9c-4907-8363-ee7dd8141394");
    private static readonly Guid LayoutSourceLink = new("90515593-8e6f-4ce1-9c2c-5746fc32716a");
    private static readonly Guid MasterSourceLink = new("e03210e5-e70a-45da-8184-64835d8aeb6a");
    private static readonly Guid LayoutDatabaseLink = new("b1ddeca8-d51c-41c2-9a1a-77a1bcb3a5bb");
    private static readonly Guid MasterDatabaseLink = new("4b952067-803c-4aee-bc0d-8d1bbd107c6f");
    private static readonly Guid LayoutMasterLink = new("980d1d7e-00c4-4f38-a77b-92108e7b660b");
    private static readonly Guid DrawingDatabaseLink = new("daade85d-fe69-4343-8254-4655416e28f6");
    private static readonly Guid DatabaseDrawingLink = new("ac4d07f4-7e7c-4337-8bec-c82b034f4216");
    private static readonly Guid DrawingViewMemoLink = new("004ca348-04da-4f8b-bf94-22d0e313040b");
    private static readonly Guid DrawingViewLink = new("77201cab-66f6-4947-831d-c851ba54217d");
    private static readonly Guid NavigatorViewLink = new("bd38ee00-e866-43d5-a407-2437516d65d0");

    internal static PlaLayoutBook? ReadLayoutBook(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var records = ReadLayoutRecords(document, classes);
        var roots = records.Where(r => RecordClass(r.Value) == LayoutBookClass).Take(2).ToArray();
        if (roots.Length == 0) return null;
        if (roots.Length != 1) throw new InvalidDataException("The layout book has conflicting roots.");
        var (root, rootBody) = roots[0];
        var parents = ReadNavigatorLink(rootBody, NavigatorTreeLink, 0x12);
        if (parents.Length != 1 || !records.TryGetValue(parents[0], out var wrapper) ||
            RecordClass(wrapper) != NavigatorRootClass ||
            !ReadNavigatorLink(wrapper, NavigatorTreeLink, 1, 0x11).Contains(root))
            throw new InvalidDataException("The layout book has an invalid navigator root link.");

        var views = IndexLayoutViews(records);
        var pending = new Stack<(Guid Item, Guid Parent, Guid? Database)>();
        var visited = new HashSet<Guid> { root };
        var items = new List<PlaLayoutBookItem>();
        QueueChildren(root, rootBody, null);
        while (pending.TryPop(out var entry))
        {
            if (!visited.Add(entry.Item) || !records.TryGetValue(entry.Item, out var body))
                throw new InvalidDataException("The layout book has a missing or repeated item.");
            if (!ReadNavigatorLink(body, NavigatorTreeLink, 0x12).SequenceEqual([entry.Parent]))
                throw new InvalidDataException("A layout book item has a conflicting parent link.");
            var item = ReadLayoutBookItem(records, views, body, entry.Item, entry.Parent, entry.Database);
            items.Add(item);
            QueueChildren(entry.Item, body, item.DatabaseGuid);
        }
        ResolveMasterLayouts(records, items);
        ResolveLayoutRevisions(document, classes, records, items);
        return new PlaLayoutBook(root, items.AsReadOnly());

        void QueueChildren(Guid parent, byte[] body, Guid? database)
        {
            var children = ReadNavigatorLink(body, NavigatorTreeLink, 1, 0x11);
            for (int i = children.Length - 1; i >= 0; i--) pending.Push((children[i], parent, database));
        }
    }

    private static PlaLayoutBookItem ReadLayoutBookItem(Dictionary<Guid, byte[]> records, Dictionary<Guid, Guid?> views,
        byte[] body, Guid id, Guid parent, Guid? database)
    {
        Guid type = RecordClass(body);
        if (type == MasterFolderClass)
            return new PlaLayoutBookItem(id, parent, "MasterFolderItem", null, null, null, null, null);
        if (type == LayoutSubsetClass)
        {
            var fields = ReadRecordFields(body, 56, out _);
            if (!HasNavigatorEnvelope(body, 49) || !body.AsSpan(39, 6).SequenceEqual(new byte[] { 1, 0, 3, 0, 8, 1 }))
                throw new InvalidDataException("A layout subset has an invalid name frame.");
            string number = ReadFramedText(ref fields);
            if (fields.IsEmpty || fields[0] > 1) throw new InvalidDataException("A layout subset's name is truncated.");
            fields = fields[1..];
            return new PlaLayoutBookItem(id, parent, "SubsetItem", ReadFramedText(ref fields), number, null, null, null);
        }

        var (label, sourceClass, metadataClass) = type == LayoutItemClass ? ("LayoutItem", LayoutSourceClass, LayoutMetadataClass) :
            type == MasterLayoutClass ? ("MasterLayoutItem", MasterSourceClass, MasterMetadataClass) :
            type == LayoutDrawingClass ? ("DrawingItem", DrawingSourceClass, DrawingMetadataClass) :
            throw new InvalidDataException("The layout book contains an unsupported item class.");
        var (sourceId, source) = ReadLinkedNavigatorRecord(records, id, body, NavigatorSourceLink, sourceClass, 0x12, 0x11);
        var (metadataId, metadata) = ReadLinkedNavigatorRecord(records, sourceId, source, NavigatorMetadataLink, metadataClass, 0x12, 0x11);
        int textOffset = type == LayoutDrawingClass ? 99 : 56;
        var textFields = ReadRecordFields(metadata, textOffset, out _);
        if (!HasNavigatorEnvelope(metadata, type == LayoutDrawingClass ? 53 : 49))
            throw new InvalidDataException("A layout book item's text frame is invalid.");
        string first = ReadFramedText(ref textFields);
        string second = ReadFramedText(ref textFields);
        if (type == LayoutDrawingClass)
        {
            if (textFields.Length <= 40) throw new InvalidDataException("The drawing's source path is truncated.");
            var pathField = textFields[40..];
            string path = ReadFramedText(ref pathField);
            Guid? drawingSource = path.Length == 0 && pathField.Length >= 8 &&
                BinaryPrimitives.ReadUInt32LittleEndian(pathField[4..]) is 0x802C or 0x8030 or 0x8032 ? sourceId :
                ReadDrawingView(records, views, database, metadataId, metadata);
            return new PlaLayoutBookItem(id, parent, label, first, second, drawingSource, path, null);
        }
        if (type == MasterLayoutClass)
        {
            if (!ReadNavigatorLink(body, MasterSourceLink, 0x12).SequenceEqual([sourceId]))
                throw new InvalidDataException("A master layout has a conflicting source link.");
            return new PlaLayoutBookItem(id, parent, label, second, second, sourceId, null,
                ReadMasterPaperSize(ReadLayoutSettings(records, id, body)?.Body))
            {
                DatabaseGuid = ReadLayoutDatabase(records, metadataId, metadata, MasterDatabaseLink, MasterDatabaseClass)
            };
        }
        if (!ReadNavigatorLink(body, LayoutSourceLink, 0x12).SequenceEqual([sourceId]) ||
            !HasNavigatorEnvelope(body, 57) || !body.AsSpan(39, 2).SequenceEqual(new byte[] { 1, 1 }))
            throw new InvalidDataException("A layout has an invalid number frame or source link.");
        var numberField = ReadRecordFields(body, 64, out _);
        return new PlaLayoutBookItem(id, parent, label, second, ReadFramedText(ref numberField), sourceId, null, null)
        {
            DatabaseGuid = ReadLayoutDatabase(records, metadataId, metadata, LayoutDatabaseLink, LayoutDatabaseClass)
        };
    }

    private static Dictionary<Guid, Guid?> IndexLayoutViews(Dictionary<Guid, byte[]> records)
    {
        var result = new Dictionary<Guid, Guid?>();
        foreach (var (id, record) in records)
        {
            var type = RecordClass(record);
            if (type != SavedViewClass && type != SavedIndependentViewClass) continue;
            var target = ReadNavigatorLink(record, NavigatorViewLink, 0x12);
            if (target.Length != 0 && !result.TryAdd(target[0], id)) result[target[0]] = null;
        }
        return result;
    }

    private static Guid? ReadDrawingView(Dictionary<Guid, byte[]> records, Dictionary<Guid, Guid?> views,
        Guid? databaseId, Guid metadataId, byte[] metadata)
    {
        if (databaseId is not { } database || !metadata.AsSpan(59, 3).SequenceEqual(new byte[] { 1, 6, 0 }) ||
            !ReadNavigatorLink(metadata, DrawingDatabaseLink, 0x12).SequenceEqual([database]))
            return null;
        var owner = records[database];
        var drawing = new Guid(metadata.AsSpan(62, 16));
        if (!records.TryGetValue(drawing, out var element) || RecordClass(element) != DrawingElementClass ||
            !ReadNavigatorLink(element, DatabaseDrawingLink, 0x12).SequenceEqual([database]) ||
            !ReadNavigatorLink(owner, DatabaseDrawingLink, 1, 0x11).Contains(drawing) ||
            !ReadNavigatorLink(owner, DrawingDatabaseLink, 1, 0x11).Contains(metadataId))
            return null;
        var link = ReadNavigatorLink(element, DrawingViewMemoLink, 0x11);
        if (link.Length == 0 || !records.TryGetValue(link[0], out var memo) || RecordClass(memo) != DrawingViewMemoClass ||
            !ReadNavigatorLink(memo, DrawingViewMemoLink, 0x12).SequenceEqual([drawing])) return null;
        var view = ReadNavigatorLink(memo, DrawingViewLink, 0x12);
        return view.Length == 0 ? null : views.GetValueOrDefault(view[0]);
    }

    private static Guid? ReadLayoutDatabase(Dictionary<Guid, byte[]> records, Guid metadataId, byte[] metadata,
        Guid role, Guid databaseClass)
    {
        var link = ReadNavigatorLink(metadata, role, 0x12);
        if (link.Length == 0 || !records.TryGetValue(link[0], out var database)) return null;
        if (RecordClass(database) != databaseClass ||
            !ReadNavigatorLink(database, role, 0x11).SequenceEqual([metadataId]))
            throw new InvalidDataException("A layout has a conflicting database link.");
        return link[0];
    }

    private static void ResolveMasterLayouts(Dictionary<Guid, byte[]> records, List<PlaLayoutBookItem> items)
    {
        var masters = new Dictionary<Guid, (PlaLayoutBookItem Item, HashSet<Guid> Layouts)>();
        foreach (var item in items)
        {
            if (item.Type != "MasterLayoutItem" || item.DatabaseGuid is not { } database) continue;
            var layouts = ReadNavigatorLink(records[database], LayoutMasterLink, 1, 0x11).ToHashSet();
            if (!masters.TryAdd(database, (item, layouts)))
                throw new InvalidDataException("Master layouts share the same database.");
        }
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Type != "LayoutItem" || item.DatabaseGuid is not { } database) continue;
            var link = ReadNavigatorLink(records[database], LayoutMasterLink, 0x12);
            if (link.Length == 0 || !masters.TryGetValue(link[0], out var master)) continue;
            if (!master.Layouts.Contains(database))
                throw new InvalidDataException("A layout and its master have conflicting database links.");
            items[i] = item with { MasterLayoutGuid = master.Item.Guid, PaperSize = master.Item.PaperSize };
        }
    }

    private static (Guid Id, byte[] Body)? ReadLayoutSettings(Dictionary<Guid, byte[]> records, Guid item, byte[] body)
    {
        var matches = ReadNavigatorLink(body, NavigatorSettingLink, 1, 0x11)
            .Where(id => records.TryGetValue(id, out var record) && RecordClass(record) == LayoutSettingsClass)
            .Take(2).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new InvalidDataException("A layout has conflicting settings.");
        var settings = records[matches[0]];
        if (!ReadNavigatorLink(settings, NavigatorSettingLink, 0x12).SequenceEqual([item]))
            throw new InvalidDataException("A layout's settings have a conflicting owner.");
        return (matches[0], settings);
    }

    private static PlaLayoutPaperSize? ReadMasterPaperSize(byte[]? settings)
    {
        if (settings is null || !HasNavigatorEnvelope(settings, 51) || settings.Length < 94 ||
            !settings.AsSpan(92, 2).SequenceEqual(new byte[] { 0, 2 })) return null;
        var dimensions = ReadRecordFields(settings, 94, out _);
        if (dimensions.Length < 16) throw new InvalidDataException("A master layout's paper size is truncated.");
        double width = BinaryPrimitives.ReadDoubleLittleEndian(dimensions);
        double height = BinaryPrimitives.ReadDoubleLittleEndian(dimensions[8..]);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidDataException("A master layout has an invalid paper size.");
        return new PlaLayoutPaperSize(width, height);
    }

    private static bool HasNavigatorEnvelope(byte[] body, int offset) =>
        body.Length >= offset + 6 && body[offset] is 0x82 or 0x83 &&
        body.AsSpan(offset + 1, 5).SequenceEqual(new byte[] { 0, 3, 0, 0, 0 });

    private static (Guid Id, byte[] Record) ReadLinkedNavigatorRecord(Dictionary<Guid, byte[]> records, Guid item,
        byte[] body, Guid role, Guid expectedClass, byte tag, byte reciprocalTag)
    {
        var targets = ReadNavigatorLink(body, role, tag);
        if (targets.Length != 1 || !records.TryGetValue(targets[0], out var record) || RecordClass(record) != expectedClass ||
            !ReadNavigatorLink(record, role, reciprocalTag).SequenceEqual([item]))
            throw new InvalidDataException("A layout book item has an invalid source or metadata link.");
        return (targets[0], record);
    }

    private static Guid[] ReadNavigatorLink(byte[] body, Guid role, byte tag, byte alternateTag = 0)
    {
        ReadRecordFields(body, 39, out var relationships);
        Guid[]? result = null;
        while (relationships.Length >= 17)
        {
            var targets = ReadRelationshipEntry(ref relationships, out byte actualTag, out var actualRole);
            if (actualRole != role || actualTag != tag && actualTag != alternateTag) continue;
            if (result is not null) throw new InvalidDataException("A navigator item has repeated links of the same role.");
            result = ReadRelationshipTargets(targets);
        }
        CheckRelationshipEnd(relationships);
        return result ?? [];
    }

    private static Guid RecordClass(byte[] body) => new(body.AsSpan(0, 16));

    private static Dictionary<Guid, byte[]> ReadLayoutRecords(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var wanted = new HashSet<Guid>
        {
            NavigatorRootClass, LayoutBookClass, LayoutSubsetClass, LayoutItemClass, MasterFolderClass, MasterLayoutClass,
            LayoutDrawingClass, LayoutSourceClass, MasterSourceClass, DrawingSourceClass, LayoutMetadataClass,
            MasterMetadataClass, DrawingMetadataClass, LayoutSettingsClass, LayoutRevisionStateClass, LayoutDatabaseClass, MasterDatabaseClass,
            DrawingElementClass, DrawingViewMemoClass, SavedViewClass, SavedIndependentViewClass, LayoutRevisionOverrideClass,
        };
        var kinds = classes.Where(c => wanted.Contains(c.Guid)).ToDictionary(c => c.Number, c => c.Guid);
        var result = new Dictionary<Guid, byte[]>();
        if (!kinds.ContainsValue(LayoutBookClass)) return result;
        var directories = new Dictionary<ulong, List<(ObjectDirectoryRecord Record, bool ShortGuid)>>();
        Span<byte> tail = stackalloc byte[DirectoryFinder.RecordSize];
        tail.Clear();
        foreach (var block in document.Blocks)
        {
            if (!IsRawTable(block, DirectoryKind) || block.UnpackedSize < 40) continue;
            ReadOnlySpan<byte> data = document.ReadPayload(block);
            int header = DirectoryHeaderSize(data, NavigatorDirectoryLayout);
            bool navigator = header != 0;
            if (!navigator) header = DirectoryHeaderSize(data, DirectoryLayout);
            if (header == 0) continue;
            ulong units = BinaryPrimitives.ReadUInt64LittleEndian(data[(header - 40)..]);
            var owner = new Guid(data.Slice(header - 32, 16));
            if (!navigator && !ContainsLayoutDatabase(data[header..], owner, kinds)) continue;
            for (int i = header; i <= data.Length - DirectoryFinder.RecordSize; i += DirectoryFinder.RecordSize)
            {
                var record = DirectoryFinder.ReadRecord(data[i..]);
                if (!kinds.TryGetValue(record.ClassNumber, out var type)) continue;
                bool database = IsLayoutDatabase(type);
                bool model = database || type == DrawingElementClass || type == DrawingViewMemoClass;
                if (navigator == model || database && record.ObjectId != owner) continue;
                AddDirectoryEntry(units, record, shortGuid: false);
            }
            // The prefixed directory can end two bytes before the end of its last entry.
            if (!navigator && header == 42 && (data.Length - header) % DirectoryFinder.RecordSize == DirectoryFinder.RecordSize - 2)
            {
                data[^(DirectoryFinder.RecordSize - 2)..].CopyTo(tail);
                var record = DirectoryFinder.ReadRecord(tail);
                if (kinds.GetValueOrDefault(record.ClassNumber) == DrawingViewMemoClass)
                    AddDirectoryEntry(units, record, shortGuid: true);
            }
        }
        if (directories.Count == 0) return result;
        foreach (var pool in document.FindCompressedPools())
        {
            long length = pool.TotalDecompressed;
            if (length % ExtentUnit != 0 || !directories.TryGetValue((ulong)(length / ExtentUnit), out var entries)) continue;
            var data = pool.Decode();
            foreach (var (record, shortGuid) in entries)
            {
                var entry = shortGuid ? CompleteDrawingMemoEntry(data, record) : record;
                if (entry.ObjectId == Guid.Empty) continue;
                var body = ReadIndexedPayload(data, entry);
                if (body is null || body.Length < 46 || RecordClass(body) != kinds[entry.ClassNumber]) continue;
                if (!result.TryAdd(entry.ObjectId, body) && !result[entry.ObjectId].AsSpan(35).SequenceEqual(body.AsSpan(35)))
                    throw new InvalidDataException("A layout book record has conflicting copies.");
            }
        }
        return result;

        void AddDirectoryEntry(ulong units, ObjectDirectoryRecord record, bool shortGuid)
        {
            if (record.ObjectId == Guid.Empty || record.Count == 0 || record.Position > units ||
                record.Count > units - record.Position) return;
            if (!directories.TryGetValue(units, out var entries)) directories.Add(units, entries = []);
            entries.Add((record, shortGuid));
        }
    }

    private static bool ContainsLayoutDatabase(ReadOnlySpan<byte> directory, Guid owner, Dictionary<ushort, Guid> kinds)
    {
        for (int i = 0; i <= directory.Length - DirectoryFinder.RecordSize; i += DirectoryFinder.RecordSize)
        {
            var record = DirectoryFinder.ReadRecord(directory[i..]);
            if (record.ObjectId == owner && IsLayoutDatabase(kinds.GetValueOrDefault(record.ClassNumber))) return true;
        }
        return false;
    }

    private static bool IsLayoutDatabase(Guid type) => type == LayoutDatabaseClass || type == MasterDatabaseClass;

    private static ObjectDirectoryRecord CompleteDrawingMemoEntry(ReadOnlySpan<byte> pool, ObjectDirectoryRecord record)
    {
        var extent = pool.Slice((int)(record.Position * ExtentUnit), (int)(record.Count * ExtentUnit));
        if (!BlockHeaderParser.TryParse(extent, 0, extent.Length, out var header, poolSubgroup: true) ||
            header.Tag != "BL" || header.Version != 1 || header.Kind != record.ClassNumber || header.Guid is not { } id)
            return default;
        Span<byte> prefix = stackalloc byte[16];
        Span<byte> candidate = stackalloc byte[16];
        record.ObjectId.TryWriteBytes(prefix);
        id.TryWriteBytes(candidate);
        return prefix[..14].SequenceEqual(candidate[..14]) ? record with { ObjectId = id } : default;
    }
}
