namespace PlaIO.Model;

/// <summary>The base profile of an Opening in its extrusion plane.</summary>
/// <param name="Shape">Rectangle, Square, Circle, Ellipse or Custom.</param>
/// <param name="Width">The profile width in metres.</param>
/// <param name="Height">The profile height in metres.</param>
public sealed record PlaOpeningProfile(string Shape, double Width, double Height);
