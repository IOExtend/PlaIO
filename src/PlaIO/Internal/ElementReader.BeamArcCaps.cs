using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    // In the plane of the sweep the cap follows the intersection of equal offsets of the two axes.
    // Each point has coordinates (A + B*u, Side*sqrt(C + D*u + E*u*u)) in this local basis.
    private readonly record struct BeamArcCap(PlaPlanPoint Center, PlaPlanPoint Direction, double A, double B,
        double Side, double C, double D, double E)
    {
        public static BeamArcCap Radial(SlabEdge arc, PlaPlanPoint end, double radialSign) =>
            new(arc.Center, UnitFrom(arc.Center, end), arc.Radius, radialSign, 0, 0, 0, 0);

        public static BeamArcCap? AtJoint(SlabEdge arc, PlaBeamReferenceAxis peer, PlaPlanPoint joint, int side,
            BeamVector ownNormal, BeamVector peerNormal)
        {
            double radialSign = -Math.Sign(arc.Sweep);
            bool collinear = Math.Abs(ownNormal.X + peerNormal.X) <= JunctionTolerance &&
                Math.Abs(ownNormal.Y + peerNormal.Y) <= JunctionTolerance;
            if (collinear) return Radial(arc, joint, radialSign);

            double b;
            PlaPlanPoint direction;
            if (peer.PlanSweepRadians == 0)
            {
                double forward = side == 0 ? 1 : -1;
                direction = new(-forward * peerNormal.Y, forward * peerNormal.X);
                b = 1;
            }
            else
            {
                if (ReadCircularEdge(peer.Start, peer.End, peer.PlanSweepRadians, out _) is not { } other)
                    return null;
                double distance = Distance(arc.Center, other.Center);
                if (distance <= JunctionTolerance) return null;
                direction = UnitFrom(arc.Center, other.Center);
                b = (arc.Radius * radialSign + other.Radius * Math.Sign(other.Sweep)) / distance;
            }

            var delta = new PlaPlanPoint(joint.X - arc.Center.X, joint.Y - arc.Center.Y);
            double a = delta.X * direction.X + delta.Y * direction.Y;
            double h = -delta.X * direction.Y + delta.Y * direction.X;
            if (Math.Abs(h) <= JunctionTolerance) return null;
            return new BeamArcCap(arc.Center, direction, a, b, Math.Sign(h), h * h,
                2 * (arc.Radius * radialSign - a * b), 1 - b * b);
        }

        public PlaPlanPoint Point(double u)
        {
            double x = A + B * u;
            double y = Side * Math.Sqrt(Math.Max(0, Discriminant(u)));
            return new(Center.X + Direction.X * x - Direction.Y * y, Center.Y + Direction.Y * x + Direction.X * y);
        }

        public bool StaysNear(PlaPlanPoint nominal, double angle, double min, double max)
        {
            if (!(Discriminant(min) >= 0 && Discriminant(max) >= 0)) return false;
            double vertex = -D / (2 * E);
            if (E > 0 && vertex > min && vertex < max && !(Discriminant(vertex) > 0)) return false;

            var ray = UnitFrom(Center, nominal);
            var first = new PlaPlanPoint(Math.Sin(angle) * ray.X - Math.Cos(angle) * ray.Y,
                Math.Sin(angle) * ray.Y + Math.Cos(angle) * ray.X);
            var second = new PlaPlanPoint(first.X + 2 * Math.Cos(angle) * ray.Y,
                first.Y - 2 * Math.Cos(angle) * ray.X);
            return StaysInside(first, min, max) && StaysInside(second, min, max);
        }

        private bool StaysInside(PlaPlanPoint normal, double min, double max)
        {
            foreach (double u in ExtremeOffsets(normal, min, max))
            {
                var point = Point(u);
                if (!((point.X - Center.X) * normal.X + (point.Y - Center.Y) * normal.Y > JunctionTolerance))
                    return false;
            }
            return true;
        }

        public IEnumerable<double> ExtremeOffsets(PlaPlanPoint projection, double min, double max)
        {
            yield return min;
            yield return max;
            if (Side == 0) yield break;
            // Squaring the derivative gives a quadratic. Extra roots are still points on the cap.
            double linear = B * (Direction.X * projection.X + Direction.Y * projection.Y);
            double radical = -Direction.Y * projection.X + Direction.X * projection.Y;
            double weight = linear * linear - radical * radical * E;
            double a = 4 * E * weight, b = 4 * D * weight;
            double c = 4 * linear * linear * C - radical * radical * D * D;
            if (a == 0)
            {
                double root = -c / b;
                if (root > min && root < max) yield return root;
                yield break;
            }
            double square = b * b - 4 * a * c;
            if (square < 0) yield break;
            double numerator = -(b + Math.CopySign(Math.Sqrt(square), b)) / 2;
            double first = numerator / a, second = c / numerator;
            if (first > min && first < max) yield return first;
            if (second > min && second < max) yield return second;
        }

        private double Discriminant(double u) => C + u * (D + E * u);

        private static PlaPlanPoint UnitFrom(PlaPlanPoint from, PlaPlanPoint to)
        {
            double length = Distance(from, to);
            return new((to.X - from.X) / length, (to.Y - from.Y) / length);
        }
    }

    private static PlaBoundingBox? CurvedBeamPlan(SlabEdge arc, BeamArcCap start, BeamArcCap end,
        double min, double max, Elevations height, Func<PlaPlanPoint, double, bool> survives)
    {
        var points = new List<(PlaPlanPoint Point, double U)>();
        foreach (var cap in new[] { start, end })
        {
            foreach (double u in cap.ExtremeOffsets(new(1, 0), min, max)) points.Add((cap.Point(u), u));
            foreach (double u in cap.ExtremeOffsets(new(0, 1), min, max)) points.Add((cap.Point(u), u));
        }

        // At an interior angle each plan coordinate is linear in the radius.
        foreach (double u in new[] { min, max })
        {
            var first = start.Point(u);
            var last = end.Point(u);
            double angle = Math.Atan2(first.Y - arc.Center.Y, first.X - arc.Center.X);
            double sweep = Math.Sign(arc.Sweep) * ForwardAngle(Math.Sign(arc.Sweep) *
                (Math.Atan2(last.Y - arc.Center.Y, last.X - arc.Center.X) - angle));
            var edge = new SlabEdge(first, last, sweep, arc.Center, arc.Radius - Math.Sign(arc.Sweep) * u);
            for (int i = 0; i < 4; i++)
            {
                double cardinal = i * (Math.PI / 2);
                double travel = ForwardAngle(Math.Sign(sweep) * (cardinal - angle));
                if (travel <= Math.Abs(sweep)) points.Add((edge.AtAngle(cardinal), u));
            }
        }
        double x0 = points.Min(p => p.Point.X), x1 = points.Max(p => p.Point.X);
        double y0 = points.Min(p => p.Point.Y), y1 = points.Max(p => p.Point.Y);
        return Witness(p => p.X, x0) && Witness(p => p.X, x1) && Witness(p => p.Y, y0) && Witness(p => p.Y, y1)
            ? MakeBoundingBox((x0, y0, x1, y1), height.Bottom, height.Top) : null;

        bool Witness(Func<PlaPlanPoint, double> coordinate, double extreme) =>
            points.Any(p => Math.Abs(coordinate(p.Point) - extreme) <= JunctionTolerance && survives(p.Point, p.U));

        static double ForwardAngle(double radians)
        {
            radians %= Math.Tau;
            return radians < 0 ? radians + Math.Tau : radians;
        }
    }
}
