namespace PlaIO.Model;

/// <summary>A verified straight wall solid formed by sweeping vertical sections between two points in plan.</summary>
/// <param name="Start">The start of the sweep in project XY metres.</param>
/// <param name="End">The end of the sweep in project XY metres.</param>
/// <param name="BaseElevation">The project elevation of the section origin in metres.</param>
/// <param name="Sections">The connected regions of the swept section, which may include openings.</param>
/// <param name="Volume">The volume of the wall in cubic metres.</param>
public sealed record PlaWallExtrusion(PlaPlanPoint Start, PlaPlanPoint End, double BaseElevation,
    IReadOnlyList<PlaWallExtrusionSection> Sections, double Volume);

/// <summary>The boundaries of one connected wall cross-section in local U/Z coordinates.</summary>
/// <param name="Rings">The counterclockwise exterior followed by clockwise holes. U points to the right
/// when looking from the sweep start to its end, and Z points up.</param>
public sealed record PlaWallExtrusionSection(IReadOnlyList<IReadOnlyList<PlaProfilePoint>> Rings);
