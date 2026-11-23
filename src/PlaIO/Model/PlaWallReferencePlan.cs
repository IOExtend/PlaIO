namespace PlaIO.Model;

/// <summary>The saved reference line and transverse placement of a straight constant-width wall before junction cleanup.</summary>
/// <param name="Start">The reference line start in project XY coordinates.</param>
/// <param name="End">The reference line end in project XY coordinates.</param>
/// <param name="OutsideOffset">The signed distance from the reference line to the outside face in metres.
/// Positive distances point to the right when looking from start to end.</param>
/// <param name="InsideOffset">The signed distance from the reference line to the inside face in metres,
/// using the same direction as <paramref name="OutsideOffset"/>.</param>
public readonly record struct PlaWallReferencePlan(PlaPlanPoint Start, PlaPlanPoint End,
    double OutsideOffset, double InsideOffset);
