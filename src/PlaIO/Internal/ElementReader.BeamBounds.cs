using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static Elevations? ReadBeamGeometry(ReadOnlySpan<byte> record, Guid owner, ModelGeometry geometry,
        PlaMemberProfile? profile, PlaBeamReferenceAxis? reference, double home, out PlaBoundingBox? boundingBox, out double? netVolume)
    {
        boundingBox = null;
        netVolume = null;
        if (reference is { PlanSweepRadians: not 0 } axis && profile is not null)
            return ReadCurvedBeamGeometry(record, owner, geometry, axis, profile, home, out boundingBox);

        Guid id = ReadBeamPrismMemo(record, owner, geometry);
        if (geometry.Read(id, BeamMemoClass) is not { } memo ||
            ReadStraightBeamAxis(geometry, id, memo, 0, out var planes) is not { } beam || beam.Owner != owner)
            return null;

        var hatch = ReadBeamHatch(geometry, id, memo);
        if (planes.HasSquareHorizontalEnds(beam))
        {
            boundingBox = ReadBeamBoundingBox(geometry, id, memo, beam, hatch, out netVolume);
            if (boundingBox is null && profile is not null && BeamProfileIsFullyCovered(geometry, id, memo, beam, profile))
            {
                netVolume = 0;
                return null;
            }
            return boundingBox is { } box ? CheckedElevations(box.ZMin - home, box.ZMax - home)
                : ReadBeamElevations(geometry, id, memo, beam, planes, hatch, profile, home, null, out boundingBox);
        }
        if (ProjectBeamCaps(beam, planes, hatch, profile) is not { } caps) return null;
        if (planes.Rise == 0)
            return ReadBeamElevations(geometry, id, memo, beam, planes, hatch, profile, home, caps, out boundingBox);

        uint priority = ReadBeamSectionPriority(geometry, hatch, profile);
        bool keepsVolume = true;
        bool hasButtJoint = false;
        if (!HasOnlyBeamTargetContact(memo, Guid.Empty, Guid.Empty, owner, KeepsEndPlanes)) return null;
        boundingBox = caps.Bounds;
        if (keepsVolume && planes.StartNormal + planes.EndNormal == default)
        {
            double area = profile?.Components is [{ Arcs: null } component] ? component.CrossSectionArea
                : ReadBeamRectangle(hatch) is { } rectangle ? 2 * rectangle.HalfWidth * rectangle.Depth : 0;
            double volume = area * double.Hypot(beam.Length, planes.Rise);
            if (double.IsFinite(volume) && volume > 0) netVolume = volume;
        }
        // Schedule elevations at an inclined butt joint can include a limit from the adjoining beam.
        return hasButtJoint ? null : CheckedElevations(caps.Bounds.ZMin - home, caps.Bounds.ZMax - home);

        bool KeepsEndPlanes(Guid relation, Guid junction)
        {
            if (relation == BeamColumnRelation && TryReadBeamColumnCut(geometry, id, junction, priority, out var plan))
                return plan is null || BeamPlanIsDisjoint(plan.Value, caps.Bounds);
            if (relation == BeamWallRelation || relation == BeamSlabRelation)
            {
                if (priority > 0 && BeamWinsContact(geometry, id, relation, junction, priority)) return true;
                keepsVolume = false;
                return relation == BeamWallRelation && ProfileWallLeavesBeamCaps(geometry, id, beam.Layer, junction, priority, caps);
            }
            keepsVolume = false;
            hasButtJoint = true;
            if (!IsBeamEndRelation(relation) || ReadBeamAxisJunction(geometry, id, relation, junction) is not { } joint ||
                !IsBeamEndRelation(joint.Reciprocal) || geometry.Read(joint.Peer, BeamMemoClass) is not { } peerMemo ||
                ReadStraightBeamAxis(geometry, joint.Peer, peerMemo, 0, out var peerPlanes) is not { } peer ||
                !SameIntersectionGroup(geometry, beam.Layer, peer.Layer) ||
                !BeamsMeetAtVerticalButt(beam, planes, peer, peerPlanes, joint.X, joint.Y))
                return false;

            return HasOnlyBeamTargetContact(peerMemo, joint.Reciprocal, junction, peer.Owner, KeepsButtPlane);

            bool KeepsButtPlane(Guid role, Guid otherJunction)
            {
                if (role == BeamSlabRelation || role == BeamWallRelation || role == BeamColumnRelation) return true;
                if (ReadBeamAxisJunction(geometry, joint.Peer, role, otherJunction) is not { } other) return false;
                if (other.Peer == id)
                    return role == joint.Reciprocal && other.Reciprocal == relation &&
                        Math.Abs(other.X - joint.X) <= JunctionTolerance && Math.Abs(other.Y - joint.Y) <= JunctionTolerance &&
                        HasGeometryRelation(memo, relation, otherJunction);
                if (!IsBeamEndRelation(role) || !IsOnBeamJoint(peer, other.X, other.Y, end: true) ||
                    geometry.Read(other.Peer, BeamMemoClass) is not { } beyondMemo ||
                    !HasGeometryRelation(beyondMemo, other.Reciprocal, otherJunction) ||
                    ReadBeamPrism(geometry, other.Peer, beyondMemo, 0) is not { } beyond ||
                    !SameIntersectionGroup(geometry, peer.Layer, beyond.Layer) ||
                    !IsOnBeamJoint(beyond, other.X, other.Y, IsBeamEndRelation(other.Reciprocal)) ||
                    ReadBeamContactExtents(geometry, joint.Peer, peerMemo, peer) is not { } peerSection ||
                    ReadBeamContactExtents(geometry, other.Peer, beyondMemo, beyond) is not { } outside ||
                    ProjectBeamJunctionInterval(peer, beyond, other.X, other.Y, peerSection.Left, peerSection.Right,
                        outside.Left, outside.Right) is not { } reach)
                    return false;
                double commonEnd = (joint.X - peer.X) * peer.Dx + (joint.Y - peer.Y) * peer.Dy;
                return commonEnd < reach.Min - JunctionTolerance || commonEnd > reach.Max + JunctionTolerance;
            }
        }
    }

    private static PlaBoundingBox? ReadBeamBoundingBox(ModelGeometry geometry, Guid id, byte[] memo, BeamPrism beam,
        ReadOnlySpan<byte> hatch, out double? netVolume)
    {
        netVolume = null;
        if (BeamSectionExtents(beam, hatch) is not { } section) return null;
        var rectangle = ReadBeamRectangle(hatch);
        int material = BinaryPrimitives.ReadInt32LittleEndian(hatch[18..]);
        double start = 0, stop = beam.Length;
        double startClear = 0, stopClear = beam.Length, interiorStart = beam.Length, interiorStop = 0;
        bool startJoined = false, stopJoined = false;
        double volumeLength = beam.Length;
        bool volumeSupported = true;
        if (!HasOnlyBeamTargetContact(memo, Guid.Empty, Guid.Empty, beam.Owner, ReadContact) ||
            startClear >= stopClear || interiorStart < startClear || interiorStop > stopClear)
            return null;

        var box = MakeBeamBox(beam, section, start, stop);
        if (box is not null && volumeSupported && rectangle is { } rectangular)
        {
            double value = 2 * rectangular.HalfWidth * rectangular.Depth * volumeLength;
            if (double.IsFinite(value) && value > 0) netVolume = value;
        }
        return box;

        bool ReadContact(Guid relation, Guid junction)
        {
            if (geometry.Attributes.Material.GetValueOrDefault(material) is not (> 0 and { } priority)) return false;
            if (relation == BeamSlabRelation || relation == BeamWallRelation || relation == BeamColumnRelation)
                return BeamWinsContact(geometry, id, relation, junction, priority);
            if (rectangle is not { } own || Math.Abs(beam.SinRoll) > JunctionTolerance ||
                ReadBeamAxisJunction(geometry, id, relation, junction) is not { } joint ||
                geometry.Read(joint.Peer, BeamMemoClass) is not { } otherMemo ||
                !HasGeometryRelation(otherMemo, joint.Reciprocal, junction) ||
                ReadBeamPrism(geometry, joint.Peer, otherMemo, 0) is not { } other ||
                Math.Abs(other.SinRoll) > JunctionTolerance || !SameIntersectionGroup(geometry, beam.Layer, other.Layer))
                return false;

            var otherHatch = ReadBeamHatch(geometry, joint.Peer, otherMemo);
            if (ReadBeamRectangle(otherHatch) is not { } peer ||
                geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(otherHatch[18..])) != priority ||
                Math.Abs(beam.Z - other.Z) > JunctionTolerance || Math.Abs(own.Depth - peer.Depth) > JunctionTolerance)
                return false;

            bool atEnd = IsBeamEndRelation(relation), peerEnd = IsBeamEndRelation(joint.Reciprocal);
            if (!IsOnBeamJoint(beam, joint.X, joint.Y, atEnd) || !IsOnBeamJoint(other, joint.X, joint.Y, peerEnd))
                return false;
            double along = (joint.X - beam.X) * beam.Dx + (joint.Y - beam.Y) * beam.Dy;
            double peerAlong = (joint.X - other.X) * other.Dx + (joint.Y - other.Y) * other.Dy;
            double dot = beam.Dx * other.Dx + beam.Dy * other.Dy;
            if (Math.Abs(dot) > JunctionTolerance)
            {
                // A collinear butt joint keeps both ends when the members point away from the joint.
                if (!atEnd || !peerEnd || Math.Abs(Math.Abs(dot) - 1) > JunctionTolerance ||
                    Math.Abs(own.HalfWidth - peer.HalfWidth) > JunctionTolerance ||
                    (beam.Length / 2 - along) * (other.Length / 2 - peerAlong) * dot >= 0)
                    return false;
                return SetEnd(along, 0);
            }

            if (!atEnd)
            {
                volumeSupported &= peerEnd && other.Length > own.HalfWidth + JunctionTolerance && PeerKeepsContact();
                interiorStart = Math.Min(interiorStart, along - peer.HalfWidth);
                interiorStop = Math.Max(interiorStop, along + peer.HalfWidth);
                return interiorStart > JunctionTolerance && interiorStop < beam.Length - JunctionTolerance;
            }
            // A second contact on the peer can change the face used to clean up this end.
            if (!PeerKeepsContact()) return false;
            if (peerEnd)
                return (Math.Abs(beam.Dx) <= JunctionTolerance || Math.Abs(beam.Dy) <= JunctionTolerance) &&
                    other.Length > own.HalfWidth + JunctionTolerance &&
                    SetEnd(along, -peer.HalfWidth);
            return peerAlong > own.HalfWidth + JunctionTolerance && peerAlong < other.Length - own.HalfWidth - JunctionTolerance &&
                SetEnd(along, peer.HalfWidth);

            bool PeerKeepsContact() => HasOnlyBeamTargetContact(otherMemo, joint.Reciprocal, junction, other.Owner,
                (key, contact) => (key == BeamSlabRelation || key == BeamWallRelation || key == BeamColumnRelation) &&
                    BeamWinsContact(geometry, joint.Peer, key, contact, priority));
        }

        bool SetEnd(double along, double inward)
        {
            // A symmetric miter replaces the wedge it removes. A butt cut removes the whole section.
            volumeLength -= Math.Max(0, inward);
            if (along < beam.Length / 2)
            {
                if (startJoined) return false;
                startJoined = true;
                start = inward;
                startClear = Math.Abs(inward);
            }
            else
            {
                if (stopJoined) return false;
                stopJoined = true;
                stop = beam.Length - inward;
                stopClear = beam.Length - Math.Abs(inward);
            }
            return true;
        }
    }

    private static PlaBoundingBox? MakeBeamBox(BeamPrism beam, (Elevations Height, double Left, double Right) section,
        double start, double stop)
    {
        double x0 = beam.X + beam.Dx * start, x1 = beam.X + beam.Dx * stop;
        double y0 = beam.Y + beam.Dy * start, y1 = beam.Y + beam.Dy * stop;
        double firstX = beam.Dy * section.Left, secondX = beam.Dy * section.Right;
        double firstY = -beam.Dx * section.Left, secondY = -beam.Dx * section.Right;
        return MakeBoundingBox((Math.Min(x0, x1) + Math.Min(firstX, secondX), Math.Min(y0, y1) + Math.Min(firstY, secondY),
            Math.Max(x0, x1) + Math.Max(firstX, secondX), Math.Max(y0, y1) + Math.Max(firstY, secondY)),
            section.Height.Bottom, section.Height.Top);
    }

    private static (Guid Id, byte[] Body)? ReadBeamStructuralNeighbor(ModelGeometry geometry, Guid beam, Guid relation, Guid junction)
    {
        var (kind, peerRole, peerClass) = relation == BeamSlabRelation ? (BeamSlabJunctionClass, SlabBeamRelation, SlabClass) :
            relation == BeamWallRelation ? (WallBeamJunctionClass, WallBeamRelation, WallClass) :
                (BeamColumnJunctionClass, ColumnBeamRelation, ColumnMemoClass);
        if (geometry.Read(junction, kind) is not { } contact ||
            ReadSingleRelation(contact, relation, requireSection: true) != beam)
            return null;
        Guid peer = ReadSingleRelation(contact, peerRole, requireSection: true);
        return geometry.Read(peer, peerClass) is { } body && HasGeometryRelation(body, peerRole, junction) ? (peer, body) : null;
    }

    private static bool BeamWinsContact(ModelGeometry geometry, Guid id, Guid relation, Guid junction, uint priority)
    {
        if (ReadBeamStructuralNeighbor(geometry, id, relation, junction) is not { } neighbor) return false;
        var (peer, memo) = neighbor;
        if (relation == BeamSlabRelation)
            return ReadConnectedSlabPriority(geometry, peer, junction) is > 0 and { } slab && slab <= priority;
        if (relation == BeamWallRelation)
            return ReadWallPriority(geometry, peer, memo) is > 0 and { } p && p <= priority;
        Guid owner = ReadSingleRelation(memo, ColumnMemoRelation, requireSection: true);
        return geometry.Read(owner, ColumnClass) is { } body &&
            ReadColumnPrismMemo(body.AsSpan(FindClassFrame(body, ColumnClass)), owner, geometry) == peer &&
            ReadColumnSection(memo, owner, out var junctions) is { } sectionId &&
            junctions is not null && junctions.All(c => c.Key != ColumnWallRelation) &&
            geometry.Read(sectionId, RectangularSectionClass) is { } section &&
            ReadSingleRelation(section, SectionProfileRelation, requireSection: true) == peer &&
            ReadCenteredRectangle(section, out int material) is not null &&
            geometry.Attributes.Material.GetValueOrDefault(material) is > 0 and { } column &&
            column < priority;
    }
}
