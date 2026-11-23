namespace PlaIO.Model;

/// <summary>The saved cross-section of a placed complex-profile wall.</summary>
/// <param name="ProfileIndex">The project-local profile attribute index.</param>
/// <param name="Components">The material fills in saved drawing order.</param>
public sealed record PlaWallProfile(int ProfileIndex, IReadOnlyList<PlaWallProfileComponent> Components);

/// <summary>A material fill in a saved wall cross-section.</summary>
/// <param name="BuildingMaterialIndex">The project-local building material index.</param>
/// <param name="Function">The component role, or null if the saved role is unrecognized.</param>
/// <param name="CrossSectionArea">The fill area in square metres, including circular edges and excluding holes.</param>
/// <param name="Rings">The exterior followed by its holes, in saved vertex order without repeated closing vertices.</param>
/// <param name="Arcs">The circular edges of the rings, or null when all edges are straight.</param>
public sealed record PlaWallProfileComponent(int BuildingMaterialIndex, string? Function, double CrossSectionArea,
    IReadOnlyList<IReadOnlyList<PlaProfilePoint>> Rings, IReadOnlyList<PlaProfileArc>? Arcs)
    : PlaProfileComponent(BuildingMaterialIndex, Function, CrossSectionArea, Rings, Arcs);

/// <summary>A point in the local coordinates of a saved profile, in metres.</summary>
/// <param name="U">The first saved coordinate in the profile plane.</param>
/// <param name="Z">The second saved coordinate in the profile plane.</param>
public readonly record struct PlaProfilePoint(double U, double Z);

/// <summary>A circular edge from the given vertex to its successor, wrapping within the ring.</summary>
/// <param name="RingIndex">The zero-based ring index.</param>
/// <param name="StartVertexIndex">The zero-based index of the first vertex in that ring.</param>
/// <param name="SweepRadians">The signed central angle in radians. Positive is counterclockwise in the profile plane.</param>
public readonly record struct PlaProfileArc(int RingIndex, int StartVertexIndex, double SweepRadians);
