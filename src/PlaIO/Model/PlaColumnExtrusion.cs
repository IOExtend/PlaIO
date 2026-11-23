namespace PlaIO.Model;

/// <summary>The evaluated solid of a supported vertical, single-segment column.</summary>
/// <param name="Origin">The column axis in project XY metres.</param>
/// <param name="Sections">The connected polygonal pieces of its horizontal section in XY metres relative to the axis.
/// Empty when the section is circular.</param>
/// <param name="LowerPlane">The lower face of the solid.</param>
/// <param name="UpperPlane">The upper face of the solid.</param>
/// <param name="BoundingBox">The tight bounds of this solid in project metres.</param>
/// <param name="Volume">The volume of the evaluated solid in cubic metres, analytic for a circular section.</param>
public sealed record PlaColumnExtrusion(PlaPlanPoint Origin, IReadOnlyList<PlaColumnExtrusionSection> Sections,
    PlaColumnEndPlane LowerPlane, PlaColumnEndPlane UpperPlane, PlaBoundingBox BoundingBox, double Volume)
{
    /// <summary>The separate horizontal spans left by an interior cut. Null means the solid spans both end planes.</summary>
    public IReadOnlyList<PlaColumnSpan>? Spans { get; internal init; }

    /// <summary>The analytic radius in metres for a circular section, or null for a polygonal section.</summary>
    public double? CircularRadius { get; internal init; }

    /// <summary>The polygonal area of a horizontal circular cap in square metres, as used in listings.</summary>
    public double? ScheduledCapArea { get; internal init; }

    /// <summary>The polygonal lateral surface area of the circular solid in square metres, when supported.</summary>
    public double? ScheduledLateralArea { get; internal init; }

    /// <summary>The Archicad listing volume of the circular solid in cubic metres, when supported.</summary>
    public double? ScheduledVolume => ScheduledCapArea * (UpperPlane.Elevation - LowerPlane.Elevation);
}

/// <summary>An uninterrupted full-section span of a column.</summary>
/// <param name="Bottom">The lower elevation relative to project zero in metres.</param>
/// <param name="Top">The upper elevation relative to project zero in metres.</param>
public readonly record struct PlaColumnSpan(double Bottom, double Top);

/// <summary>One connected column section with a counterclockwise exterior followed by clockwise holes.</summary>
/// <param name="Rings">The section boundaries, without repeated closing vertices.</param>
public sealed record PlaColumnExtrusionSection(IReadOnlyList<IReadOnlyList<PlaPlanPoint>> Rings);

/// <summary>An end plane whose elevation at local point (x, y) equals Elevation + SlopeX * x + SlopeY * y.</summary>
/// <param name="Elevation">The plane elevation at the column axis in project metres.</param>
/// <param name="SlopeX">The elevation change per metre along project X.</param>
/// <param name="SlopeY">The elevation change per metre along project Y.</param>
public readonly record struct PlaColumnEndPlane(double Elevation, double SlopeX, double SlopeY);
