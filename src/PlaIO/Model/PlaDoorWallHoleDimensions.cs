namespace PlaIO.Model;

/// <summary>The saved GDL wall-hole dimensions of a Door instance, in metres.</summary>
/// <param name="Width">The value of the instance's wall-hole width parameter.</param>
/// <param name="Height">The value of the instance's wall-hole height parameter.</param>
public readonly record struct PlaDoorWallHoleDimensions(double Width, double Height);
