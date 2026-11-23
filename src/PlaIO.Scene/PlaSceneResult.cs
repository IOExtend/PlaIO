using IOScene;

namespace PlaIO.Scene;

/// <summary>The scene made from one Archicad container by <see cref="PlaSceneReader"/>, and what the read reported.</summary>
public sealed class PlaSceneResult
{
    /// <summary>The converted model and its available scene data.</summary>
    public required SceneDocument Document { get; init; }

    /// <summary>Conversion diagnostics for empty scenes or incomplete mesh and grid coverage.</summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; init; } = [];
}
