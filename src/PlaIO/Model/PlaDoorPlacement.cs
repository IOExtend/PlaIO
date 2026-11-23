namespace PlaIO.Model;

/// <summary>The placement of a Door on the reference line of its straight host wall.</summary>
/// <param name="ReferencePoint">The point on the wall reference line in project XY metres.</param>
/// <param name="DistanceAlongWall">The distance in metres from the directed reference line start.</param>
/// <param name="SillElevationToProjectZero">The nominal sill elevation in metres relative to project zero, if available.</param>
/// <param name="SillElevationToHostStory">The nominal sill elevation in metres relative to the wall's home story, if available.</param>
public readonly record struct PlaDoorPlacement(PlaPlanPoint ReferencePoint, double DistanceAlongWall,
    double? SillElevationToProjectZero, double? SillElevationToHostStory);
