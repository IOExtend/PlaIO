using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    // Near-coincident saved outlines can differ by micrometres even where the model joins their edges.
    private const double WallCapPlanClearance = 1e-6;

    private readonly record struct WallCapObstacle((double Start, double End) Span, Elevations? Height = null,
        IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? Rings = null, bool HasVerifiedExtent = true)
    {
        public bool Reaches(double elevation) => Height is not { } height ||
            elevation >= height.Bottom - JunctionTolerance && elevation <= height.Top + JunctionTolerance;
    }

    private static (bool Bottom, bool Top) WallCapsWithMaterial(WallStrip strip, Elevations bounds, List<WallCapObstacle> blocked)
    {
        if (UnblockedWallSpans(strip, blocked.Select(obstacle => obstacle.Span)).Any() || HasWallCapPatch(strip, blocked))
            return (true, true);
        // A cutter can reach only one plane. Each plane needs its own surviving patch.
        return (HasWallCapPatch(strip, blocked.Where(obstacle => obstacle.Reaches(bounds.Bottom))),
            HasWallCapPatch(strip, blocked.Where(obstacle => obstacle.Reaches(bounds.Top))));
    }

    private static bool HasWallCapPatch(WallStrip strip, IEnumerable<WallCapObstacle> blocked)
    {
        var obstacles = blocked.ToArray();
        var slabs = obstacles.Select(obstacle => obstacle.Rings).OfType<IReadOnlyList<IReadOnlyList<PlaPlanPoint>>>().ToArray();
        return UnblockedWallSpans(strip, obstacles.Where(obstacle => obstacle.Rings is null).Select(obstacle => obstacle.Span))
            .Any(span => HasWallCapPatch(strip, span, slabs));
    }

    private static IEnumerable<(double Start, double End)> UnblockedWallSpans(WallStrip strip,
        IEnumerable<(double Start, double End)> blocked)
    {
        double covered = strip.Start;
        foreach (var (from, to) in blocked.OrderBy(span => span.Start))
        {
            double stop = Math.Min(from, strip.End);
            if (stop > covered + JunctionTolerance) yield return (covered, stop);
            covered = Math.Max(covered, to);
            if (covered >= strip.End) yield break;
        }
        if (covered < strip.End - JunctionTolerance) yield return (covered, strip.End);
    }

    private static bool HasWallCapPatch(WallStrip strip, (double Start, double End) span,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<PlaPlanPoint>>> slabs)
    {
        const double gap = WallCapPlanClearance;
        var box = (strip with { Start = span.Start, End = span.End }).Footprint;
        var interior = (X0: box.X0 + gap, Y0: box.Y0 + gap, X1: box.X1 - gap, Y1: box.Y1 - gap);
        return interior.X0 < interior.X1 && interior.Y0 < interior.Y1 &&
            WallPlanHasGap(interior, slabs, center => slabs.All(slab => SlabMissesPatch(slab, center))) is true;
    }

    private static bool SlabMissesPatch(IReadOnlyList<IReadOnlyList<PlaPlanPoint>> slab, PlaPlanPoint center)
    {
        if (ContainsPoint(slab[0], center) && !slab.Skip(1).Any(hole => ContainsPoint(hole, center))) return false;
        const double gap = WallCapPlanClearance;
        foreach (var ring in slab)
        {
            var previous = ring[^1];
            foreach (var point in ring)
            {
                double low = 0, high = 1;
                if (ClipSegment(previous.X, point.X, center.X - gap, center.X + gap, ref low, ref high) &&
                    ClipSegment(previous.Y, point.Y, center.Y - gap, center.Y + gap, ref low, ref high))
                    return false;
                previous = point;
            }
        }
        return true;
    }
}
