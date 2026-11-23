using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static PlaOpeningGeometry? ReadPlacedOpening(ReadOnlySpan<byte> settings, PlaOpeningProfile profile, PlaPlanPoint slabOrigin,
        (double X0, double Y0, double X1, double Y1) footprint, out double area)
    {
        area = 0;
        var localOrigin = SlabOpeningProfileOrigin(settings, profile);
        var origin = new PlaPlanPoint(slabOrigin.X + localOrigin.X, slabOrigin.Y + localOrigin.Y);
        var plan = TranslateOpeningPlan(footprint, slabOrigin);
        if (!double.IsFinite(plan.X0) || !double.IsFinite(plan.Y0) || !double.IsFinite(plan.X1) || !double.IsFinite(plan.Y1) ||
            plan.X0 >= plan.X1 || plan.Y0 >= plan.Y1)
            return null;
        if (profile.Shape == "Custom")
        {
            var polygon = settings[304..];
            if (ReadStraightRings(polygon, out area) is not { } local) return null;
            var rings = local.Select(ring => (IReadOnlyList<PlaPlanPoint>)Array.AsReadOnly(ring
                .Select(p => new PlaPlanPoint(p.X + origin.X, p.Y + origin.Y)).ToArray())).ToArray();
            return new PlaOpeningGeometry(origin, Array.AsReadOnly(rings));
        }
        if (profile.Shape is "Circle" or "Ellipse")
        {
            // The top-center frame uses different circular subdivision counts. Its analytic placement is still available.
            if (profile.Shape == "Circle" && settings[256..].StartsWith(CenteredOpeningFrame))
                area = 36 * Math.Tan(Math.PI / 36) * (profile.Width / 2) * (profile.Height / 2);
            return new PlaOpeningGeometry(origin, null);
        }
        area = profile.Width * profile.Height;
        PlaPlanPoint[] corners = [new(plan.X0, plan.Y0), new(plan.X1, plan.Y0), new(plan.X1, plan.Y1), new(plan.X0, plan.Y1)];
        return new PlaOpeningGeometry(origin, Array.AsReadOnly<IReadOnlyList<PlaPlanPoint>>([Array.AsReadOnly(corners)]));
    }

    private static bool OpeningFitsSlab(ReadOnlySpan<byte> polygon, PlaOpeningGeometry placement, PlaOpeningProfile profile,
        (double X0, double Y0, double X1, double Y1) bounds)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(polygon[47..]) != 0) return false;
        bounds = OpeningCutPlan(bounds, profile);
        if (SlabPolygonContainsFootprint(polygon, bounds, allowBoundary: true)) return true;
        if (profile.Shape != "Custom" || placement.Rings is not { Count: 1 } ||
            ReadStraightRings(polygon, out _) is not { } slab)
            return false;
        // A hole can subtract area even when it lies entirely within the footprint's boundary.
        var ring = placement.Rings[0];
        if (slab.Skip(1).Any(hole => !IsSeparateHole([slab[0], ring], hole) &&
            !OpeningPlansDisjoint(bounds, (hole.Min(p => p.X), hole.Min(p => p.Y), hole.Max(p => p.X), hole.Max(p => p.Y)))))
            return false;
        return Enumerable.Range(0, ring.Count).All(i => SegmentInSlab(ring[i], ring[(i + 1) % ring.Count], slab[0]));
    }

    private static bool SegmentInSlab(PlaPlanPoint a, PlaPlanPoint b, IReadOnlyList<PlaPlanPoint> boundary)
    {
        var positions = new List<double> { 0, 1 };
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (!double.IsFinite(lengthSquared) || lengthSquared == 0) return false;
        for (int i = 0; i < boundary.Count; i++)
        {
            var c = boundary[i];
            var d = boundary[(i + 1) % boundary.Count];
            double ex = d.X - c.X, ey = d.Y - c.Y;
            double det = dx * ey - dy * ex;
            double ox = c.X - a.X, oy = c.Y - a.Y;
            if (det != 0)
            {
                double t = (ox * ey - oy * ex) / det, u = (ox * dy - oy * dx) / det;
                if (t is > 0 and < 1 && u is >= 0 and <= 1) positions.Add(t);
            }
            else
            {
                double t = (ox * dx + oy * dy) / lengthSquared;
                if (t is > 0 and < 1) positions.Add(t);
            }
        }
        positions.Sort();
        for (int i = 1; i < positions.Count; i++)
        {
            double t = (positions[i - 1] + positions[i]) * 0.5;
            var point = new PlaPlanPoint(a.X + t * dx, a.Y + t * dy);
            if (!ContainsPoint(boundary, point) && !PointOnOpeningBoundary(boundary, point)) return false;
        }
        return true;
    }

    private static bool PointOnOpeningBoundary(IReadOnlyList<PlaPlanPoint> ring, PlaPlanPoint point)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0 || !double.IsFinite(lengthSquared)) continue;
            double t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
            double x = point.X - (a.X + t * dx), y = point.Y - (a.Y + t * dy);
            if (x * x + y * y <= JunctionTolerance * JunctionTolerance) return true;
        }
        return false;
    }

    private static (double X0, double Y0, double X1, double Y1) TranslateOpeningPlan(
        (double X0, double Y0, double X1, double Y1) plan, PlaPlanPoint origin) =>
        (plan.X0 + origin.X, plan.Y0 + origin.Y, plan.X1 + origin.X, plan.Y1 + origin.Y);

    private static (double X0, double Y0, double X1, double Y1) OpeningCutPlan(
        (double X0, double Y0, double X1, double Y1) plan, PlaOpeningProfile profile)
    {
        if (profile.Shape != "Circle") return plan;
        double extra = profile.Width / 2 * (1 / Math.Cos(Math.PI / 36) - 1);
        return (plan.X0 - extra, plan.Y0 - extra, plan.X1 + extra, plan.Y1 + extra);
    }

    private static bool OpeningPlansDisjoint((double X0, double Y0, double X1, double Y1) a,
        (double X0, double Y0, double X1, double Y1) b) =>
        a.X1 <= b.X0 || a.X0 >= b.X1 || a.Y1 <= b.Y0 || a.Y0 >= b.Y1;

    private static bool SameOpeningGeometry(PlaOpeningGeometry? first, PlaOpeningGeometry? second)
    {
        if (first == second) return true;
        if (first is null || second is null || first.Origin != second.Origin || first.CutVolume != second.CutVolume)
            return false;
        return (first.Rings is null ? second.Rings is null : second.Rings is { } rings &&
                first.Rings.Count == rings.Count && first.Rings.Zip(rings).All(pair => pair.First.SequenceEqual(pair.Second))) &&
            (first.Cuts is null ? second.Cuts is null : second.Cuts is { } cuts && first.Cuts.SequenceEqual(cuts));
    }
}
