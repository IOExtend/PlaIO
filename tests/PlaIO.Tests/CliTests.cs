using System.Text.Json;
using PlaIO.CommandLine;
using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "plaio-tests-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, byte[] content)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = Cli.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static readonly ObjectDirectoryRecord[] DirectoryRecords = Synthetic.Extents(80, new Random(12));

    private static byte[] SampleFdb() => Synthetic.Fdb(
        Synthetic.Block(PlaBlockMode.Raw, new byte[20]),
        Synthetic.Block(PlaBlockMode.ZeroMask, Synthetic.ZeroMask(new byte[16]), unpacked: 16, guid: Guid.NewGuid(), extra: 2),
        Synthetic.Block(PlaBlockMode.Lz, new byte[10], tag: "DY", unpacked: 30, flagsOverride: 0x8290),
        Synthetic.Block(PlaBlockMode.Raw, Synthetic.DirectoryPayload(2, DirectoryRecords)));

    [Fact]
    public void UsageErrorsExitWithOne()
    {
        string file = Write("a.pla", SampleFdb());
        Assert.Equal(1, Run().Code);
        Assert.Equal(1, Run("export", file).Code);
        Assert.Equal(1, Run("--frobnicate").Code);
        Assert.Equal(1, Run("info").Code);
        Assert.Equal(1, Run("info", file, "--out", "x.json").Code);
        Assert.Equal(1, Run("blocks", file, "--verbose").Code);
        Assert.Equal(1, Run("info", file, file).Code);
        Assert.Equal(1, Run("directory", file, "--out").Code);
        Assert.Equal(1, Run("info", "").Code);
        Assert.Equal(1, Run("directory", file, "--out", "").Code);
        var (_, _, err) = Run("export", file);
        Assert.Contains("export", err);
    }

    [Fact]
    public void HelpPrintsUsageAndVersionPrintsTheToolVersion()
    {
        var (code, output, _) = Run("--help");
        Assert.Equal(0, code);
        Assert.Contains("Exit codes", output);
        Assert.Contains("plaio directory <file>", output);
        (code, output, _) = Run("--version");
        Assert.Equal(0, code);
        Assert.Matches(@"^plaio \d+\.\d+\.\d+$", output.Trim());
    }

    [Fact]
    public void InputsThatCannotBeUsedExitWithTwo()
    {
        Assert.Equal(2, Run("info", Path.Combine(_folder, "missing.pla")).Code);
        var (code, _, err) = Run("info", Write("x.pla", "not a container"u8.ToArray()));
        Assert.Equal(2, code);
        Assert.Contains("not a recognized", err);
        string bimx = Write("b.bimx", Synthetic.Bimx(Synthetic.SimplePng(), [1, 2]));
        Assert.Equal(2, Run("blocks", bimx).Code);
        Assert.Equal(2, Run("directory", bimx).Code);
        Assert.Equal(2, Run("stories", bimx).Code);
        Assert.Equal(2, Run("layers", bimx).Code);
        Assert.Equal(2, Run("materials", bimx).Code);
        Assert.Equal(2, Run("profiles", bimx).Code);
        Assert.Equal(2, Run("classifications", bimx).Code);
        Assert.Equal(2, Run("properties", bimx).Code);
        Assert.Equal(2, Run("layouts", bimx).Code);
        Assert.Equal(2, Run("info", Write("cut.pla", Synthetic.Header()[..20])).Code);
        Assert.Equal(2, Run("info", Write("bad.bimx", Synthetic.Bimx([1, 2, 3], []))).Code);
        Assert.Equal(2, Run("info", "a\0b").Code);
    }

    [Fact]
    public void ProfilesWritesProjectProfileAttributes()
    {
        Guid id = Guid.NewGuid();
        var (code, output, error) = Run("profiles", Write("profiles.pln", ElementReaderTests.ProfileCatalogFile(id)));
        Assert.Equal(0, code);
        Assert.Empty(error);
        using var json = JsonDocument.Parse(output);
        var profile = Assert.Single(json.RootElement.GetProperty("profiles").EnumerateArray());
        Assert.Equal(id, profile.GetProperty("guid").GetGuid());
        Assert.Equal(3, profile.GetProperty("index").GetInt32());
        Assert.Equal("Профиль", profile.GetProperty("name").GetString());
        Assert.True(profile.GetProperty("hasCoreSkin").GetBoolean());
    }

    [Fact]
    public void ClassificationsWritesCatalogAndElementsReferenceItsItems()
    {
        var fixture = ElementReaderTests.ClassificationFixture();
        string file = Write("classifications.pln", fixture.File);
        var (code, output, error) = Run("classifications", file);
        Assert.Equal(0, code);
        Assert.Empty(error);
        using var catalog = JsonDocument.Parse(output);
        var system = catalog.RootElement.GetProperty("classificationSystems")[0];
        Assert.Equal(fixture.First, system.GetProperty("guid").GetGuid());
        Assert.Equal("2024-02-29", system.GetProperty("date").GetString());
        Assert.Equal(fixture.Branch, system.GetProperty("items")[1].GetProperty("parentGuid").GetGuid());

        (code, output, error) = Run("elements", file);
        Assert.Equal(0, code);
        Assert.Empty(error);
        using var elements = JsonDocument.Parse(output);
        var element = Assert.Single(elements.RootElement.GetProperty("elements").EnumerateArray(),
            value => value.GetProperty("guid").GetGuid() == fixture.Classified);
        var classification = element.GetProperty("classifications")[0];
        Assert.Equal(fixture.First, classification.GetProperty("systemGuid").GetGuid());
        Assert.Equal(fixture.Leaf, classification.GetProperty("itemGuid").GetGuid());
    }

    [Fact]
    public void PropertiesWritesGroupsAndTypedDefinitionValues()
    {
        var fixture = ElementReaderTests.PropertyCatalogFixture(withOverride: true);
        string file = Write("properties.pln", fixture.File);
        var (code, output, error) = Run("properties", file);
        Assert.Equal(0, code);
        Assert.Empty(error);
        using var json = JsonDocument.Parse(output);
        var group = Assert.Single(json.RootElement.GetProperty("groups").EnumerateArray());
        var definition = Assert.Single(json.RootElement.GetProperty("definitions").EnumerateArray(),
            value => value.GetProperty("guid").GetGuid() == fixture.Length);
        Assert.Equal(fixture.Group, group.GetProperty("guid").GetGuid());
        Assert.Equal(fixture.Group, definition.GetProperty("groupGuid").GetGuid());
        Assert.Equal("length", definition.GetProperty("dataType").GetString());
        Assert.Equal(0.125, definition.GetProperty("defaultValue").GetProperty("value").GetDouble());

        (code, output, error) = Run("elements", file);
        Assert.Equal(0, code);
        Assert.Empty(error);
        using var elements = JsonDocument.Parse(output);
        var element = Assert.Single(elements.RootElement.GetProperty("elements").EnumerateArray());
        var saved = Assert.Single(element.GetProperty("propertyOverrides").EnumerateArray(),
            value => value.GetProperty("definitionGuid").GetGuid() == fixture.Length);
        Assert.Equal(0.5, saved.GetProperty("value").GetProperty("value").GetDouble());
    }

    [Fact]
    public void LayoutsWritesTheBookAndPlacedEntries()
    {
        var fixture = ElementReaderTests.LayoutBookFixture();
        var (code, output, error) = Run("layouts", Write("book.pln", fixture.File));
        Assert.Equal(0, code);
        Assert.Empty(error);
        using var json = JsonDocument.Parse(output);
        var book = json.RootElement.GetProperty("layoutBook");
        Assert.Equal(fixture.Book, book.GetProperty("guid").GetGuid());
        var drawing = book.GetProperty("items")[2];
        Assert.Equal("DrawingItem", drawing.GetProperty("type").GetString());
        Assert.Equal(fixture.Items[1].Guid, drawing.GetProperty("parentGuid").GetGuid());
        Assert.Equal(fixture.Items[2].SourceGuid, drawing.GetProperty("sourceGuid").GetGuid());
        var layout = book.GetProperty("items")[1];
        Assert.Equal(fixture.Items[1].DatabaseGuid, layout.GetProperty("databaseGuid").GetGuid());
        Assert.Equal(fixture.Items[1].MasterLayoutGuid, layout.GetProperty("masterLayoutGuid").GetGuid());
        Assert.Equal(0.420, layout.GetProperty("paperSize").GetProperty("width").GetDouble());
    }

    [Fact]
    public void LayoutsWritesIssuedAndOpenRevisionData()
    {
        var fixture = ElementReaderTests.LayoutBookFixture(revisions: "mixed");
        var (code, output, error) = Run("layouts", Write("revisions.pln", fixture.File));
        Assert.Equal(0, code);
        Assert.Empty(error);
        using var json = JsonDocument.Parse(output);
        var revision = json.RootElement.GetProperty("layoutBook").GetProperty("items")[1].GetProperty("revision");
        Assert.True(revision.GetProperty("hasIssuedRevision").GetBoolean());
        Assert.True(revision.GetProperty("hasActualRevision").GetBoolean());
        Assert.Equal("A", revision.GetProperty("issuedRevisions")[1].GetProperty("id").GetString());
        Assert.Equal("Draft", revision.GetProperty("openTransmittal").GetProperty("id").GetString());
        Assert.Equal("01", revision.GetProperty("currentRevisionId").GetString());
        Assert.Equal(JsonValueKind.Null, revision.GetProperty("currentFinalRevisionId").ValueKind);
    }

    [Fact]
    public void InfoOnFdbPrintsHeaderFieldsAndBlockCounts()
    {
        var (code, output, _) = Run("info", Write("a.pla", SampleFdb()));
        Assert.Equal(0, code);
        Assert.Contains("kind: FDB", output);
        Assert.Contains("layoutVersion: 0x0010", output);
        Assert.Contains("blocks: 4", output);
        Assert.Contains("mode Raw: 2", output);
        Assert.Contains("mode ZeroMask: 1", output);
        Assert.Contains("mode Lz: 1", output);
        Assert.Contains("tag DY: 1", output);
        Assert.Contains("flag 0x10: 1", output);
    }

    [Fact]
    public void InfoOnBimxPrintsTheCoverBounds()
    {
        var png = Synthetic.SimplePng();
        var (code, output, _) = Run("info", Write("b.bimx", Synthetic.Bimx(png, [1, 2])));
        Assert.Equal(0, code);
        Assert.Contains("kind: BIMx", output);
        Assert.Contains($"coverLength: {png.Length}", output);
        Assert.Contains("coverOffset: 0x4", output);
    }

    [Fact]
    public void BlocksListsEveryBlock()
    {
        var (code, output, _) = Run("blocks", Write("a.pla", SampleFdb()));
        Assert.Equal(0, code);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(5, lines.Length);
        Assert.StartsWith("offset\ttag\t", lines[0]);
        Assert.StartsWith("0x20\tBL\t0x01020304\t0xfdea\t0\t", lines[1]);
        Assert.All(lines, l => Assert.Equal(13, l.Split('\t').Length));
        Assert.Contains("\tLz\t", lines[3]);
    }

    [Fact]
    public void DirectoryWritesSummaryAndJson()
    {
        string json = Path.Combine(_folder, "out.json");
        var (code, output, _) = Run("directory", Write("a.pla", SampleFdb()), "--out", json);
        Assert.Equal(0, code);
        Assert.Contains("\t2\t2\t80\t79\t0\t0", output);

        using var document = JsonDocument.Parse(File.ReadAllBytes(json));
        var root = document.RootElement;
        Assert.Equal(new FileInfo(Path.Combine(_folder, "a.pla")).Length, root.GetProperty("length").GetInt64());
        var directory = Assert.Single(root.GetProperty("directories").EnumerateArray().ToList());
        Assert.Equal(2, directory.GetProperty("recordOffset").GetInt32());
        Assert.Equal(2, directory.GetProperty("phase").GetInt32());
        Assert.Equal(80, directory.GetProperty("recordCount").GetInt32());
        Assert.Equal(79, directory.GetProperty("abuttingPairs").GetInt32());
        Assert.Equal(0, directory.GetProperty("overlappingPairs").GetInt32());
        Assert.Equal(0, directory.GetProperty("duplicatePositions").GetInt32());
        Assert.True(directory.GetProperty("payloadOffset").GetInt64() > directory.GetProperty("blockOffset").GetInt64());
        var records = directory.GetProperty("records").EnumerateArray().Select(r => new ObjectDirectoryRecord(
            r.GetProperty("class").GetUInt16(), r.GetProperty("pos").GetUInt64(), r.GetProperty("count").GetUInt32(),
            Guid.Parse(r.GetProperty("guid").GetString()!))).ToArray();
        Assert.Equal(DirectoryRecords, records);
    }

    [Fact]
    public void DirectoryWithoutOutPrintsOnlyTheSummary()
    {
        var (code, output, _) = Run("directory", Write("a.pla", SampleFdb()));
        Assert.Equal(0, code);
        Assert.Equal(2, output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void ElementsPrintsTheReadOnlyModelAsJson()
    {
        Guid id = Guid.NewGuid();
        string input = Write("model.pln", ElementReaderTests.WallFile(id));
        var (code, output, error) = Run("elements", input);
        Assert.Equal(0, code);
        Assert.Empty(error);
        using var json = JsonDocument.Parse(output);
        var element = Assert.Single(json.RootElement.GetProperty("elements").EnumerateArray());
        Assert.Equal(id, element.GetProperty("guid").GetGuid());
        Assert.Equal("Wall", element.GetProperty("type").GetString());
        Assert.Equal("Wall-1", element.GetProperty("elementId").GetString());
        Assert.Equal(0.25, element.GetProperty("wallThickness").GetDouble());
        Assert.Equal(5, element.GetProperty("wallReferenceLineChordLength").GetDouble());
        Assert.Equal(JsonValueKind.Null, element.GetProperty("topLinkedStoryIndex").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("bottomElevationToProjectZero").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("topElevationToProjectZero").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("bottomElevationToHomeStory").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("topElevationToHomeStory").ValueKind);
    }

    [Fact]
    public void OutputThatCannotBeReplacedExitsWithTwoAndLeavesNoTemporaryFile()
    {
        string target = Directory.CreateDirectory(Path.Combine(_folder, "taken")).FullName;
        Assert.Equal(2, Run("directory", Write("a.pla", SampleFdb()), "--out", target).Code);
        Assert.Empty(Directory.EnumerateFiles(_folder, "*.tmp"));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void AnExistingFileNamedLikeATemporaryFileIsLeftAlone()
    {
        string json = Path.Combine(_folder, "out.json");
        string bystander = Write("out.json.tmp", "keep"u8.ToArray());
        Assert.Equal(0, Run("directory", Write("a.pla", SampleFdb()), "--out", json).Code);
        Assert.Equal("keep"u8.ToArray(), File.ReadAllBytes(bystander));
        Assert.Single(Directory.EnumerateFiles(_folder, "*.tmp"));
        Assert.True(File.Exists(json));
    }

    [Fact]
    public void OutputReplacesAnExistingFile()
    {
        string json = Write("old.json", "stale"u8.ToArray());
        Assert.Equal(0, Run("directory", Write("a.pla", SampleFdb()), "--out", json).Code);
        using var document = JsonDocument.Parse(File.ReadAllBytes(json));
        Assert.Single(document.RootElement.GetProperty("directories").EnumerateArray());
    }

    [Fact]
    public void OutputThatCannotBeWrittenExitsWithTwoAndNamesTheOutput()
    {
        string json = Path.Combine(_folder, "no-such-folder", "out.json");
        var (code, _, err) = Run("directory", Write("a.pla", SampleFdb()), "--out", json);
        Assert.Equal(2, code);
        Assert.Contains($"'{json}' cannot be written", err);
    }
}
