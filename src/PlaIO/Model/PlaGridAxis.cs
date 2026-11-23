using System.Text.Json.Serialization;

namespace PlaIO.Model;

/// <summary>A saved straight or circular grid axis in project plan coordinates.</summary>
/// <param name="Guid">The GUID of the placed grid object.</param>
/// <param name="Label">The saved label of the start marker.</param>
/// <param name="Start">The start point in project XY coordinates, in metres.</param>
/// <param name="End">The end point in project XY coordinates, in metres.</param>
/// <param name="HomeStoryIndex">The signed home story index when available.</param>
public sealed record PlaGridAxis(Guid Guid, string Label, PlaPlanPoint Start, PlaPlanPoint End, int? HomeStoryIndex)
{
    /// <summary>The saved elevation offset from the home story in metres, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ElevationToHomeStory { get; internal init; }

    /// <summary>The elevation relative to project zero in metres, when the home story elevation is known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ElevationToProjectZero { get; internal init; }

    /// <summary>The circular geometry, when the axis follows an arc. Start and end follow its counterclockwise order.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlaGridArc? Arc { get; internal init; }

    /// <summary>The configured floor plan segment length in metres when the line is shown in segments.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FloorPlanSegmentLength { get; internal init; }

    /// <summary>Whether the saved floor plan display hides the axis line.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsFloorPlanLineHidden { get; internal init; }

    /// <summary>Samples the full reference axis as a polyline, with a maximum arc-to-chord error in metres.</summary>
    /// <param name="maxError">The positive, finite maximum error in metres.</param>
    /// <returns>Points in order, or null if more than 65,536 points are needed or a point is not representable.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxError"/> is nonfinite or not positive.</exception>
    public IReadOnlyList<PlaPlanPoint>? Tessellate(double maxError)
    {
        if (!double.IsFinite(maxError) || maxError <= 0) throw new ArgumentOutOfRangeException(nameof(maxError));
        if (Arc is not { } arc) return [Start, End];

        double maxStep = 4 * Math.Asin(Math.Sqrt(Math.Min(0.5, maxError / arc.Radius / 2)));
        double count = Math.Ceiling(arc.SweepRadians / Math.Min(Math.PI / 2, maxStep));
        if (!(count >= 1 && count < 65_536)) return null;

        double startAngle = Math.Atan2(Start.Y - arc.Center.Y, Start.X - arc.Center.X);
        var points = new PlaPlanPoint[(int)count + 1];
        points[0] = Start;
        for (int i = 1; i < points.Length - 1; i++)
        {
            var (sin, cos) = Math.SinCos(startAngle + arc.SweepRadians * i / count);
            var point = new PlaPlanPoint(arc.Center.X + arc.Radius * cos, arc.Center.Y + arc.Radius * sin);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
            points[i] = point;
        }
        points[^1] = End;
        return Array.AsReadOnly(points);
    }
}

/// <summary>The analytic geometry of a circular grid axis.</summary>
/// <param name="Center">The center in project XY coordinates, in metres.</param>
/// <param name="Radius">The radius in metres.</param>
/// <param name="SweepRadians">The positive counterclockwise sweep in radians.</param>
public readonly record struct PlaGridArc(PlaPlanPoint Center, double Radius, double SweepRadians);
