namespace PlaIO.Model;

/// <summary>The saved cross-section of a supported single-segment beam or column.</summary>
/// <param name="ProfileIndex">The saved project-local profile attribute index.</param>
/// <param name="Components">The material fills in saved drawing order.</param>
public sealed record PlaMemberProfile(int ProfileIndex, IReadOnlyList<PlaProfileComponent> Components);

/// <summary>A material fill in a saved cross-section.</summary>
/// <param name="BuildingMaterialIndex">The project-local building material index, or zero for an unassigned catalog fill.</param>
/// <param name="Function">The component role, or null if the saved role is unrecognized.</param>
/// <param name="CrossSectionArea">The fill area in square metres, including circular edges and excluding holes.</param>
/// <param name="Rings">The exterior followed by its holes, in saved vertex order without repeated closing vertices.</param>
/// <param name="Arcs">The circular edges of the rings, or null when all edges are straight.</param>
public record PlaProfileComponent(int BuildingMaterialIndex, string? Function, double CrossSectionArea,
    IReadOnlyList<IReadOnlyList<PlaProfilePoint>> Rings, IReadOnlyList<PlaProfileArc>? Arcs);
