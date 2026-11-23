namespace PlaIO.Model;

/// <summary>The plan profile and evaluated cut geometry of a supported horizontal Opening.</summary>
/// <param name="Origin">The profile center in project XY coordinates for a parametric shape, or the origin
/// of the saved polygon for a custom shape.</param>
/// <param name="Rings">The straight profile rings in project XY, with implicit closure. Circular and elliptical
/// profiles use the Opening's width and height around <paramref name="Origin"/>.</param>
/// <param name="Cuts">The verified cut intervals by slab, or null if the cut geometry is unavailable.</param>
/// <param name="CutVolume">The combined cut volume in cubic metres, or null if unavailable.</param>
public sealed record PlaOpeningGeometry(PlaPlanPoint Origin, IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? Rings,
    IReadOnlyList<PlaOpeningCut>? Cuts = null, double? CutVolume = null);

/// <summary>A vertical interval where a slab is cut across the Opening profile.</summary>
/// <param name="SlabGuid">The GUID of the cut slab.</param>
/// <param name="Bottom">The bottom elevation in metres relative to project zero.</param>
/// <param name="Top">The top elevation in metres relative to project zero.</param>
public sealed record PlaOpeningCut(Guid SlabGuid, double Bottom, double Top);
