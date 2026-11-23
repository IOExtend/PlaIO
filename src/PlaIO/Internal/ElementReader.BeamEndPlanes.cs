using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private readonly record struct BeamVector(double X, double Y, double Z)
    {
        public bool IsUnit => Math.Abs(Dot(this) - 1) <= JunctionTolerance;
        public double Dot(BeamVector other) => X * other.X + Y * other.Y + Z * other.Z;
        public static BeamVector operator +(BeamVector a, BeamVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static BeamVector operator *(BeamVector a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
    }

    private readonly record struct BeamCaps(PlaBoundingBox Bounds, PlaBoundingBox First, PlaBoundingBox Last,
        double FullSectionStart, double FullSectionEnd);

    private static BeamVector ReadBeamVector(ReadOnlySpan<byte> record, int offset) =>
        new(ReadDouble(record, offset), ReadDouble(record, offset + 8), ReadDouble(record, offset + 16));

    private static bool BeamsMeetAtVerticalButt(BeamPrism first, BeamEndPlanes firstPlanes,
        BeamPrism second, BeamEndPlanes secondPlanes, double x, double y)
    {
        if (!IsOnBeamJoint(first, x, y, end: true) || !IsOnBeamJoint(second, x, y, end: true)) return false;
        bool firstStart = AtStart(first), secondStart = AtStart(second);
        var a = firstStart ? firstPlanes.StartNormal : firstPlanes.EndNormal;
        var b = secondStart ? secondPlanes.StartNormal : secondPlanes.EndNormal;
        double firstSign = firstStart ? -1 : 1, secondSign = secondStart ? -1 : 1;
        return AlongAxis(a, firstSign * first.Dx, firstSign * first.Dy) &&
            AlongAxis(b, secondSign * second.Dx, secondSign * second.Dy) &&
            Math.Abs(a.X + b.X) <= JunctionTolerance && Math.Abs(a.Y + b.Y) <= JunctionTolerance;

        bool AtStart(BeamPrism beam) => Math.Abs((x - beam.X) * beam.Dx + (y - beam.Y) * beam.Dy) <= JunctionTolerance;
    }

    private static BeamCaps? ProjectBeamCaps(BeamPrism axis, BeamEndPlanes planes,
        ReadOnlySpan<byte> hatch, PlaMemberProfile? profile)
    {
        var vertices = profile is null ? StraightSectionVertices(hatch) : default;
        if (profile is null && vertices.IsEmpty) return null;
        double length = double.Hypot(axis.Length, planes.Rise);
        if (!double.IsFinite(length)) return null;
        double cos = axis.Length / length, sin = planes.Rise / length;
        var tangent = new BeamVector(cos * axis.Dx, cos * axis.Dy, sin);
        var right = new BeamVector(axis.Dy, -axis.Dx, 0);
        var up = new BeamVector(-sin * axis.Dx, -sin * axis.Dy, cos);
        var u = right * axis.CosRoll + up * axis.SinRoll;
        var v = right * -axis.SinRoll + up * axis.CosRoll;
        double startDot = planes.StartNormal.Dot(tangent), endDot = planes.EndNormal.Dot(tangent);
        if (!planes.StartNormal.IsUnit || !planes.EndNormal.IsUnit ||
            !(startDot < -JunctionTolerance && endDot > JunctionTolerance))
            return null;

        var origin = new BeamVector(axis.X, axis.Y, axis.Z);
        var end = new BeamVector(axis.X + axis.Dx * axis.Length, axis.Y + axis.Dy * axis.Length, axis.Z + planes.Rise);
        var heading = new BeamVector(axis.Dx, axis.Dy, 0);
        if (ProjectEnd(vertices, planes.StartNormal, startDot, origin, out double startInset) is not { } first ||
            ProjectEnd(vertices, planes.EndNormal, endDot, end, out double endInset, beginning: false) is not { } last ||
            !(axis.Length + endInset - startInset > JunctionTolerance))
            return null;

        var box = MakeBoundingBox((Math.Min(first.XMin, last.XMin), Math.Min(first.YMin, last.YMin),
            Math.Max(first.XMax, last.XMax), Math.Max(first.YMax, last.YMax)),
            Math.Min(first.ZMin, last.ZMin), Math.Max(first.ZMax, last.ZMax));
        return box is null ? null : new BeamCaps(box, first, last, startInset, axis.Length + endInset);

        PlaBoundingBox? ProjectEnd(ReadOnlySpan<byte> points, BeamVector normal, double axisDot, BeamVector point,
            out double inset, bool beginning = true)
        {
            inset = 0;
            // Slide each profile coordinate along its generator until it meets the saved plane.
            var a = u + tangent * (-normal.Dot(u) / axisDot);
            var b = v + tangent * (-normal.Dot(v) / axisDot);
            var axial = ProjectBeamSection(points, profile?.Components, a.Dot(heading), b.Dot(heading));
            var x = ProjectBeamSection(points, profile?.Components, a.X, b.X);
            var y = ProjectBeamSection(points, profile?.Components, a.Y, b.Y);
            var z = ProjectBeamSection(points, profile?.Components, a.Z, b.Z);
            if (axial is not { } shifts || x is not { } px || y is not { } py || z is not { } pz) return null;
            inset = beginning ? shifts.Max : shifts.Min;
            return new PlaBoundingBox(point.X + px.Min, point.Y + py.Min, point.Z + pz.Min,
                point.X + px.Max, point.Y + py.Max, point.Z + pz.Max);
        }
    }

    private static (double Min, double Max)? ProjectBeamSection(ReadOnlySpan<byte> vertices, IReadOnlyList<PlaProfileComponent>? components,
        double u, double z)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        if (components is null)
        {
            for (int i = 0; i < vertices.Length; i += 16) Include(ReadDouble(vertices, i), ReadDouble(vertices, i + 8));
        }
        else
        {
            double angle = Math.Atan2(z, u);
            foreach (var component in components)
            {
                var ring = component.Rings[0];
                foreach (var point in ring) Include(point.U, point.Z);
                foreach (var arc in component.Arcs ?? [])
                {
                    if (arc.RingIndex != 0) continue;
                    var p = ring[arc.StartVertexIndex];
                    var q = ring[(arc.StartVertexIndex + 1) % ring.Count];
                    if (ReadCircularEdge(new(p.U, p.Z), new(q.U, q.Z), arc.SweepRadians, out _) is not { } edge)
                        return null;
                    if (edge.IncludesAngle(angle))
                    {
                        var point = edge.AtAngle(angle);
                        Include(point.X, point.Y);
                    }
                    if (edge.IncludesAngle(angle + Math.PI))
                    {
                        var point = edge.AtAngle(angle + Math.PI);
                        Include(point.X, point.Y);
                    }
                }
            }
        }
        return double.IsFinite(min) && double.IsFinite(max) ? (min, max) : null;

        void Include(double x, double y)
        {
            double value = u * x + z * y;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }
    }
}
