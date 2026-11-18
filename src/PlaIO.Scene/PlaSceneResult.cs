using IOScene;

namespace PlaIO.Scene;

/// <summary>The scene made from one Archicad container by <see cref="PlaSceneReader"/>, and what the read reported.</summary>
public sealed class PlaSceneResult
{
    /// <summary>The scene: one model, without elements while the reader decodes only the container.</summary>
    public required SceneDocument Document { get; init; }

    /// <summary>What the scene does not hold yet: <c>container-only</c> while elements, properties and geometry are not
    /// read. Empty when the container was read whole.</summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; init; } = [];
}
