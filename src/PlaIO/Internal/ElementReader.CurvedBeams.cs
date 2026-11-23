using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid ElementAssemblyRelation = new("68236820-e176-4e8c-be39-d5d56a7a71c0");
    private static readonly Guid ElementAssemblyClass = new("8f2fee46-6a3f-41ac-a32b-69e44dc6276d");
    private static readonly Guid ElementAssemblyMemberClass = new("47a28b23-f655-49fa-b219-1511ca52c49a");
    private static readonly Guid AttachedAnnotationClass = new("5793eecb-32e2-4fdd-b216-739ff6c806c4");
    private static readonly Guid AttachedAnnotationRelation = new("386bc4fb-ea34-4287-8067-4f16b966b7af");
    private static readonly Guid AngularDimensionClass = new("2120198c-d317-4cea-ab4e-c7b6ceb5156e");
    private static readonly HashSet<Guid> AngularDimensionAnchorRelations =
    [
        new("7469b782-0846-4120-b41e-e3a2f7ca9376"), new("1ff9df7b-3f2d-4f16-855a-b863af9ca26d"),
        new("13fe778c-0592-4d95-a788-f5e0fed3bcbe"), new("b3ffaf20-1c37-4090-b8f3-9e65bd5fa244"),
    ];

    private static Guid ReadCurvedBeamMemo(ReadOnlySpan<byte> record, Guid owner, ModelGeometry geometry) =>
        ReadPrismMemo(record, BeamMemoRelation, (group, member) => IsUntransformedAssemblyMember(geometry, owner, group, member),
            (role, target) => IsPrismAnnotation(geometry, owner, role, target, allowLinearDimension: false));

    private static bool IsPrismAnnotation(ModelGeometry geometry, Guid owner, Guid role, Guid target, bool allowLinearDimension) =>
        role == AttachedAnnotationRelation ? allowLinearDimension && IsAttachedAnnotation(geometry, owner, target) :
            AngularDimensionAnchorRelations.Contains(role) && geometry.Read(target, AngularDimensionClass) is { } dimension &&
            ReadSingleRelation(dimension, role, requireSection: true) == owner;

    private static bool IsAttachedAnnotation(ModelGeometry geometry, Guid owner, Guid target) =>
        geometry.Read(target, AttachedAnnotationClass) is { } annotation &&
        ReadSingleRelation(annotation, AttachedAnnotationRelation, requireSection: true) == owner;

    private static bool IsUntransformedAssemblyMember(ModelGeometry geometry, Guid owner, Guid group, Guid member)
    {
        if (geometry.Read(group, ElementAssemblyClass) is not { } parent ||
            !HasGeometryRelation(parent, ElementAssemblyRelation, owner) ||
            geometry.Read(member, ElementAssemblyMemberClass) is not { Length: 144 } child ||
            FindRelationshipSection(child, child.Length) != 90 ||
            ReadDouble(child, 60) != 0 || ReadDouble(child, 68) != 0 || ReadDouble(child, 76) != 1)
            return false;
        var link = child.AsSpan(90);
        return link[0] == 4 && new Guid(link.Slice(1, 16)) == ElementAssemblyRelation &&
            BinaryPrimitives.ReadUInt32LittleEndian(link[17..]) == 2 &&
            new Guid(link.Slice(21, 16)) == group && new Guid(link.Slice(37, 16)) == owner && link[53] == 0;
    }

    private static Elevations? ReadCurvedBeamGeometry(ReadOnlySpan<byte> record, Guid owner, ModelGeometry geometry,
        PlaBeamReferenceAxis reference, PlaMemberProfile profile, double home, out PlaBoundingBox? box)
    {
        box = null;
        Guid id = ReadCurvedBeamMemo(record, owner, geometry);
        if (geometry.Read(id, BeamMemoClass) is not { } memo ||
            ReadBeamAxisFrame(record, owner, geometry) is not { } frame ||
            ReadCircularEdge(reference.Start, reference.End, reference.PlanSweepRadians, out _) is not { } arc)
            return null;

        var axis = frame.Axis with { Z = reference.StartElevationToProjectZero };
        if (Math.Abs(axis.SinRoll) > JunctionTolerance || ProjectBeamProfile(axis, profile) is not { } section) return null;
        double radialSign = -Math.Sign(arc.Sweep);
        double u0 = section.Left, u1 = section.Right;
        if (Math.Min(arc.Radius + radialSign * u0, arc.Radius + radialSign * u1) <= JunctionTolerance) return null;
        var neighbors = new (Guid Id, BeamArcCap Cap)?[2];
        var slabs = new Dictionary<Guid, CurvedBeamSlab>();
        uint priority = ReadBeamSectionPriority(geometry, default, profile);
        if (!HasOnlyBeamTargetContact(memo, Guid.Empty, Guid.Empty, owner, ReadContact)) return null;

        var start = neighbors[0]?.Cap ?? BeamArcCap.Radial(arc, arc.Start, radialSign);
        var end = neighbors[1]?.Cap ?? BeamArcCap.Radial(arc, arc.End, radialSign);
        double angularRoom = Math.Min(Math.Abs(arc.Sweep), Math.Tau - Math.Abs(arc.Sweep)) / 2;
        if (!start.StaysNear(arc.Start, angularRoom, u0, u1) || !end.StaysNear(arc.End, angularRoom, u0, u1))
            return null;
        if (slabs.Values.All(s => s.Band.Bottom > section.Height.Bottom + JunctionTolerance &&
                                 s.Band.Top < section.Height.Top - JunctionTolerance))
        {
            box = CurvedBeamPlan(arc, start, end, u0, u1, section.Height, PointSurvives);
            return CheckedElevations(section.Height.Bottom - home, section.Height.Top - home);
        }
        return null;

        bool ReadContact(Guid relation, Guid junction)
        {
            if (relation == BeamSlabRelation)
                return ReadSlab(id, junction);
            if (!IsBeamEndRelation(relation) ||
                ReadBeamAxisJunction(geometry, id, relation, junction) is not { } joint ||
                !IsBeamEndRelation(joint.Reciprocal) ||
                geometry.Read(joint.Peer, BeamMemoClass) is not { } peerMemo ||
                !HasGeometryRelation(peerMemo, joint.Reciprocal, junction))
                return false;

            var point = new PlaPlanPoint(joint.X, joint.Y);
            int side = BeamEndAt(reference, point, arc);
            if (side < 0) return false;
            if (neighbors[side] is { } saved) return saved.Id == joint.Peer;

            Guid peerOwner = ReadSingleRelation(peerMemo, BeamMemoRelation, requireSection: true);
            if (geometry.Read(peerOwner, BeamClass) is not { } peerBody) return false;
            var peer = peerBody.AsSpan(FindClassFrame(peerBody, BeamClass));
            if (ReadCurvedBeamMemo(peer, peerOwner, geometry) != joint.Peer ||
                ReadBeamAxisFrame(peer, peerOwner, geometry) is not { } peerFrame ||
                !SameIntersectionGroup(geometry, axis.Layer, peerFrame.Axis.Layer) ||
                ReadBeamReferenceAxis(peer, peerOwner, geometry) is not { } other ||
                !BeamEndsMeet(reference, side, other, point) ||
                Math.Abs(other.StartElevationToProjectZero - axis.Z) > JunctionTolerance ||
                Math.Abs(other.EndElevationToProjectZero - axis.Z) > JunctionTolerance ||
                Math.Abs(peerFrame.Axis.SinRoll - axis.SinRoll) > JunctionTolerance ||
                ReadMemberProfile(peer, peerOwner, beam: true, geometry) is not { } otherProfile ||
                !SameBeamSection(profile, otherProfile) ||
                !HasOnlyBeamTargetContact(peerMemo, joint.Reciprocal, junction, peerOwner, CheckPeerContact))
                return false;

            var peerNormal = side == 0 ? peerFrame.Planes.EndNormal : peerFrame.Planes.StartNormal;
            var ownNormal = side == 0 ? frame.Planes.StartNormal : frame.Planes.EndNormal;
            if (other.PlanSweepRadians == 0 &&
                (!AlongAxis(peerNormal with { Z = 0 }, (side == 0 ? 1 : -1) * peerFrame.Axis.Dx,
                    (side == 0 ? 1 : -1) * peerFrame.Axis.Dy) || Math.Abs(peerNormal.Z) > JunctionTolerance) ||
                ownNormal.Dot(peerNormal) >= 0)
                return false;
            var nominal = side == 0 ? arc.Start : arc.End;
            var cap = Distance(point, nominal) > JunctionTolerance
                ? BeamArcCap.Radial(arc, nominal, radialSign)
                : BeamArcCap.AtJoint(arc, other, nominal, side, ownNormal, peerNormal);
            if (cap is null) return false;
            neighbors[side] = (joint.Peer, cap.Value);
            return true;

            bool CheckPeerContact(Guid role, Guid otherJunction)
            {
                if (role == BeamSlabRelation) return ReadSlab(joint.Peer, otherJunction);
                if (!IsBeamEndRelation(role) ||
                    ReadBeamAxisJunction(geometry, joint.Peer, role, otherJunction) is not { } link ||
                    !IsBeamEndRelation(link.Reciprocal)) return false;
                var contact = new PlaPlanPoint(link.X, link.Y);
                if (link.Peer == id)
                    return BeamEndsMeet(reference, side, other, contact) && role == joint.Reciprocal &&
                        HasGeometryRelation(memo, relation, otherJunction);
                if (BeamEndAt(other, contact) == side) return true;
                if (geometry.Read(link.Peer, BeamMemoClass) is not { } farMemo ||
                    !HasGeometryRelation(farMemo, link.Reciprocal, otherJunction))
                    return false;
                Guid farOwner = ReadSingleRelation(farMemo, BeamMemoRelation, requireSection: true);
                if (geometry.Read(farOwner, BeamClass) is not { } farBody ||
                    ReadBeamReferenceAxis(farBody.AsSpan(FindClassFrame(farBody, BeamClass)), farOwner, geometry) is not { } far ||
                    far.PlanSweepRadians == 0 ||
                    ReadCircularEdge(far.Start, far.End, far.PlanSweepRadians, out _) is not { } farArc ||
                    BeamEndAt(far, contact, farArc) != 1 - side)
                    return false;
                return BeamEndsMeet(far, 1 - side, other, contact);
            }
        }

        bool ReadSlab(Guid beam, Guid junction)
        {
            if (geometry.Read(junction, BeamSlabJunctionClass) is not { } contact ||
                ReadSingleRelation(contact, BeamSlabRelation, requireSection: true) != beam)
                return false;
            Guid slab = ReadSingleRelation(contact, SlabBeamRelation, requireSection: true);
            if (geometry.Read(slab, SlabClass) is not { } body || !HasGeometryRelation(body, SlabBeamRelation, junction))
                return false;
            if (slabs.ContainsKey(slab)) return true;
            var framed = body.AsSpan(FindClassFrame(body, SlabClass));
            var location = ReadElementLocation(framed, "Slab", 0);
            if (!SameIntersectionGroup(geometry, axis.Layer, location.LayerIndex) ||
                !HasSupportedSlabContacts(framed, Guid.Empty, Guid.Empty, IsKnownContact,
                    (group, member) => IsUntransformedAssemblyMember(geometry, slab, group, member)))
                return false;
            var settings = AfterCountedString(framed, ClassElementIdOffset("Slab"));
            if (priority > 0 && ReadSlabPriority(settings, geometry.Attributes) is { } p && p < priority) return true;
            if (ReadElevations(framed, "Slab") is not { } planes ||
                ReadFloorBox(framed, "Slab", planes, geometry.StoryElevation(location.HomeStoryIndex))
                    is not { } bounds)
                return false;
            if (DisjointHeights(bounds.ZMin, bounds.ZMax, section.Height)) return true;
            var outline = ReadSlabOutline(settings);
            if (ReadAfterVerticalSlabEdges(outline).IsEmpty) return false;
            var plan = BinaryPrimitives.ReadUInt32LittleEndian(outline[47..]) == 0
                ? ReadStraightRings(outline, out _) is { } rings ? new PlaSlabGeometry(rings, bounds.ZMin, bounds.ZMax, 0, null) : null
                : ReadCurvedSlabGeometry(outline, bounds);
            if (plan is null) return false;
            slabs.Add(slab, new CurvedBeamSlab(new Elevations(bounds.ZMin, bounds.ZMax), plan));
            return true;

            bool IsKnownContact(Guid key, Guid target)
            {
                if (key == AttachedAnnotationRelation)
                    return IsAttachedAnnotation(geometry, slab, target);
                if (key != SlabBeamRelation || geometry.Read(target, BeamSlabJunctionClass) is not { } link ||
                    ReadSingleRelation(link, key, requireSection: true) != slab)
                    return false;
                Guid neighbor = ReadSingleRelation(link, BeamSlabRelation, requireSection: true);
                return geometry.Read(neighbor, BeamMemoClass) is { } neighborMemo &&
                    HasGeometryRelation(neighborMemo, BeamSlabRelation, target);
            }
        }

        bool PointSurvives(PlaPlanPoint point, double u)
        {
            var cutters = slabs.Values.Where(s => s.MayContain(point)).ToArray();
            if (cutters.Length == 0) return true;
            foreach (var component in profile.Components)
            {
                var ring = component.Rings[0];
                for (int i = 0; i < ring.Count; i++)
                {
                    var p = ring[i];
                    var q = ring[(i + 1) % ring.Count];
                    double a = axis.CosRoll * p.U - axis.SinRoll * p.Z;
                    double b = axis.CosRoll * q.U - axis.SinRoll * q.Z;
                    double z0 = axis.Z + axis.SinRoll * p.U + axis.CosRoll * p.Z;
                    double z1 = axis.Z + axis.SinRoll * q.U + axis.CosRoll * q.Z;
                    if (u == a && FreeAt(z0)) return true;
                    if ((u > Math.Min(a, b) && u < Math.Max(a, b)) &&
                        !(component.Arcs?.Any(edge => edge.RingIndex == 0 && edge.StartVertexIndex == i) ?? false) &&
                        FreeAt(z0 + (z1 - z0) * ((u - a) / (b - a))))
                        return true;
                }
            }
            return false;

            bool FreeAt(double z) => cutters.All(s => z < s.Band.Bottom - JunctionTolerance || z > s.Band.Top + JunctionTolerance);
        }
    }

    private static bool SameBeamSection(PlaMemberProfile a, PlaMemberProfile b) =>
        a.ProfileIndex == b.ProfileIndex && a.Components.Count == b.Components.Count &&
        a.Components.Zip(b.Components).All(p => p.First.BuildingMaterialIndex == p.Second.BuildingMaterialIndex &&
            p.First.Rings.Count == p.Second.Rings.Count &&
            p.First.Rings.Zip(p.Second.Rings).All(r => r.First.SequenceEqual(r.Second)) &&
            (p.First.Arcs ?? []).SequenceEqual(p.Second.Arcs ?? []));

    private static int BeamEndAt(PlaBeamReferenceAxis axis, PlaPlanPoint point, SlabEdge? arc = null)
    {
        bool start = Matches(axis.Start, outward: -Math.Sign(axis.PlanSweepRadians));
        bool end = Matches(axis.End, outward: Math.Sign(axis.PlanSweepRadians));
        return start == end ? -1 : start ? 0 : 1;

        bool Matches(PlaPlanPoint end, int outward)
        {
            double gap = Distance(end, point);
            if (gap <= JunctionTolerance) return true;
            // Near tangency the saved extra intersection can fall beyond the end of the drawn arc.
            // Its tangential displacement must fit within the sagitta of the coordinate tolerance.
            return arc is { } edge && gap * gap <= 2 * edge.Radius * JunctionTolerance &&
                Math.Abs(Distance(edge.Center, point) - edge.Radius) <= JunctionTolerance &&
                outward * Cross(edge.Center, end, point) > 0;
        }
    }

    private static bool BeamEndsMeet(PlaBeamReferenceAxis curved, int side, PlaBeamReferenceAxis peer, PlaPlanPoint joint)
    {
        var a = side == 0 ? curved.Start : curved.End;
        var b = side == 0 ? peer.End : peer.Start;
        if (Distance(a, b) > JunctionTolerance) return false;
        if (Distance(b, joint) <= JunctionTolerance) return true;
        double length = Distance(peer.Start, peer.End);
        return peer.PlanSweepRadians == 0 && Math.Abs(Cross(peer.Start, peer.End, joint)) <= JunctionTolerance * length;
    }

    private readonly record struct CurvedBeamSlab(Elevations Band, PlaSlabGeometry Section)
    {
        public bool MayContain(PlaPlanPoint point)
        {
            var ring = Section.Rings[0];
            if (Section.Arcs is not { Count: > 0 })
                return ContainsPoint(ring, point) || PointOnOpeningBoundary(ring, point);
            var edges = new SlabEdge[ring.Count];
            for (int i = 0; i < ring.Count; i++) edges[i] = new(ring[i], ring[(i + 1) % ring.Count], 0);
            foreach (var item in Section.Arcs)
            {
                if (item.RingIndex != 0) continue;
                var chord = edges[item.StartVertexIndex];
                if (ReadCircularEdge(chord.Start, chord.End, item.SweepRadians, out _) is not { } edge) return true;
                if (Math.Abs(Distance(edge.Center, point) - edge.Radius) <= JunctionTolerance &&
                    edge.IncludesDirection(point)) return true;
                edges[item.StartVertexIndex] = edge;
            }
            return CurvedRingContainsPoint(ring, edges, point) || PointOnOpeningBoundary(ring, point);
        }
    }
}
