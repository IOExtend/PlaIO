namespace PlaIO.Model;

/// <summary>An axis-aligned 3D bounding box in metres in the project coordinate system.</summary>
/// <param name="XMin">The minimum X coordinate.</param>
/// <param name="YMin">The minimum Y coordinate.</param>
/// <param name="ZMin">The minimum Z coordinate relative to project zero.</param>
/// <param name="XMax">The maximum X coordinate.</param>
/// <param name="YMax">The maximum Y coordinate.</param>
/// <param name="ZMax">The maximum Z coordinate relative to project zero.</param>
public sealed record PlaBoundingBox(double XMin, double YMin, double ZMin, double XMax, double YMax, double ZMax);
