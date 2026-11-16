using IOScene;
using PlaIO.Scene;
using Xunit;

namespace PlaIO.Tests;

/// <summary>The tests of <see cref="PlaSceneReader"/> on synthetic containers. The identity tests are golden: the model
/// key derivation is what the consumers of a scene store together with the source ids of the elements, so a change of
/// these literals is a breaking release of PlaIO.Scene.</summary>
public class PlaSceneReaderTests
{
    [Fact]
    public void OneModelWithTheFileNameAsItsKey()
    {
        using var scope = TempContainer("sample.pla");

        var result = PlaSceneReader.Read(scope.Path);
        var document = result.Document;
        var model = Assert.Single(document.Models);

        // The frozen identity derivation: the file name with its extension, without its directory.
        Assert.Equal("sample.pla", model.Key);
        Assert.Equal("sample.pla", model.Name);
        Assert.Equal("pla", model.SourceFormat);
        Assert.Equal(1, model.Id);
        Assert.StartsWith("PlaIO.Scene ", model.Producer);
        Assert.Equal("sample.pla", document.Title);

        // The container reader decodes no elements yet, so the scene is geometry-free and property-free.
        Assert.Empty(document.Elements);
        Assert.Empty(document.Geometries);
        Assert.Empty(document.Instances);
        Assert.Empty(document.PropertySets);
        Assert.Empty(document.Validate());

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(SceneSeverity.Info, diagnostic.Severity);
        Assert.Equal("container-only", diagnostic.Code);
    }

    [Fact]
    public void PlnExtensionIsTheSourceFormat()
    {
        using var scope = TempContainer("project.pln");

        var model = Assert.Single(PlaSceneReader.Read(scope.Path).Document.Models);

        Assert.Equal("project.pln", model.Key);
        Assert.Equal("pln", model.SourceFormat);
    }

    [Fact]
    public void AnotherExtensionWritesNoSourceFormat()
    {
        using var scope = TempContainer("container.dat");

        var model = Assert.Single(PlaSceneReader.Read(scope.Path).Document.Models);

        Assert.Equal("container.dat", model.Key);
        Assert.Null(model.SourceFormat);
    }

    [Fact]
    public void ModelKeyOptionReplacesTheFileName()
    {
        using var scope = TempContainer("sample.pla");

        var model = Assert.Single(PlaSceneReader.Read(scope.Path, new PlaSceneOptions { ModelKey = "campus" }).Document.Models);

        Assert.Equal("campus", model.Key);
        Assert.Equal("campus", model.Name);
    }

    [Fact]
    public void AnEmptyPathThrows()
    {
        Assert.Throws<ArgumentException>(() => PlaSceneReader.Read(""));
    }

    [Fact]
    public void AnEmptyModelKeyThrows()
    {
        using var scope = TempContainer("sample.pla");

        Assert.Throws<ArgumentException>(() => PlaSceneReader.Read(scope.Path, new PlaSceneOptions { ModelKey = "" }));
    }

    /// <summary>A synthetic container written to a file of the given name in its own temporary directory.</summary>
    private static TempScope TempContainer(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"plaio-scene-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Raw, [0x01, 0x02, 0x03])));
        return new TempScope(path, directory);
    }

    private sealed class TempScope(string path, string directory) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
