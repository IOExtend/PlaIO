namespace PlaIO.Model;

/// <summary>The saved nominal dimensions of a Door.</summary>
/// <param name="Width">The width in metres.</param>
/// <param name="Height">The height in metres.</param>
public readonly record struct PlaDoorDimensions(double Width, double Height)
{
    /// <summary>The signed sill height in metres relative to its saved anchor, or null if unavailable.</summary>
    public double? SillHeightFromAnchor { get; init; }
}
