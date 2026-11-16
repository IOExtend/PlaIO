using System.Reflection;
using IOScene;

namespace PlaIO.Scene;

/// <summary>
/// Turns an Archicad container (a .pla or .pln file) into a scene (<see cref="SceneDocument"/>): one model for the file,
/// without elements, properties or geometry while the container reader does not decode them yet.
/// </summary>
/// <remarks>
/// <para>The model's <c>Key</c> is the <see cref="PlaSceneOptions.ModelKey"/>, else the file name without its directory.
/// The derivation is frozen: the key never switches to a better identifier, and the elements this adapter will write get
/// their persisted identity from it. <c>SourceFormat</c> is the lower-case file extension (<c>pla</c> or <c>pln</c>),
/// <c>Producer</c> this assembly's name and version.</para>
/// <para>The scene holds no guesses: what the container reader cannot decode yet (element contents, class names, the
/// meaning of the object directory records) is not written, and the <c>container-only</c> diagnostic says so.</para>
/// </remarks>
public static class PlaSceneReader
{
    private const int ModelId = 1;

    /// <summary>Reads the container at <paramref name="path"/> and turns it into a scene.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty, or <see cref="PlaSceneOptions.ModelKey"/> is empty.</exception>
    public static PlaSceneResult Read(string path, PlaSceneOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string key = options?.ModelKey ?? Path.GetFileName(path);
        if (key.Length == 0) throw new ArgumentException("The model key is empty.", nameof(options));
        return Map(path, key);
    }

    private static PlaSceneResult Map(string path, string key)
    {
        using var document = PlaDocument.Open(path);
        var scene = new SceneDocument { Title = key };
        scene.Models.Add(new SceneModel
        {
            Id = ModelId,
            Key = key,
            Name = key,
            SourceFormat = SourceFormat(path),
            Producer = Producer,
        });
        return new PlaSceneResult
        {
            Document = scene,
            Diagnostics =
            [
                new(SceneSeverity.Info, "container-only",
                    "The reader decodes the container only: elements, properties and geometry are not read yet, so the scene has no elements."),
            ],
        };
    }

    private static string? SourceFormat(string path)
    {
        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension is "pla" or "pln" ? extension : null;
    }

    /// <summary>"PlaIO.Scene" and the version of this assembly without the source revision.</summary>
    private static string Producer { get; } = "PlaIO.Scene " + (typeof(PlaSceneReader).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(PlaSceneReader).Assembly.GetName().Version?.ToString(3));
}
