using System.Buffers.Binary;
using System.Text;
using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

public partial class ElementReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsLayoutBookInSavedOrder(bool prefixedDirectory)
    {
        var fixture = LayoutBookFixture(prefixedDirectory);
        using var document = PlaDocument.Open(new MemoryStream(fixture.File));
        var book = Assert.IsType<PlaLayoutBook>(document.ReadLayoutBook());
        Assert.Equal(fixture.Book, book.Guid);
        Assert.Equal(fixture.Items, book.Items);
    }

    [Theory]
    [InlineData("wrong parent")]
    [InlineData("wrong metadata owner")]
    [InlineData("duplicate child")]
    [InlineData("wrong database owner")]
    [InlineData("wrong master assignment")]
    public void RejectsInconsistentLayoutBookLinks(string problem)
    {
        using var document = PlaDocument.Open(new MemoryStream(LayoutBookFixture(false, problem).File));
        Assert.Throws<InvalidDataException>(document.ReadLayoutBook);
    }

    [Fact]
    public void ReadsLayoutHierarchyWhenLayoutDatabasesAreUnavailable()
    {
        var fixture = LayoutBookFixture(includeDatabases: false);
        using var document = PlaDocument.Open(new MemoryStream(fixture.File));
        var book = Assert.IsType<PlaLayoutBook>(document.ReadLayoutBook());
        Assert.Equal(fixture.Items, book.Items);
        var layout = Assert.Single(book.Items, item => item.Type == "LayoutItem");
        Assert.Null(layout.MasterLayoutGuid);
        Assert.Null(layout.PaperSize);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "wrong last entry prefix")]
    [InlineData(false, "ambiguous drawing view")]
    public void ResolvesInternalDrawingFromTheSavedViewLink(bool shortEntry, string? problem)
    {
        var fixture = LayoutBookFixture(shortEntry, problem, internalDrawing: true, shortMemoEntry: shortEntry);
        using var document = PlaDocument.Open(new MemoryStream(fixture.File));
        var book = Assert.IsType<PlaLayoutBook>(document.ReadLayoutBook());
        Assert.Equal(fixture.Items, book.Items);
        var drawing = Assert.Single(book.Items, item => item.Type == "DrawingItem");
        Assert.Equal(problem is null, drawing.SourceGuid.HasValue);
    }

    [Theory]
    [InlineData("empty", "", false, false, 0)]
    [InlineData("first", "Z", true, false, 1)]
    [InlineData("history", "A", true, false, 2)]
    [InlineData("open", "01", false, true, 0)]
    [InlineData("mixed", "01", true, true, 2)]
    public void ReadsSavedLayoutRevisions(string scenario, string? current, bool issued, bool open, int count)
    {
        var fixture = LayoutBookFixture(revisions: scenario);
        using var document = PlaDocument.Open(new MemoryStream(fixture.File));
        var book = Assert.IsType<PlaLayoutBook>(document.ReadLayoutBook());
        var revision = Assert.IsType<PlaLayoutRevision>(Assert.Single(book.Items, item => item.Type == "LayoutItem").Revision);
        Assert.Equal(current, revision.CurrentRevisionId);
        Assert.Equal(open ? null : current, revision.CurrentFinalRevisionId);
        Assert.Equal(issued, revision.HasIssuedRevision);
        Assert.Equal(open, revision.HasActualRevision);
        Assert.Equal(new[] { "Z", "A" }.Take(count), revision.IssuedRevisions.Select(entry => entry.Id));
        foreach (var entry in revision.IssuedRevisions)
        {
            Assert.Equal("Issue for " + entry.Id, entry.Transmittal.Name);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), entry.Transmittal.IssuedAtUtc);
            Assert.Equal("Author", entry.Transmittal.IssuedBy);
        }
        Assert.Equal(open ? "Draft" : null, revision.OpenTransmittal?.Id);
        Assert.Null(revision.OpenTransmittal?.IssuedAtUtc);
        Assert.Null(Assert.Single(book.Items, item => item.Type == "DrawingItem").Revision);
    }

    [Theory]
    [InlineData("01", "01", null, "02")]
    [InlineData("00", "01|03|00", null, "04")]
    [InlineData("R099", "R099", null, "R100")]
    [InlineData("T-Y", "T-Y", null, "T-Z")]
    [InlineData("R-Z", "R-Z", null, "R-ZA")]
    [InlineData("Manual", "01|03", "03", "04")]
    [InlineData("rev", "REV", null, "rev")]
    [InlineData("ending!", "ending!", null, null)]
    public void ResolvesOpenLayoutRevisionIdFromSavedNumbering(string numberingBase, string issuedIds,
        string? earlierOverride, string? expected)
    {
        var fixture = LayoutBookFixture(revisions: "mixed", revisionNumbers: issuedIds.Split('|'),
            revisionBase: numberingBase, earlierOverride: earlierOverride);
        using var document = PlaDocument.Open(new MemoryStream(fixture.File));
        var book = Assert.IsType<PlaLayoutBook>(document.ReadLayoutBook());
        var revision = Assert.IsType<PlaLayoutRevision>(Assert.Single(book.Items, item => item.Type == "LayoutItem").Revision);
        Assert.True(revision.HasActualRevision);
        Assert.True(revision.HasIssuedRevision);
        Assert.Equal(expected, revision.CurrentRevisionId);
        Assert.Null(revision.CurrentFinalRevisionId);
    }

    [Fact]
    public void UsesTheOpenTransmittalIdWhenItOverridesRevisionNumbering()
    {
        using var document = PlaDocument.Open(new MemoryStream(LayoutBookFixture(revisions: "override").File));
        var book = Assert.IsType<PlaLayoutBook>(document.ReadLayoutBook());
        var revision = Assert.IsType<PlaLayoutRevision>(Assert.Single(book.Items, item => item.Type == "LayoutItem").Revision);
        Assert.Equal("Draft", revision.CurrentRevisionId);
        Assert.Null(revision.CurrentFinalRevisionId);
        Assert.True(revision.OpenTransmittal?.OverridesRevisionIds);
    }

    [Theory]
    [InlineData("wrong revision owner")]
    [InlineData("wrong transmittal member")]
    [InlineData("truncated transmittal")]
    public void RejectsInconsistentLayoutRevisionLinks(string problem)
    {
        using var document = PlaDocument.Open(new MemoryStream(LayoutBookFixture(problem: problem, revisions: "history").File));
        Assert.Throws<InvalidDataException>(document.ReadLayoutBook);
    }

    [Fact]
    public void RejectsARevisionNumberingOverrideWithTheWrongOwner()
    {
        var fixture = LayoutBookFixture(problem: "wrong override owner", revisions: "mixed", earlierOverride: "03");
        using var document = PlaDocument.Open(new MemoryStream(fixture.File));
        Assert.Throws<InvalidDataException>(document.ReadLayoutBook);
    }

    internal static (byte[] File, Guid Book, PlaLayoutBookItem[] Items) LayoutBookFixture(bool prefixedDirectory = false,
        string? problem = null, bool includeDatabases = true, bool internalDrawing = false, bool shortMemoEntry = false,
        string? revisions = null, string[]? revisionNumbers = null, string revisionBase = "01", string? earlierOverride = null)
    {
        Guid directoryType = new("06957baa-872a-40ea-b18b-2d4d1bcef8f0");
        Guid tree = new("33788fbb-8e6c-4a8d-a816-705f8d1ce5c6");
        Guid setting = new("22a736de-07dc-49d4-9ab1-df90d9118b23");
        Guid source = new("fe91e636-9138-4f5d-9d85-b5d11ed2387b");
        Guid metadata = new("6cbbd184-9a9c-4907-8363-ee7dd8141394");
        Guid layoutSource = new("90515593-8e6f-4ce1-9c2c-5746fc32716a");
        Guid masterSource = new("e03210e5-e70a-45da-8184-64835d8aeb6a");
        Guid layoutDatabaseLink = new("b1ddeca8-d51c-41c2-9a1a-77a1bcb3a5bb");
        Guid masterDatabaseLink = new("4b952067-803c-4aee-bc0d-8d1bbd107c6f");
        Guid assignment = new("980d1d7e-00c4-4f38-a77b-92108e7b660b");
        Guid drawingDatabaseLink = new("daade85d-fe69-4343-8254-4655416e28f6");
        Guid databaseDrawingLink = new("ac4d07f4-7e7c-4337-8bec-c82b034f4216");
        Guid drawingMemoLink = new("004ca348-04da-4f8b-bf94-22d0e313040b");
        Guid drawingViewLink = new("77201cab-66f6-4947-831d-c851ba54217d");
        Guid navigatorViewLink = new("bd38ee00-e866-43d5-a407-2437516d65d0");
        Guid revisionStateLink = new("6c8ed16c-a969-4a25-9f40-1ab657dc6b50");
        Guid revisionOverrideLink = new("a0e2f86f-4496-4a59-bbbe-ef3a6f1267a9");
        Guid issuedRevisionLink = new("9d374292-829a-4553-a3e0-6631d9a0110c");
        Guid issuedTransmittalLink = new("3b14d41d-2596-4cb7-8767-21309d216a9c");
        Guid openTransmittalLink = new("2421d43e-0a52-4d18-acfb-54e6262d9dbc");
        Guid[] classes =
        [
            new("2bbbecb3-0ecd-4228-897b-cc6ffb35d37f"), new("983b7466-b012-43db-a637-bc6f93076c34"),
            new("cf1797c1-69ec-4648-b5c5-57f898ab530b"), new("973bdcc9-e2f6-464f-85fb-833be20f5de5"),
            new("6253793e-0a1f-4691-811b-4900cba006d1"), new("9d76eef2-a7cf-43b2-b09b-6d83d9607baa"),
            new("10440840-10b2-4cab-99d7-54f8af93da44"), new("af0e45a5-fb37-4d8e-9132-d739c85efb91"),
            new("2092ceaa-500d-4520-b62a-63403a643735"), new("31f5f16f-4b9e-41a8-9a32-f006ed168560"),
            new("39b16f73-c419-4640-8ceb-682d7a0e8ff9"), new("93de1151-dc0a-4fe6-b394-54d7430988b5"),
            new("8c3b244d-a5a4-45d6-b917-ce005c1c6161"), new("f080c777-1d2e-40df-91b4-c6169b14e516"),
            new("32acaf3c-26b8-4593-926c-f81a205550ba"), new("041e23dd-9fca-4a53-9633-8865f82a3a40"),
            new("2dbe8c52-93b3-46b5-a0a9-2492c45c2ab3"), new("ba3407e3-8afd-4eb7-9f53-f197ff35c20e"),
            new("89967df1-d45d-4078-9278-9b637f992e7b"), new("169571f5-0665-4fec-8070-5e14962eaf1a"),
            new("2c2d6560-da21-4610-b3b8-fe05f4b5c431"), new("ad6bafb9-a10c-4258-aad3-97f97ac5d1ae"),
            new("8535adec-7d22-4334-8dfa-d3318ff35d23"),
        ];
        Guid root = Guid.NewGuid(), book = Guid.NewGuid(), subset = Guid.NewGuid(), sheet = Guid.NewGuid();
        Guid masterFolder = Guid.NewGuid(), master = Guid.NewGuid(), drawing = Guid.NewGuid();
        Guid sheetData = Guid.NewGuid(), masterData = Guid.NewGuid(), drawingData = Guid.NewGuid();
        Guid sheetText = Guid.NewGuid(), masterText = Guid.NewGuid(), drawingText = Guid.NewGuid(), paper = Guid.NewGuid();
        Guid sheetDatabase = Guid.NewGuid(), masterDatabase = Guid.NewGuid();
        Guid drawingElement = Guid.NewGuid(), memo = Guid.NewGuid(), view = Guid.NewGuid(), viewKey = Guid.NewGuid();
        Guid sheetSettings = Guid.NewGuid(), state = Guid.NewGuid(), openIssue = Guid.NewGuid();

        var extents = new List<(ushort Kind, Guid Id, byte[] Body)>();
        var revisionBlocks = new List<byte[]>();
        Add(1, root, [1], Link(0x11, tree, book));
        Add(2, book, [1], Link(0x12, tree, root),
            problem == "duplicate child" ? Link(1, tree, subset, subset, masterFolder) : Link(1, tree, subset, masterFolder));
        var subsetPrefix = Frame(17, 10);
        new byte[] { 1, 0, 3, 0, 8, 1 }.CopyTo(subsetPrefix, 0);
        Add(3, subset, Synthetic.Concat(subsetPrefix, Text("04"), [1], Text("Детали")),
            Link(0x12, tree, book), Link(0x11, tree, sheet));
        var sheetPrefix = Frame(25, 18);
        sheetPrefix[0] = 1;
        sheetPrefix[1] = 1;
        Add(4, sheet, Synthetic.Concat(sheetPrefix, Text("04.1")),
            Link(0x12, tree, problem == "wrong parent" ? book : subset), Link(0x11, tree, drawing),
            Link(0x12, source, sheetData), Link(0x12, layoutSource, sheetData),
            revisions is null ? [] : Link(0x11, setting, sheetSettings));
        Add(5, masterFolder, [1], Link(0x12, tree, book), Link(0x11, tree, master));
        Add(6, master, [1], Link(0x12, tree, masterFolder), Link(0x12, source, masterData),
            Link(0x12, masterSource, masterData), Link(0x11, setting, paper));
        Add(7, drawing, [1], Link(0x12, tree, sheet), Link(0x12, source, drawingData));
        Add(8, sheetData, [1], Link(0x11, source, sheet), Link(0x12, metadata, sheetText));
        Add(9, masterData, [1], Link(0x11, source, master), Link(0x12, metadata, masterText));
        Add(10, drawingData, [1], Link(0x11, source, drawing), Link(0x12, metadata, drawingText));
        Add(11, sheetText, Synthetic.Concat(Frame(17, 10), Text("Stored"), Text("Разрез")),
            Link(0x11, metadata, problem == "wrong metadata owner" ? masterData : sheetData),
            Link(0x12, layoutDatabaseLink, sheetDatabase));
        Add(12, masterText, Synthetic.Concat(Frame(17, 10), Text(""), Text("A3")), Link(0x11, metadata, masterData),
            Link(0x12, masterDatabaseLink, masterDatabase));
        var sourceType = new byte[] { 0, 0, 0, 0, internalDrawing ? (byte)0x14 : (byte)0x32, 0x80, 0, 0 };
        var drawingFrame = Frame(60, 14);
        new byte[] { 1, 6, 0 }.CopyTo(drawingFrame, 20);
        drawingElement.TryWriteBytes(drawingFrame.AsSpan(23));
        var drawingPath = internalDrawing ? @"\Views\Drawing" : "";
        Add(13, drawingText, Synthetic.Concat(drawingFrame, Text("Чертёж"), Text("1"), new byte[40], Text(drawingPath), sourceType),
            Link(0x11, metadata, drawingData), Link(0x12, drawingDatabaseLink, sheetDatabase));
        var paperFrame = Frame(71, 12);
        paperFrame[54] = 2;
        BinaryPrimitives.WriteDoubleLittleEndian(paperFrame.AsSpan(55), 0.420);
        BinaryPrimitives.WriteDoubleLittleEndian(paperFrame.AsSpan(63), 0.297);
        Add(14, paper, paperFrame, Link(0x12, setting, master));
        if (revisions is not null)
        {
            bool isOpen = revisions is "open" or "mixed" or "override";
            string[] numbers = revisionNumbers ?? (revisions is "history" or "mixed" ? ["Z", "A"] : revisions == "first" ? ["Z"] : []);
            var issued = new List<Guid>(numbers.Length);
            foreach (string number in numbers)
            {
                Guid revision = Guid.NewGuid(), issue = Guid.NewGuid();
                issued.Add(revision);
                revisionBlocks.Add(Record(21, revision, Synthetic.Concat([2, 0, 1, 0, 0x83, 0, 3, 0, 0, 0], Text(number)),
                    Link(0x12, issuedRevisionLink, problem == "wrong revision owner" ? paper : sheetSettings),
                    Link(0x12, issuedTransmittalLink, issue)));
                AddTransmittal(issue, number, problem == "wrong transmittal member" ? sheet : revision,
                    closed: true, overridesRevisionIds: number == earlierOverride);
            }
            byte[] history = issued.Count == 0 ? [] : Link(issued.Count == 1 ? (byte)0x11 : (byte)1, issuedRevisionLink, issued.ToArray());
            Add(14, sheetSettings, [1], Link(0x12, setting, sheet), Link(0x11, revisionStateLink, state), history);
            byte[] overrideLink = [];
            if (earlierOverride is not null)
            {
                Guid saved = Guid.NewGuid();
                Add(23, saved, Synthetic.Concat([1, 0, 0x83, 0, 3, 0, 0, 0], Text(earlierOverride)),
                    Link(0x12, revisionOverrideLink, problem == "wrong override owner" ? paper : state));
                overrideLink = Link(0x11, revisionOverrideLink, saved);
            }
            Add(20, state, Synthetic.Concat([1, 0, 1, 0, 0x83, 0, 3, 0, 0, 0], Text(revisionBase), Text("")),
                Link(0x12, revisionStateLink, sheetSettings), isOpen ? Link(0x12, openTransmittalLink, openIssue) : [], overrideLink);
            if (isOpen) AddTransmittal(openIssue, "Draft", state, closed: false, overridesRevisionIds: revisions == "override");
        }
        if (internalDrawing)
        {
            Add(19, view, [1], Link(0x12, navigatorViewLink, viewKey));
            if (problem == "ambiguous drawing view")
                Add(19, Guid.NewGuid(), [1], Link(0x12, navigatorViewLink, viewKey));
        }

        var data = new byte[512 * extents.Count];
        int headerOffset = prefixedDirectory ? 2 : 0;
        byte[] directory = new byte[headerOffset + 40 + 30 * extents.Count];
        BinaryPrimitives.WriteInt64LittleEndian(directory.AsSpan(headerOffset), data.Length / 16);
        Guid.NewGuid().TryWriteBytes(directory.AsSpan(headerOffset + 8));
        directoryType.TryWriteBytes(directory.AsSpan(headerOffset + 24));
        for (int i = 0; i < extents.Count; i++)
        {
            var (kind, id, bytes) = extents[i];
            bytes.CopyTo(data, 512 * i);
            Synthetic.Record(new ObjectDirectoryRecord(kind, (ulong)i * 32, 32, id))
                .CopyTo(directory, headerOffset + 40 + 30 * i);
        }
        var blocks = new List<byte[]>
        {
            ClassTable(classes.Select((c, i) => ((ushort)(i + 1), c)).ToArray()),
            Synthetic.Block(PlaBlockMode.Raw, directory, ver: 1, kind: 0xFDEA),
            Pool(data),
        };
        blocks.AddRange(revisionBlocks);
        if (includeDatabases)
        {
            Guid linkedMetadata = problem == "wrong database owner" ? masterText : sheetText;
            if (internalDrawing)
            {
                var table = new byte[headerOffset + 40 + 90];
                BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(headerOffset), 96);
                sheetDatabase.TryWriteBytes(table.AsSpan(headerOffset + 8));
                new Guid("1f9b53da-bb9f-4f97-b5d9-532843923eac").TryWriteBytes(table.AsSpan(headerOffset + 24));
                Synthetic.Record(new ObjectDirectoryRecord(15, 0, 32, sheetDatabase)).CopyTo(table, headerOffset + 40);
                Synthetic.Record(new ObjectDirectoryRecord(17, 32, 32, drawingElement)).CopyTo(table, headerOffset + 70);
                Synthetic.Record(new ObjectDirectoryRecord(18, 64, 32, problem == "wrong last entry prefix" ? Guid.NewGuid() : memo))
                    .CopyTo(table, headerOffset + 100);
                var pool = new byte[3 * 512];
                Record(15, sheetDatabase, [1], Link(0x12, assignment, masterDatabase), Link(0x11, layoutDatabaseLink, linkedMetadata),
                    Link(0x11, drawingDatabaseLink, drawingText), Link(0x11, databaseDrawingLink, drawingElement)).CopyTo(pool, 0);
                Record(17, drawingElement, [1], Link(0x12, databaseDrawingLink, sheetDatabase),
                    Link(0x11, drawingMemoLink, memo)).CopyTo(pool, 512);
                Record(18, memo, [1], Link(0x12, drawingMemoLink, drawingElement), Link(0x12, drawingViewLink, viewKey)).CopyTo(pool, 1024);
                if (shortMemoEntry) table = table[..^2];
                blocks.Add(Synthetic.Block(PlaBlockMode.Raw, table, ver: 1, kind: 0xFDEA));
                blocks.Add(Pool(pool));
            }
            else
                AddDatabase(15, sheetDatabase, 32, Link(0x12, assignment, masterDatabase),
                    Link(0x11, layoutDatabaseLink, linkedMetadata));
            AddDatabase(16, masterDatabase, 40, Link(0x11, masterDatabaseLink, masterText),
                Link(1, assignment, problem == "wrong master assignment" ? Guid.NewGuid() : sheetDatabase));
        }
        var file = Synthetic.Fdb(blocks.ToArray());
        return (file, book,
        [
            new PlaLayoutBookItem(subset, book, "SubsetItem", "Детали", "04", null, null, null),
            new PlaLayoutBookItem(sheet, subset, "LayoutItem", "Разрез", "04.1", sheetData, null, includeDatabases ? new(0.420, 0.297) : null)
            {
                DatabaseGuid = includeDatabases ? sheetDatabase : null,
                MasterLayoutGuid = includeDatabases ? master : null
            },
            new PlaLayoutBookItem(drawing, sheet, "DrawingItem", "Чертёж", "1",
                internalDrawing ? (problem is null ? view : null) : drawingData, drawingPath, null),
            new PlaLayoutBookItem(masterFolder, book, "MasterFolderItem", null, null, null, null, null),
            new PlaLayoutBookItem(master, masterFolder, "MasterLayoutItem", "A3", "A3", masterData, null, new(0.420, 0.297))
            {
                DatabaseGuid = includeDatabases ? masterDatabase : null
            },
        ]);

        void Add(ushort kind, Guid id, byte[] fields, params byte[][] links) =>
            extents.Add((kind, id, Record(kind, id, fields, links)));

        void AddTransmittal(Guid id, string number, Guid member, bool closed, bool overridesRevisionIds)
        {
            if (problem == "truncated transmittal")
            {
                var truncated = new byte[35];
                classes[21].TryWriteBytes(truncated);
                revisionBlocks.Add(Synthetic.Block(PlaBlockMode.Raw, truncated, ver: 1, kind: 22, guid: id, extra: 1));
                return;
            }
            var time = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(time, 1_700_000_000);
            var tail = new byte[13];
            tail[8] = overridesRevisionIds ? (byte)1 : (byte)0;
            tail[^1] = closed ? (byte)1 : (byte)0;
            var fields = Synthetic.Concat([0x83, 0, 3, 0, 0, 0], Text(number), Text("Issue for " + number),
                time, Text(closed ? "Author" : ""), tail);
            var header = new byte[6] { 1, 2, 0, 0, 0, 0 };
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), fields.Length);
            revisionBlocks.Add(Record(22, id, Synthetic.Concat(header, fields),
                Link(0x11, closed ? issuedTransmittalLink : openTransmittalLink, member)));
        }

        void AddDatabase(ushort kind, Guid id, uint units, params byte[][] links)
        {
            blocks.Add(OwnedDirectory(prefixedDirectory, id, units, new ObjectDirectoryRecord(kind, 0, units, id)));
            var bytes = new byte[units * 16];
            Record(kind, id, [1], links).CopyTo(bytes, 0);
            blocks.Add(Pool(bytes));
        }

        byte[] Record(ushort kind, Guid id, byte[] fields, params byte[][] links)
        {
            var prefix = new byte[39];
            classes[kind - 1].TryWriteBytes(prefix);
            BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(35), (uint)fields.Length);
            var relations = Synthetic.Concat([new byte[] { 1, 0 }, .. links, new byte[] { 0 }]);
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)relations.Length);
            byte[] body = Synthetic.Concat(prefix, fields, size, relations);
            return Synthetic.Block(PlaBlockMode.Raw, body, ver: 1, kind: kind, guid: id, extra: 1);
        }

        static byte[] Frame(int length, int version)
        {
            var bytes = new byte[length];
            new byte[] { 0x83, 0, 3, 0, 0, 0 }.CopyTo(bytes, version);
            return bytes;
        }

        static byte[] Text(string text)
        {
            byte[] frame = [1, 0, 0, 0, 0, 0];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(2), text.Length);
            return Synthetic.Concat(frame, Encoding.Unicode.GetBytes(text));
        }

        static byte[] Link(byte tag, Guid key, params Guid[] ids)
        {
            var prefix = new byte[tag == 1 ? 21 : 17];
            prefix[0] = tag;
            key.TryWriteBytes(prefix.AsSpan(1));
            if (tag == 1) BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(17), ids.Length);
            return Synthetic.Concat([prefix, .. ids.Select(id => id.ToByteArray())]);
        }
    }
}
