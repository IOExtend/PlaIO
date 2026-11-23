using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static bool BeamProfileIsFullyCovered(ModelGeometry geometry, Guid id, byte[] memo, BeamPrism beam, PlaMemberProfile profile)
    {
        if (ProjectBeamProfile(beam, profile) is not { } section) return false;
        var materials = geometry.Attributes.Material;
        if (HighestProfilePriority(profile) is not { } highest) return false;
        var covered = new List<(double Start, double End)>();
        var candidates = new List<(Guid Relation, Guid Junction, Guid Peer, Guid Reciprocal, byte[] Memo, PlaPlanPoint? Point)>();
        var required = (Start: 0.0, End: beam.Length);
        if (!HasOnlyBeamTargetContact(memo, Guid.Empty, Guid.Empty, beam.Owner, ReadContact)) return false;
        foreach (var (role, junction, peer, reciprocal, peerMemo, point) in candidates)
            AddCover(role, junction, peer, reciprocal, peerMemo, point);
        double through = required.Start;
        foreach (var (start, end) in covered.OrderBy(interval => interval.Start))
        {
            if (start > through + JunctionTolerance) return false;
            through = Math.Max(through, end);
            if (through >= required.End - JunctionTolerance) return true;
        }
        return false;

        uint? HighestProfilePriority(PlaMemberProfile placed)
        {
            uint maximum = 0;
            foreach (var component in placed.Components)
            {
                if (materials.GetValueOrDefault(component.BuildingMaterialIndex) is not (> 0 and { } value)) return null;
                maximum = Math.Max(maximum, value);
            }
            return maximum;
        }

        uint? HighestBeamPriority(Guid id, byte[] beamMemo)
        {
            var hatch = ReadBeamHatch(geometry, id, beamMemo);
            if (!hatch.IsEmpty) return materials.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..]));
            Guid owner = ReadSingleRelation(beamMemo, BeamMemoRelation, requireSection: true);
            return geometry.Read(owner, BeamClass) is { } body &&
                ReadMemberProfile(body.AsSpan(FindClassFrame(body, BeamClass)), owner, beam: true, geometry) is { } other
                ? HighestProfilePriority(other) : null;
        }

        bool ReadContact(Guid relation, Guid junction)
        {
            if (relation == BeamSlabRelation || relation == BeamWallRelation || relation == BeamColumnRelation)
                return ReadBeamStructuralNeighbor(geometry, id, relation, junction) is not null;
            var overlap = ReadBeamAxisJunction(geometry, id, relation, junction, withoutPoint: true);
            if ((overlap ?? ReadBeamAxisJunction(geometry, id, relation, junction)) is not { } joint ||
                geometry.Read(joint.Peer, BeamMemoClass) is not { } peerMemo ||
                !HasGeometryRelation(peerMemo, joint.Reciprocal, junction))
                return false;
            if (IsBeamEndRelation(relation))
            {
                if (ReadBeamPrism(geometry, joint.Peer, peerMemo, 0) is not { } joined ||
                    !SameIntersectionGroup(geometry, beam.Layer, joined.Layer) ||
                    !IsOnBeamJoint(beam, joint.X, joint.Y, end: true) ||
                    !IsOnBeamJoint(joined, joint.X, joint.Y, IsBeamEndRelation(joint.Reciprocal)) ||
                    ReadBeamContactExtents(geometry, joint.Peer, peerMemo, joined) is not { } tip)
                    return false;
                double at = (joint.X - beam.X) * beam.Dx + (joint.Y - beam.Y) * beam.Dy;
                if (ProjectBeamJunctionInterval(beam, joined, joint.X, joint.Y, section.Left, section.Right,
                    tip.Left, tip.Right) is not { } reach)
                {
                    double peerAt = (joint.X - joined.X) * joined.Dx + (joint.Y - joined.Y) * joined.Dy;
                    double direction = beam.Dx * joined.Dx + beam.Dy * joined.Dy;
                    return IsBeamEndRelation(joint.Reciprocal) &&
                        (beam.Length / 2 - at) * (joined.Length / 2 - peerAt) * direction < 0;
                }
                if (at < beam.Length / 2) required.Start = Math.Min(required.Start, reach.Min);
                else required.End = Math.Max(required.End, reach.Max);
                return true;
            }
            if (overlap is null && !IsOnBeamJoint(beam, joint.X, joint.Y, end: false)) return false;
            candidates.Add((relation, junction, joint.Peer, joint.Reciprocal, peerMemo,
                overlap is null ? new PlaPlanPoint(joint.X, joint.Y) : null));
            return true;
        }

        void AddCover(Guid relation, Guid junction, Guid peerId, Guid reciprocal, byte[] peerMemo, PlaPlanPoint? point)
        {
            if (ReadBeamPrism(geometry, peerId, peerMemo, 0) is not { } peer ||
                !SameIntersectionGroup(geometry, beam.Layer, peer.Layer) || Math.Abs(peer.SinRoll) > JunctionTolerance ||
                point is { } contact && (IsBeamEndRelation(reciprocal) || !IsOnBeamJoint(peer, contact.X, contact.Y, end: false)))
                return;

            var hatch = ReadBeamHatch(geometry, peerId, peerMemo);
            if (ReadBeamRectangle(hatch) is not { } rectangle ||
                materials.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..])) is not { } priority ||
                priority <= highest ||
                section.Height.Bottom < peer.Z - rectangle.Depth - JunctionTolerance || section.Height.Top > peer.Z + JunctionTolerance)
                return;

            // Find the stations at which the peer contains the whole transverse section.
            double dx = beam.X - peer.X, dy = beam.Y - peer.Y;
            double along = dx * peer.Dx + dy * peer.Dy, across = dx * peer.Dy - dy * peer.Dx;
            double cos = beam.Dx * peer.Dx + beam.Dy * peer.Dy, sin = beam.Dx * peer.Dy - beam.Dy * peer.Dx;
            double near = Math.Min(-sin * section.Left, -sin * section.Right);
            double far = Math.Max(-sin * section.Left, -sin * section.Right);
            var interval = required;
            var cuts = new List<(double Start, double End)>();
            if (!LimitBeamStations(ref interval, along, cos, -near, peer.Length - far) ||
                !LimitBeamStations(ref interval, across, sin,
                    -rectangle.HalfWidth - Math.Min(cos * section.Left, cos * section.Right),
                    rectangle.HalfWidth - Math.Max(cos * section.Left, cos * section.Right)) ||
                !HasOnlyBeamTargetContact(peerMemo, reciprocal, junction, peer.Owner, KeepsCover))
                return;

            double start = interval.Start;
            foreach (var cut in cuts.OrderBy(c => c.Start))
            {
                if (cut.Start > start + JunctionTolerance) covered.Add((start, cut.Start));
                start = Math.Max(start, cut.End);
            }
            if (start < interval.End - JunctionTolerance) covered.Add((start, interval.End));

            bool AddCut(double low, double high)
            {
                var affected = interval;
                if (LimitBeamStations(ref affected, along, cos, low - far, high - near))
                    cuts.Add(affected);
                return true;
            }

            bool KeepsCover(Guid key, Guid otherJunction)
            {
                if (key == BeamSlabRelation || key == BeamWallRelation || key == BeamColumnRelation)
                {
                    if (BeamWinsContact(geometry, peerId, key, otherJunction, priority)) return true;
                    if (MakeBeamBox(beam, section, required.Start, required.End) is not { } target) return false;
                    if (key == BeamSlabRelation && ReadBeamSlabCutBox(geometry, peerId, otherJunction, section.Height, out var slab))
                        return slab is null || AddPlanCut((slab.XMin, slab.YMin, slab.XMax, slab.YMax));
                    return key == BeamColumnRelation &&
                        TryReadBeamColumnCut(geometry, peerId, otherJunction, priority, out var plan) &&
                        (plan is null || AddPlanCut(plan.Value));

                    bool AddPlanCut((double X0, double Y0, double X1, double Y1) plan)
                    {
                        if (BeamPlanIsDisjoint(plan, target)) return true;
                        var span = ProjectRectangle(plan, peer.X, peer.Y, peer.Dx, peer.Dy);
                        return AddCut(span.Min, span.Max);
                    }
                }

                if (ReadBeamAxisJunction(geometry, peerId, key, otherJunction, withoutPoint: true) is { } overlap)
                {
                    if (overlap.Peer == id)
                        return overlap.Reciprocal == relation && key == reciprocal &&
                            HasGeometryRelation(memo, relation, otherJunction);
                    if (geometry.Read(overlap.Peer, BeamMemoClass) is not { } overlapMemo ||
                        !HasGeometryRelation(overlapMemo, overlap.Reciprocal, otherJunction))
                        return false;
                    return HighestBeamPriority(overlap.Peer, overlapMemo) is > 0 and { } p && p < priority;
                }

                // A join outside the covered interval can only affect the part of the prism within its peer's strip.
                if (ReadBeamAxisJunction(geometry, peerId, key, otherJunction) is not { } endpoint ||
                    geometry.Read(endpoint.Peer, BeamMemoClass) is not { } endMemo ||
                    !HasGeometryRelation(endMemo, endpoint.Reciprocal, otherJunction))
                    return false;
                if (endpoint.Peer == id)
                    return point is not null && key == reciprocal && endpoint.Reciprocal == relation &&
                        IsOnBeamJoint(beam, endpoint.X, endpoint.Y, end: false) &&
                        IsOnBeamJoint(peer, endpoint.X, endpoint.Y, end: false);
                if (ReadBeamPrism(geometry, endpoint.Peer, endMemo, 0) is not { } endPeer ||
                    !SameIntersectionGroup(geometry, peer.Layer, endPeer.Layer) ||
                    !IsOnBeamJoint(peer, endpoint.X, endpoint.Y, IsBeamEndRelation(key)) ||
                    !IsOnBeamJoint(endPeer, endpoint.X, endpoint.Y, IsBeamEndRelation(endpoint.Reciprocal)) ||
                    ReadBeamContactExtents(geometry, endpoint.Peer, endMemo, endPeer) is not { } endSection)
                    return false;
                if (!IsBeamEndRelation(key) && (DisjointHeights(endSection.Height.Bottom, endSection.Height.Top, section.Height) ||
                    HighestBeamPriority(endpoint.Peer, endMemo) is > 0 and { } p3 && p3 < priority))
                    return true;
                if (ProjectBeamJunctionInterval(peer, endPeer, endpoint.X, endpoint.Y, -rectangle.HalfWidth,
                    rectangle.HalfWidth, endSection.Left, endSection.Right) is { } reach)
                    return AddCut(reach.Min, reach.Max);
                double peerAt = (endpoint.X - peer.X) * peer.Dx + (endpoint.Y - peer.Y) * peer.Dy;
                double otherAt = (endpoint.X - endPeer.X) * endPeer.Dx + (endpoint.Y - endPeer.Y) * endPeer.Dy;
                double direction = peer.Dx * endPeer.Dx + peer.Dy * endPeer.Dy;
                return IsBeamEndRelation(key) && IsBeamEndRelation(endpoint.Reciprocal) &&
                    (peer.Length / 2 - peerAt) * (endPeer.Length / 2 - otherAt) * direction < 0;
            }
        }
    }

    private static bool LimitBeamStations(ref (double Start, double End) interval,
        double origin, double slope, double min, double max)
    {
        if (!(min <= max + JunctionTolerance)) return false;
        if (slope == 0) return origin >= min - JunctionTolerance && origin <= max + JunctionTolerance;
        double a = (min - origin) / slope, b = (max - origin) / slope;
        interval = (Math.Max(interval.Start, Math.Min(a, b)), Math.Min(interval.End, Math.Max(a, b)));
        return interval.Start <= interval.End;
    }
}
