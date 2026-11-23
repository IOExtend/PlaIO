namespace PlaIO.Model;

/// <summary>The directed reference axis saved with a straight or horizontally curved beam.</summary>
/// <param name="Start">The start point in project XY coordinates.</param>
/// <param name="End">The end point in project XY coordinates.</param>
/// <param name="StartElevationToProjectZero">The start elevation in metres above project zero.</param>
/// <param name="EndElevationToProjectZero">The end elevation in metres above project zero.</param>
/// <param name="PlanSweepRadians">Zero for a straight axis or the signed circular sweep in plan.
/// Positive values turn counterclockwise from start to end.</param>
public readonly record struct PlaBeamReferenceAxis(PlaPlanPoint Start, PlaPlanPoint End,
    double StartElevationToProjectZero, double EndElevationToProjectZero, double PlanSweepRadians)
{
    private double Chord => double.Hypot(End.X - Start.X, End.Y - Start.Y);
    private double Rise => EndElevationToProjectZero - StartElevationToProjectZero;

    /// <summary>The length in metres along the saved axis before junction cleanup.</summary>
    public double Length => PlanSweepRadians == 0 ? double.Hypot(Chord, Rise)
        : Chord * (PlanSweepRadians / (2 * Math.Sin(PlanSweepRadians / 2)));

    /// <summary>The signed inclination in radians, positive when the axis rises toward its end.</summary>
    public double InclinationRadians => Math.Atan2(Rise, Chord);
}
