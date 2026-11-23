namespace PlaIO.Model;

/// <summary>A point in the project XY plane, in metres.</summary>
/// <param name="X">The project X coordinate.</param>
/// <param name="Y">The project Y coordinate.</param>
public readonly record struct PlaPlanPoint(double X, double Y);

/// <summary>A circular edge of a slab outline.</summary>
/// <param name="RingIndex">The zero-based ring containing the edge.</param>
/// <param name="StartVertexIndex">The zero-based starting vertex. The edge ends at the next vertex,
/// wrapping to the first vertex if necessary.</param>
/// <param name="SweepRadians">The signed central angle in radians. Positive sweeps are counterclockwise.</param>
public readonly record struct PlaSlabArc(int RingIndex, int StartVertexIndex, double SweepRadians);

/// <summary>One connected part of an evaluated slab with vertical sides.</summary>
/// <param name="Rings">The counterclockwise exterior followed by clockwise holes, in project XY.</param>
public sealed record PlaSlabPrism(IReadOnlyList<IReadOnlyList<PlaPlanPoint>> Rings);

/// <summary>The saved slab outline and supported horizontal faces and solid volume.</summary>
/// <param name="Rings">The outer boundary followed by holes. Each ring closes implicitly.
/// The outer ring is counterclockwise and holes are clockwise when viewed from above.</param>
/// <param name="Bottom">The bottom elevation relative to project zero in metres.</param>
/// <param name="Top">The top elevation relative to project zero in metres.</param>
/// <param name="OutlineArea">The analytic area inside the saved outline after subtracting its holes, in square metres.</param>
/// <param name="Volume">The reconstructed solid volume in cubic metres, or null when unavailable.</param>
public sealed record PlaSlabGeometry(IReadOnlyList<IReadOnlyList<PlaPlanPoint>> Rings,
    double Bottom, double Top, double OutlineArea, double? Volume)
{
    /// <summary>Whether the saved outline and elevations describe the whole slab as a vertical extrusion.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUncutVerticalPrism { get; internal init; }

    /// <summary>The evaluated solid as connected vertical extrusions from <see cref="Bottom"/> to
    /// <see cref="Top"/>, or null when that reconstruction is unavailable.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<PlaSlabPrism>? VerticalPrisms { get; internal init; }

    /// <summary>The circular edges of the saved rings, or null when every edge is straight.</summary>
    public IReadOnlyList<PlaSlabArc>? Arcs { get; init; }

    /// <summary>The total length of the saved exterior and hole boundaries in metres, or null if unavailable.</summary>
    public double? FloorPlanPerimeter { get; init; }

    /// <summary>The total length of the saved hole boundaries in metres, or null if unavailable.</summary>
    public double? FloorPlanHolesPerimeter { get; init; }

    /// <summary>The top face area in square metres after saved holes and verified Opening cuts, or null.</summary>
    public double? TopAreaWithOpenings { get; init; }

    /// <summary>The bottom face area in square metres after saved holes and verified Opening cuts, or null.</summary>
    public double? BottomAreaWithOpenings { get; init; }
}
