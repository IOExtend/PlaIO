using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private readonly record struct RectangularColumnPlan(PlaPlanPoint Origin, double HalfWidth, double HalfDepth, double Angle);

    private static bool JoinedBeamClearsColumn(ModelGeometry geometry, Guid beam, Guid junction,
        ElementLocation location, double home, RectangularColumnPlan plan)
    {
        if (geometry.Read(beam, BeamMemoClass) is not { } memo ||
            ReadBeamPrism(geometry, beam, memo, home) is not { SinRoll: 0 } axis ||
            !SameIntersectionGroup(geometry, axis.Layer, location.LayerIndex)) return false;
        var hatch = ReadBeamHatch(geometry, beam, memo);
        if (ReadBeamRectangle(hatch) is not { } rectangle ||
            geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..])) is not (> 0 and { } priority))
            return false;

        var (sin, cos) = Math.SinCos(plan.Angle);
        var ring = new List<PlaPlanPoint>(4)
        {
            Project(-plan.HalfWidth, -plan.HalfDepth), Project(plan.HalfWidth, -plan.HalfDepth),
            Project(plan.HalfWidth, plan.HalfDepth), Project(-plan.HalfWidth, plan.HalfDepth),
        };
        double half = rectangle.HalfWidth;
        ring = ClipHalfPlane(ring, xAxis: false, -half - JunctionTolerance, keepBelow: false);
        ring = ClipHalfPlane(ring, xAxis: false, half + JunctionTolerance, keepBelow: true);
        double start = 0, end = axis.Length;
        double first = ring.Count == 0 ? double.PositiveInfinity : ring.Min(p => p.X);
        double last = ring.Count == 0 ? double.NegativeInfinity : ring.Max(p => p.X);
        bool OutsideCaps() => last < start - JunctionTolerance || first > end + JunctionTolerance;
        return OutsideCaps() && HasOnlyBeamTargetContact(memo, BeamColumnRelation, junction, axis.Owner, BoundContact) && OutsideCaps();

        PlaPlanPoint Project(double u, double v)
        {
            double x = (plan.Origin.X - axis.X) + (cos * u - sin * v);
            double y = (plan.Origin.Y - axis.Y) + (sin * u + cos * v);
            return new(x * axis.Dx + y * axis.Dy, x * axis.Dy - y * axis.Dx);
        }

        bool BoundContact(Guid role, Guid contact)
        {
            if (role == BeamColumnRelation || role == BeamSlabRelation || role == BeamWallRelation)
                // Structural cuts can only remove material from this beam.
                return ReadBeamStructuralNeighbor(geometry, beam, role, contact) is not null;

            var collision = ReadBeamAxisJunction(geometry, beam, role, contact, withoutPoint: true);
            if ((collision ?? ReadBeamAxisJunction(geometry, beam, role, contact)) is not { } join ||
                geometry.Read(join.Peer, BeamMemoClass) is not { } peerMemo || !HasGeometryRelation(peerMemo, join.Reciprocal, contact) ||
                ReadBeamPrism(geometry, join.Peer, peerMemo, home) is not { SinRoll: 0 } peer ||
                !SameIntersectionGroup(geometry, axis.Layer, peer.Layer) ||
                ReadBeamContactExtents(geometry, join.Peer, peerMemo, peer) is not { } section ||
                Math.Abs(section.Left + half) > JunctionTolerance || Math.Abs(section.Right - half) > JunctionTolerance ||
                Math.Abs(section.Height.Bottom - (axis.Z - rectangle.Depth)) > JunctionTolerance ||
                Math.Abs(section.Height.Top - axis.Z) > JunctionTolerance)
                return false;

            if (collision is not null)
            {
                // Bound the point-free crossing by both section strips in the cutter's coordinates.
                double slope = axis.Dx * peer.Dy - axis.Dy * peer.Dx;
                if (Math.Abs(slope) <= JunctionTolerance) return false;
                double station = ((peer.X - axis.X) * peer.Dy - (peer.Y - axis.Y) * peer.Dx) / slope;
                var crossing = ProjectBeamJunctionInterval(axis, peer, axis.X + station * axis.Dx, axis.Y + station * axis.Dy,
                    -half, half, section.Left, section.Right);
                return crossing is { } bounds && bounds.Min > JunctionTolerance && bounds.Max < axis.Length - JunctionTolerance;
            }

            // The same join can be recorded with an end role in several evaluation contexts. Use the axis endpoints.
            bool ownEnd = IsOnBeamJoint(axis, join.X, join.Y, end: true);
            bool peerEnd = IsOnBeamJoint(peer, join.X, join.Y, end: true);
            if ((!ownEnd && !IsOnBeamJoint(axis, join.X, join.Y, end: false)) ||
                (!peerEnd && !IsOnBeamJoint(peer, join.X, join.Y, end: false))) return false;

            double dot = axis.Dx * peer.Dx + axis.Dy * peer.Dy;
            if (Math.Abs(axis.Dx * peer.Dy - axis.Dy * peer.Dx) <= JunctionTolerance)
            {
                var peerHatch = ReadBeamHatch(geometry, join.Peer, peerMemo);
                return ownEnd && peerEnd && dot > 0 && Math.Abs(axis.Length - peer.Length) <= JunctionTolerance &&
                    double.Hypot(axis.X - peer.X, axis.Y - peer.Y) <= JunctionTolerance && !peerHatch.IsEmpty &&
                    geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(peerHatch[18..])) is > 0 and { } p &&
                    p < priority;
            }

            // A joined cap stays within the intersection of the matching section strips.
            if (ProjectBeamJunctionInterval(axis, peer, join.X, join.Y, -half, half, section.Left, section.Right) is not { } reach)
                return false;
            start = Math.Min(start, reach.Min);
            end = Math.Max(end, reach.Max);
            return true;
        }
    }

    private static List<(Guid Memo, Guid Junction, uint Priority)>? ReadColumnBeamCutters(ModelGeometry geometry,
        Guid memoId, List<(Guid Key, Guid Id)> contacts, ElementLocation location, uint priority, Elevations range, bool profiled)
    {
        var cutters = new List<(Guid Memo, Guid Junction, uint Priority)>();
        foreach (var (key, id) in contacts)
        {
            var (kind, role) = key == ColumnBeamRelation ? (BeamColumnJunctionClass, BeamColumnRelation) :
                key == ColumnSlabRelation ? (SlabColumnJunctionClass, SlabColumnRelation) : (WallColumnJunctionClass, WallColumnRelation);
            if (geometry.Read(id, kind) is not { } joint || ReadSingleRelation(joint, key, requireSection: true) != memoId)
                return null;
            Guid peer = ReadSingleRelation(joint, role, requireSection: true);
            if (key == ColumnBeamRelation)
            {
                if (ReadBeamPriority(geometry, peer, id) is not (> 0 and { } beamPriority)) return null;
                if (!profiled && geometry.Read(peer, BeamMemoClass) is { } beamMemo &&
                    geometry.Read(ReadSingleRelation(beamMemo, SectionProfileRelation, 0x11, requireSection: true), RectangularSectionClass) is null)
                {
                    if (ReadBeamPrism(geometry, peer, beamMemo, 0) is not { } axis ||
                        ReadColumnCutterEnvelope(geometry, peer, beamMemo, axis) is not { } envelope) return null;
                    beamPriority = envelope.Priority;
                }
                if (beamPriority > priority) cutters.Add((peer, id, beamPriority));
            }
            else if (key == ColumnSlabRelation)
            {
                if (geometry.Read(peer, SlabClass) is not { } body || !HasGeometryRelation(body, role, id)) return null;
                var record = body.AsSpan(FindClassFrame(body, SlabClass));
                var settings = AfterCountedString(record, ClassElementIdOffset("Slab"));
                if (ReadSlabPriority(settings, geometry.Attributes) is not (> 0 and { } slabPriority) || slabPriority > priority &&
                    !SlabKeepsPlanes(geometry, record, settings, location, priority, range.Top, range.Bottom, range, checkSkins: true))
                    return null;
            }
            else if (geometry.Read(peer, WallClass) is not { } wall || !HasGeometryRelation(wall, role, id) ||
                ReadWallPriority(geometry, peer, wall) is not (> 0 and { } wallPriority) || wallPriority > priority ||
                !profiled && WallMayWrapColumn(geometry.Attributes, ReadWallSettings(wall)))
                return null;
        }
        return cutters;
    }

    private static Elevations[]? ReadColumnBeamBands(ModelGeometry geometry, Guid memoId, List<(Guid Key, Guid Id)> contacts,
        ElementLocation location, uint priority, double home, Elevations range,
        (double X0, double Y0, double X1, double Y1) section, PlaPlanPoint origin, out List<ColumnBeamMask>? partialMasks)
    {
        partialMasks = null;
        if (ReadColumnBeamCutters(geometry, memoId, contacts, location, priority, range, profiled: true) is not { } cutters)
            return null;
        var active = cutters.Select(c => c.Memo).ToHashSet();
        var bounds = new Dictionary<Guid, (double Start, double End)>();
        var throughSpans = new List<(Guid Memo, double Start, double End)>();
        var cuts = new List<Elevations>();
        var masks = new List<ColumnBeamMask>();
        bool partial = false;
        foreach (var (peer, id, beamPriority) in cutters)
        {
            if (geometry.Read(peer, BeamMemoClass) is not { } memo ||
                ReadBeamPrism(geometry, peer, memo, home) is not { } axis ||
                !SameIntersectionGroup(geometry, axis.Layer, location.LayerIndex)) return null;
            var hatch = ReadBeamHatch(geometry, peer, memo);
            if (BeamSectionElevations(axis, hatch) is not { } beam) return null;
            if (DisjointHeights(beam.Bottom, beam.Top, range)) continue;
            var limits = (Start: (Right: 0.0, Left: 0.0), End: (Right: axis.Length, Left: axis.Length));
            var miters = new List<ColumnBeamMiter>();
            var requiredMiters = new List<ColumnBeamMiter>();
            if (axis.SinRoll != 0 || ReadBeamRectangle(hatch) is not { } rectangle ||
                !HasOnlyBeamTargetContact(memo, BeamColumnRelation, id, axis.Owner, (r, j) =>
                    BeamKeepsColumnBand(geometry, peer, axis, r, j, home, section, beamPriority, priority,
                        rectangle, active, throughSpans, miters, requiredMiters, ref limits)) ||
                requiredMiters.Any(required => !miters.Any(required.Matches)))
                return null;

            double fullStart = Math.Max(limits.Start.Right, limits.Start.Left);
            double fullEnd = Math.Min(limits.End.Right, limits.End.Left);
            if (fullEnd - fullStart <= JunctionTolerance) return null;
            bounds[peer] = (fullStart, fullEnd);
            var (start, end) = ProjectRectangle(section, axis.X, axis.Y, axis.Dx, axis.Dy);
            var (left, right) = ProjectRectangle(section, axis.X, axis.Y, axis.Dy, -axis.Dx);
            if (end <= Math.Min(limits.Start.Right, limits.Start.Left) + JunctionTolerance ||
                start >= Math.Max(limits.End.Right, limits.End.Left) - JunctionTolerance ||
                right <= -rectangle.HalfWidth + JunctionTolerance || left >= rectangle.HalfWidth - JunctionTolerance)
                continue;
            if (start < fullStart - JunctionTolerance || end > fullEnd + JunctionTolerance ||
                left < -rectangle.HalfWidth - JunctionTolerance || right > rectangle.HalfWidth + JunctionTolerance)
                partial = true;
            var cut = new Elevations(Math.Max(range.Bottom, beam.Bottom), Math.Min(range.Top, beam.Top));
            cuts.Add(cut);
            double x = axis.X - origin.X, y = axis.Y - origin.Y;
            double width = rectangle.HalfWidth;
            masks.Add(new ColumnBeamMask(cut.Bottom, cut.Top,
                [At(limits.Start.Right, width), At(limits.End.Right, width),
                 At(limits.End.Left, -width), At(limits.Start.Left, -width)]));

            PlaPlanPoint At(double station, double across) =>
                new(x + axis.Dx * station + axis.Dy * across, y + axis.Dy * station - axis.Dx * across);
        }

        if (throughSpans.Any(s => !bounds.TryGetValue(s.Memo, out var span) ||
            s.Start < span.Start - JunctionTolerance || s.End > span.End + JunctionTolerance))
            return null;
        if (partial)
        {
            partialMasks = masks;
            return null;
        }
        var bands = new List<Elevations>();
        double cursor = range.Bottom;
        foreach (var cut in cuts.OrderBy(c => c.Bottom))
        {
            if (cut.Bottom > cursor + JunctionTolerance) bands.Add(new Elevations(cursor, cut.Bottom));
            cursor = Math.Max(cursor, cut.Top);
        }
        if (range.Top > cursor + JunctionTolerance) bands.Add(new Elevations(cursor, range.Top));
        return bands.Count > 0 ? bands.ToArray() : null;
    }

    private static List<ColumnBeamMask>? ReadCoextensiveColumnBeamCut(ModelGeometry geometry, Guid memoId,
        List<(Guid Key, Guid Id)> contacts, ElementLocation location, uint priority, double home, Elevations range,
        PlaPlanPoint origin, PlaPlanPoint[] ring)
    {
        if (ReadColumnBeamCutters(geometry, memoId, contacts, location, priority, range, profiled: false) is not { Count: > 0 } cutters)
            return null;
        var strongest = cutters.MaxBy(c => c.Priority);
        if (geometry.Read(strongest.Memo, BeamMemoClass) is not { } mainMemo ||
            ReadBeamPrism(geometry, strongest.Memo, mainMemo, home) is not { SinRoll: 0 } main ||
            !SameIntersectionGroup(geometry, location.LayerIndex, main.Layer) ||
            geometry.Read(ReadSingleRelation(mainMemo, SectionProfileRelation, 0x11, requireSection: true), RectangularSectionClass) is null ||
            ReadBeamRectangle(ReadBeamHatch(geometry, strongest.Memo, mainMemo)) is not { } rectangle)
            return null;

        double bottom = main.Z - rectangle.Depth, top = main.Z, half = rectangle.HalfWidth;
        if (DisjointHeights(bottom, top, range)) return null;
        var group = cutters.Select(c => c.Memo).ToHashSet();
        foreach (var (id, junction, _) in cutters)
        {
            if (id == strongest.Memo)
            {
                if (!HasOnlyBeamTargetContact(mainMemo, BeamColumnRelation, junction, main.Owner,
                    (role, contact) => PreservesCut(id, main, role, contact))) return null;
                continue;
            }

            if (geometry.Read(id, BeamMemoClass) is not { } memo ||
                ReadBeamPrism(geometry, id, memo, home) is not { SinRoll: 0 } axis ||
                !SameIntersectionGroup(geometry, main.Layer, axis.Layer) ||
                double.Hypot(axis.X - main.X, axis.Y - main.Y) > JunctionTolerance ||
                double.Hypot(axis.Dx * axis.Length - main.Dx * main.Length, axis.Dy * axis.Length - main.Dy * main.Length) > JunctionTolerance ||
                ReadColumnCutterEnvelope(geometry, id, memo, axis) is not { } section || section.Priority >= strongest.Priority ||
                section.Left < -half - JunctionTolerance || section.Right > half + JunctionTolerance ||
                section.Height.Bottom < bottom - JunctionTolerance || section.Height.Top > top + JunctionTolerance ||
                !HasOnlyBeamTargetContact(memo, BeamColumnRelation, junction, axis.Owner,
                    (role, contact) => PreservesCut(id, axis, role, contact)))
                return null;
        }

        return [new ColumnBeamMask(Math.Max(range.Bottom, bottom), Math.Min(range.Top, top),
            [PointAt(0, half), PointAt(main.Length, half), PointAt(main.Length, -half), PointAt(0, -half)])];

        PlaPlanPoint PointAt(double station, double across) =>
            new(main.X - origin.X + main.Dx * station + main.Dy * across,
                main.Y - origin.Y + main.Dy * station - main.Dx * across);

        bool PreservesCut(Guid id, BeamPrism beam, Guid role, Guid contact)
        {
            if (role == BeamColumnRelation)
                return ReadBeamStructuralNeighbor(geometry, id, role, contact) is not null;
            if (role == BeamWallRelation || role == BeamSlabRelation)
                return id == strongest.Memo && BeamWinsContact(geometry, id, role, contact, strongest.Priority);
            var interior = ReadBeamAxisJunction(geometry, id, role, contact, withoutPoint: true);
            if ((interior ?? ReadBeamAxisJunction(geometry, id, role, contact)) is not { } join ||
                geometry.Read(join.Peer, BeamMemoClass) is not { } memo || !HasGeometryRelation(memo, join.Reciprocal, contact) ||
                ReadBeamPrism(geometry, join.Peer, memo, home) is not { } peer ||
                !SameIntersectionGroup(geometry, beam.Layer, peer.Layer) ||
                interior is null && (!OnAxis(beam, join.X, join.Y) || !OnAxis(peer, join.X, join.Y)))
                return false;
            if (group.Contains(join.Peer)) return true;

            // Cleanup at a reciprocal beam join cannot change the cut outside the connected beam's strip.
            if (ReadColumnCutterEnvelope(geometry, join.Peer, memo, peer) is not { } other) return false;
            if (DisjointHeights(other.Height.Bottom, other.Height.Top, range)) return true;
            double offset = (origin.X - peer.X) * peer.Dy - (origin.Y - peer.Y) * peer.Dx;
            return ring.All(p => offset + p.X * peer.Dy - p.Y * peer.Dx < other.Left - JunctionTolerance) ||
                ring.All(p => offset + p.X * peer.Dy - p.Y * peer.Dx > other.Right + JunctionTolerance);
        }

        static bool OnAxis(BeamPrism beam, double x, double y) =>
            IsOnBeamJoint(beam, x, y, end: true) || IsOnBeamJoint(beam, x, y, end: false);
    }

    private static (Elevations Height, double Left, double Right, uint Priority)? ReadColumnCutterEnvelope(
        ModelGeometry geometry, Guid id, byte[] memo, BeamPrism axis)
    {
        Guid section = ReadSingleRelation(memo, SectionProfileRelation, 0x11, requireSection: true);
        var materials = geometry.Attributes.Material;
        if (geometry.Read(section, RectangularSectionClass) is not null)
        {
            var hatch = ReadBeamHatch(geometry, id, memo);
            return BeamSectionExtents(axis, hatch) is { } outline &&
                materials.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..])) is > 0 and { } priority
                    ? (outline.Height, outline.Left, outline.Right, priority) : null;
        }
        if (geometry.Read(axis.Owner, BeamClass) is not { } body ||
            ReadMemberProfile(body.AsSpan(FindClassFrame(body, BeamClass)), axis.Owner, beam: true, geometry) is not { } profile ||
            ProjectBeamProfile(axis, profile) is not { } extents)
            return null;
        uint maximum = 0;
        foreach (var part in profile.Components)
        {
            if (materials.GetValueOrDefault(part.BuildingMaterialIndex) is not (> 0 and { } priority)) return null;
            maximum = Math.Max(maximum, priority);
        }
        return (extents.Height, extents.Left, extents.Right, maximum);
    }

    private static bool BeamKeepsColumnBand(ModelGeometry geometry, Guid beam, BeamPrism axis, Guid role, Guid junction,
        double home, (double X0, double Y0, double X1, double Y1) section, uint priority, uint columnPriority,
        (double HalfWidth, double Depth) rectangle, HashSet<Guid> active,
        List<(Guid Memo, double Start, double End)> throughSpans,
        List<ColumnBeamMiter> miters, List<ColumnBeamMiter> requiredMiters,
        ref ((double Right, double Left) Start, (double Right, double Left) End) limits)
    {
        if (role == BeamColumnRelation)
            // Each column contact uses the beam sweep before cuts from the other column contacts.
            return ReadBeamStructuralNeighbor(geometry, beam, role, junction) is not null;
        if (role == BeamWallRelation || role == BeamSlabRelation)
            return BeamWinsContact(geometry, beam, role, junction, priority);

        var overlap = ReadBeamAxisJunction(geometry, beam, role, junction, withoutPoint: true);
        if ((overlap ?? ReadBeamAxisJunction(geometry, beam, role, junction)) is not { } join ||
            geometry.Read(join.Peer, BeamMemoClass) is not { } memo || !HasGeometryRelation(memo, join.Reciprocal, junction) ||
            ReadStraightBeamAxis(geometry, join.Peer, memo, home, out var planes, readAnnotations: true) is not { } other ||
            !planes.HasSquareHorizontalEnds(other) ||
            !SameIntersectionGroup(geometry, axis.Layer, other.Layer) ||
            ReadBeamContactExtents(geometry, join.Peer, memo, other) is not { } outline)
            return false;

        var otherHatch = ReadBeamHatch(geometry, join.Peer, memo);
        uint? peerPriority = otherHatch.IsEmpty ? null
            : geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(otherHatch[18..]));
        var (min, max) = ProjectRectangle(section, other.X, other.Y, other.Dy, -other.Dx);
        bool outsideStrip = max < outline.Left - JunctionTolerance || min > outline.Right + JunctionTolerance;
        if (overlap is not null)
            // An interior collision has no joined cap. A weaker peer leaves the cutter intact.
            return outsideStrip || peerPriority is > 0 && peerPriority < priority ||
                DisjointHeights(outline.Height.Bottom, outline.Height.Top, new Elevations(axis.Z - rectangle.Depth, axis.Z));

        bool ownEnd = IsBeamEndRelation(role), peerEnd = IsBeamEndRelation(join.Reciprocal);
        if (!IsOnBeamJoint(axis, join.X, join.Y, ownEnd) || !IsOnBeamJoint(other, join.X, join.Y, peerEnd))
            return false;
        if (!ownEnd && !peerEnd && peerPriority is > 0 && peerPriority < priority &&
            ReadColumnCutterEnvelope(geometry, join.Peer, memo, other) is { Priority: > 0 } interior && interior.Priority < priority)
            return true;
        if (ownEnd && peerEnd &&
            Math.Abs(axis.Dx * other.Dy - axis.Dy * other.Dx) <= JunctionTolerance)
        {
            double along = (join.X - axis.X) * axis.Dx + (join.Y - axis.Y) * axis.Dy;
            double peerAlong = (join.X - other.X) * other.Dx + (join.Y - other.Y) * other.Dy;
            double direction = axis.Dx * other.Dx + axis.Dy * other.Dy;
            // Square caps on opposite sides of a common collinear end meet without changing either sweep.
            if ((axis.Length / 2 - along) * (other.Length / 2 - peerAlong) * direction < 0) return true;
            // At coincident ends of coextensive beams the weaker material cannot change the cutter.
            return Math.Abs(axis.Length - other.Length) <= JunctionTolerance && peerPriority is > 0 && peerPriority < priority;
        }
        // The connected beam can only change this band's cross-section inside its transverse strip.
        if (outsideStrip) return true;

        if (other.SinRoll != 0 || Math.Abs(outline.Left + outline.Right) > JunctionTolerance ||
            Math.Abs(axis.Z - outline.Height.Top) > JunctionTolerance ||
            Math.Abs(axis.Z - rectangle.Depth - outline.Height.Bottom) > JunctionTolerance)
            return false;

        var otherRectangle = ReadBeamRectangle(otherHatch);
        double dot = axis.Dx * other.Dx + axis.Dy * other.Dy;
        if (ownEnd && peerEnd)
        {
            if (Math.Abs(outline.Right - rectangle.HalfWidth) > JunctionTolerance ||
                ReadColumnBeamMiter(axis, other, join.X, join.Y, rectangle.HalfWidth) is not { } miter) return false;
            if (peerPriority == priority && otherRectangle is { } match &&
                Math.Abs(rectangle.HalfWidth - match.HalfWidth) <= JunctionTolerance)
            {
                if (!active.Contains(join.Peer)) return MiterClearsColumn(axis, miter, rectangle.HalfWidth, section);
                ref var activeCap = ref (miter.AtStart ? ref limits.Start : ref limits.End);
                double savedEnd = miter.AtStart ? 0 : axis.Length;
                if ((activeCap.Right != savedEnd || activeCap.Left != savedEnd) &&
                    !miter.Matches(new(miter.AtStart, activeCap.Right, activeCap.Left))) return false;
                activeCap = (miter.Right, miter.Left);
                miters.Add(miter);
                return true;
            }
            // A weaker overlapping peer can repeat the same join in another evaluation context.
            if (peerPriority is not (> 0) || peerPriority >= priority || active.Contains(join.Peer) ||
                ReadColumnCutterEnvelope(geometry, join.Peer, memo, other) is not { } weaker || weaker.Priority > columnPriority ||
                weaker.Left < -rectangle.HalfWidth - JunctionTolerance || weaker.Right > rectangle.HalfWidth + JunctionTolerance ||
                weaker.Height.Bottom < axis.Z - rectangle.Depth - JunctionTolerance || weaker.Height.Top > axis.Z + JunctionTolerance)
                return false;
            requiredMiters.Add(miter);
            return true;
        }
        if (ownEnd == peerEnd || peerPriority is not (> 0)) return false;

        var through = ownEnd ? other : axis;
        var branch = ownEnd ? axis : other;
        double width = ownEnd ? rectangle.HalfWidth : outline.Right;
        double throughWidth = ownEnd ? outline.Right : rectangle.HalfWidth;
        if (Math.Abs(dot) * width > throughWidth + JunctionTolerance ||
            ProjectBeamJunctionInterval(through, branch, join.X, join.Y, -throughWidth, throughWidth, -width, width)
                is not { } reach ||
            reach.Min <= JunctionTolerance || reach.Max >= through.Length - JunctionTolerance)
            return false;

        // For equal cutters, the through beam contains the joined end's change in plan.
        if (active.Contains(join.Peer))
        {
            if (peerPriority != priority || otherRectangle is null) return false;
            throughSpans.Add((ownEnd ? join.Peer : beam, reach.Min, reach.Max));
            return true;
        }
        if (peerPriority > columnPriority || otherRectangle is not null ||
            !IsConcaveBeamSection(StraightSectionVertices(otherHatch))) return false;
        if (!ownEnd) return true;

        // Intersect both sides of the rectangular branch with the weaker through beam's near face.
        double at = (join.X - axis.X) * axis.Dx + (join.Y - axis.Y) * axis.Dy;
        bool atStart = at < axis.Length / 2;
        double slope = axis.Dx * other.Dy - axis.Dy * other.Dx;
        double face = (atStart ? slope : -slope) > 0 ? outline.Right : outline.Left;
        double side = rectangle.HalfWidth * dot;
        ref var cap = ref (atStart ? ref limits.Start : ref limits.End);
        double saved = atStart ? 0 : axis.Length;
        double rightAtFace = saved + (face - side) / slope;
        double leftAtFace = saved + (face + side) / slope;
        if (cap.Right != saved || cap.Left != saved)
            return Math.Abs(cap.Right - rightAtFace) <= JunctionTolerance &&
                Math.Abs(cap.Left - leftAtFace) <= JunctionTolerance;
        cap = (rightAtFace, leftAtFace);
        return true;
    }

    private readonly record struct ColumnBeamMiter(bool AtStart, double Right, double Left)
    {
        public bool Matches(ColumnBeamMiter other) => AtStart == other.AtStart &&
            Math.Abs(Right - other.Right) <= JunctionTolerance && Math.Abs(Left - other.Left) <= JunctionTolerance;
    }

    private static ColumnBeamMiter? ReadColumnBeamMiter(BeamPrism axis, BeamPrism peer, double x, double y, double halfWidth)
    {
        double at = (x - axis.X) * axis.Dx + (y - axis.Y) * axis.Dy;
        double peerAt = (x - peer.X) * peer.Dx + (y - peer.Y) * peer.Dy;
        bool atStart = at < axis.Length / 2;
        double sign = atStart ? 1 : -1, otherSign = peerAt < peer.Length / 2 ? 1 : -1;
        // The equal-width miter plane bisects the two axes directed away from the joint.
        double nx = peer.Dx * otherSign - axis.Dx * sign, ny = peer.Dy * otherSign - axis.Dy * sign;
        double normalAlong = nx * axis.Dx + ny * axis.Dy;
        if (Math.Abs(normalAlong) <= JunctionTolerance) return null;
        double offset = -halfWidth * (nx * axis.Dy - ny * axis.Dx) / normalAlong;
        double cap = atStart ? 0 : axis.Length;
        return Math.Abs(offset) < Math.Min(axis.Length, peer.Length) - JunctionTolerance
            ? new ColumnBeamMiter(atStart, cap + offset, cap - offset) : null;
    }

    private static bool MiterClearsColumn(BeamPrism axis, ColumnBeamMiter miter, double halfWidth,
        (double X0, double Y0, double X1, double Y1) section)
    {
        double end = miter.AtStart ? 0 : axis.Length;
        double slope = (miter.Right - end) / halfWidth;
        var (min, max) = ProjectRectangle(section, axis.X, axis.Y, axis.Dx, axis.Dy);
        var (skewMin, skewMax) = ProjectRectangle(section, axis.X, axis.Y,
            axis.Dx - slope * axis.Dy, axis.Dy + slope * axis.Dx);
        return Math.Min(min, skewMin) > end + JunctionTolerance || Math.Max(max, skewMax) < end - JunctionTolerance;
    }

    private static bool IsConcaveBeamSection(ReadOnlySpan<byte> vertices)
    {
        if (vertices.Length is <= 4 * 16 or > 4096 * 16) return false;
        var ring = new PlaPlanPoint[vertices.Length / 16];
        for (int i = 0; i < ring.Length; i++)
            ring[i] = new PlaPlanPoint(ReadDouble(vertices, i * 16), ReadDouble(vertices, i * 16 + 8));
        bool clockwise = false, counterclockwise = false;
        for (int i = 0; i < ring.Length; i++)
        {
            double turn = Cross(ring[i], ring[(i + 1) % ring.Length], ring[(i + 2) % ring.Length]);
            clockwise |= turn < 0;
            counterclockwise |= turn > 0;
        }
        return clockwise && counterclockwise && IsSimpleRing(ring);
    }
}
