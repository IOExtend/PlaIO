using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static Elevations? ReadBeamElevations(ModelGeometry geometry, Guid id, byte[] memo, BeamPrism beam, BeamEndPlanes planes,
        ReadOnlySpan<byte> hatch, PlaMemberProfile? profile, double home, BeamCaps? caps, out PlaBoundingBox? boundingBox)
    {
        boundingBox = null;
        var extents = profile is null ? BeamSectionExtents(beam, hatch) : ProjectBeamProfile(beam, profile);
        if (extents is not { } section) return null;
        uint priority = ReadBeamSectionPriority(geometry, hatch, profile);
        bool keepsPlan = true;
        var blocked = new List<(double Start, double End)>();
        if (!HasOnlyBeamTargetContact(memo, Guid.Empty, Guid.Empty, beam.Owner, ReadContact)) return null;
        // A full transverse section outside the union of all possible cuts retains both vertical extrema.
        double covered = caps?.FullSectionStart ?? 0;
        foreach (var (start, end) in blocked.OrderBy(c => c.Start))
        {
            if (start > covered + JunctionTolerance) break;
            covered = Math.Max(covered, end);
        }
        if (covered >= (caps?.FullSectionEnd ?? beam.Length) - JunctionTolerance) return null;
        if (keepsPlan)
            boundingBox = caps?.Bounds ?? MakeBeamBox(beam, section, 0, beam.Length);
        return CheckedElevations(section.Height.Bottom - home, section.Height.Top - home);

        bool ReadContact(Guid relation, Guid junction)
        {
            if (relation == BeamWallRelation || relation == BeamSlabRelation || relation == BeamColumnRelation)
            {
                if (priority > 0 && BeamWinsContact(geometry, id, relation, junction, priority)) return true;
                if (relation == BeamWallRelation) return false;
                return ReadStructuralContact(relation, junction);
            }

            if (ReadBeamAxisJunction(geometry, id, relation, junction) is not { } joint ||
                geometry.Read(joint.Peer, BeamMemoClass) is not { } otherMemo ||
                ReadStraightBeamAxis(geometry, joint.Peer, otherMemo, 0, out var otherPlanes) is not { } other ||
                !otherPlanes.StartNormal.IsUnit || !otherPlanes.EndNormal.IsUnit ||
                !HasOnlyBeamTargetContact(otherMemo, joint.Reciprocal, junction, other.Owner,
                    (key, _) => IsBeamEndRelation(key) || key == FirstBeamCollisionRelation || key == SecondBeamCollisionRelation ||
                        key == BeamSlabRelation || key == BeamWallRelation || key == BeamColumnRelation) ||
                !SameIntersectionGroup(geometry, beam.Layer, other.Layer) ||
                !IsOnBeamJoint(beam, joint.X, joint.Y, IsBeamEndRelation(relation)) ||
                !IsOnBeamJoint(other, joint.X, joint.Y, IsBeamEndRelation(joint.Reciprocal)) ||
                ReadBeamContactExtents(geometry, joint.Peer, otherMemo, other) is not { } outline)
                return false;

            double slope = beam.Dx * other.Dy - beam.Dy * other.Dx;
            double across = beam.Dx * other.Dx + beam.Dy * other.Dy;
            double along = (joint.X - beam.X) * beam.Dx + (joint.Y - beam.Y) * beam.Dy;
            if (Math.Abs(slope) <= JunctionTolerance)
            {
                double peerAlong = (joint.X - other.X) * other.Dx + (joint.Y - other.Y) * other.Dy;
                if (!IsBeamEndRelation(relation) || !IsBeamEndRelation(joint.Reciprocal) ||
                    (beam.Length / 2 - along) * (other.Length / 2 - peerAlong) * across >= 0)
                    return false;
                if (!planes.HasSquareHorizontalEnds(beam) || !otherPlanes.HasSquareHorizontalEnds(other))
                {
                    keepsPlan = false;
                    return BeamsMeetAtVerticalButt(beam, planes, other, otherPlanes, joint.X, joint.Y);
                }
                double left = across > 0 ? outline.Left : -outline.Right;
                double right = across > 0 ? outline.Right : -outline.Left;
                keepsPlan &= Math.Abs(section.Left - left) <= JunctionTolerance && Math.Abs(section.Right - right) <= JunctionTolerance &&
                    Math.Abs(section.Height.Bottom - outline.Height.Bottom) <= JunctionTolerance &&
                    Math.Abs(section.Height.Top - outline.Height.Top) <= JunctionTolerance;
                return true;
            }
            keepsPlan = false;
            return ProjectBeamJunctionInterval(beam, other, joint.X, joint.Y, section.Left, section.Right,
                outline.Left, outline.Right) is { } reach && Block(reach.Min, reach.Max);
        }

        bool ReadStructuralContact(Guid relation, Guid junction)
        {
            (double X0, double Y0, double X1, double Y1)? plan;
            if (relation == BeamSlabRelation)
            {
                if (!ReadBeamSlabCutBox(geometry, id, junction, section.Height, out var slab)) return false;
                plan = slab is { } box ? (box.XMin, box.YMin, box.XMax, box.YMax) : null;
            }
            else if (!TryReadBeamColumnCut(geometry, id, junction, priority, out plan)) return false;

            if (plan is not { } rectangle) return true;
            var transverse = ProjectRectangle(rectangle, beam.X, beam.Y, beam.Dy, -beam.Dx);
            if (transverse.Max <= section.Left || transverse.Min >= section.Right) return true;
            var axial = ProjectRectangle(rectangle, beam.X, beam.Y, beam.Dx, beam.Dy);
            keepsPlan &= caps is { } saved ? BeamPlanIsDisjoint(rectangle, saved.Bounds)
                : axial.Max <= 0 || axial.Min >= beam.Length;
            return Block(axial.Min, axial.Max);
        }

        bool Block(double start, double end)
        {
            if (!double.IsFinite(start) || !double.IsFinite(end)) return false;
            blocked.Add((Math.Min(start, end), Math.Max(start, end)));
            return true;
        }
    }

    private static bool ReadBeamSlabCutBox(ModelGeometry geometry, Guid id, Guid junction, Elevations height,
        out PlaBoundingBox? bounds)
    {
        bounds = null;
        if (ReadBeamStructuralNeighbor(geometry, id, BeamSlabRelation, junction) is not { } slab)
            return false;
        var framed = slab.Body.AsSpan(FindClassFrame(slab.Body, SlabClass));
        if (!HasSupportedSlabContacts(framed, Guid.Empty, Guid.Empty,
            (key, _) => key == SlabBeamRelation || key == SlabColumnRelation || key == SlabWallRelation ||
                key == SlabLevelDimensionRelation || key == CutElementRelation || key == OpeningHostRelation) ||
            ReadElevations(framed, "Slab") is not { } planes ||
            ReadFloorBox(framed, "Slab", planes, geometry.StoryElevation(ReadElementLocation(framed, "Slab", 0).HomeStoryIndex))
                is not { } box)
            return false;
        if (DisjointHeights(box.ZMin, box.ZMax, height)) return true;
        var outline = ReadSlabOutline(AfterCountedString(framed, ClassElementIdOffset("Slab")));
        if (ReadAfterVerticalSlabEdges(outline).IsEmpty) return false;
        bounds = box;
        return true;
    }

    private static uint ReadBeamSectionPriority(ModelGeometry geometry, ReadOnlySpan<byte> hatch, PlaMemberProfile? profile) =>
        profile is null ? geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..])) ?? 0
            : profile.Components.Min(c => geometry.Attributes.Material.GetValueOrDefault(c.BuildingMaterialIndex) ?? 0);

    private static (double Min, double Max)? ProjectBeamJunctionInterval(BeamPrism axis, BeamPrism peer, double x, double y,
        double left, double right, double peerLeft, double peerRight)
    {
        double slope = axis.Dx * peer.Dy - axis.Dy * peer.Dx;
        if (Math.Abs(slope) <= JunctionTolerance) return null;
        // The peer's strip bounds its reach even when the joint changes its length.
        double across = axis.Dx * peer.Dx + axis.Dy * peer.Dy;
        double along = (x - axis.X) * axis.Dx + (y - axis.Y) * axis.Dy;
        double a = along + (peerLeft - Math.Max(left * across, right * across)) / slope;
        double b = along + (peerRight - Math.Min(left * across, right * across)) / slope;
        return double.IsFinite(a) && double.IsFinite(b) ? (Math.Min(a, b), Math.Max(a, b)) : null;
    }

    private static bool TryReadBeamColumnCut(ModelGeometry geometry, Guid beamId, Guid junction, uint priority,
        out (double X0, double Y0, double X1, double Y1)? plan, bool allowVerifiedNonWrappingWalls = false)
    {
        plan = null;
        if (geometry.Read(junction, BeamColumnJunctionClass) is not { } contact ||
            ReadSingleRelation(contact, BeamColumnRelation, requireSection: true) != beamId)
            return false;
        Guid peer = ReadSingleRelation(contact, ColumnBeamRelation, requireSection: true);
        var hatch = ReadColumnContactSection(geometry, peer, ColumnBeamRelation, junction, out var column);
        if (hatch.IsEmpty || geometry.Read(peer, ColumnMemoClass) is not { } memo ||
            ReadColumnSection(memo, ReadSingleRelation(memo, ColumnMemoRelation, requireSection: true),
                out var contacts) is not { } sectionId)
            return false;
        // Complex-profile columns do not receive wrapping from wall skins.
        if (geometry.Read(sectionId, ComplexSectionClass) is null &&
            contacts?.Any(c => c.Key == ColumnWallRelation && (!allowVerifiedNonWrappingWalls || !WallKeepsColumnOutline(c.Id))) == true)
            return false;
        int material = BinaryPrimitives.ReadInt32LittleEndian(hatch[18..]);
        if (geometry.Attributes.Material.GetValueOrDefault(material) is > 0 and { } p && p < priority)
            return true;
        var settings = AfterCountedString(column, 735);
        if (settings.Length < 95 || ReadDouble(settings, 28) != 0) return false;
        plan = ReadColumnFootprint(column, hatch, ReadDouble(settings, 87));
        return plan is not null;

        bool WallKeepsColumnOutline(Guid id)
        {
            if (geometry.Read(id, WallColumnJunctionClass) is not { } joint ||
                ReadSingleRelation(joint, ColumnWallRelation, requireSection: true) != peer ||
                geometry.Read(ReadSingleRelation(joint, WallColumnRelation, requireSection: true), WallClass) is not { } wall ||
                !HasGeometryRelation(wall, WallColumnRelation, id))
                return false;
            var wallSettings = ReadWallSettings(wall);
            return wallSettings.Length >= 231 && BinaryPrimitives.ReadUInt16LittleEndian(wallSettings[225..]) is 1 or 2 or 3 &&
                !WallMayWrapColumn(geometry.Attributes, wallSettings);
        }
    }

    private static bool BeamPlanIsDisjoint((double X0, double Y0, double X1, double Y1) plan, PlaBoundingBox beam) =>
        plan.X1 <= beam.XMin || plan.X0 >= beam.XMax || plan.Y1 <= beam.YMin || plan.Y0 >= beam.YMax;

    private static (Elevations Height, double Left, double Right)? ReadBeamContactExtents(ModelGeometry geometry,
        Guid id, byte[] memo, BeamPrism beam)
    {
        if (BeamSectionExtents(beam, ReadBeamHatch(geometry, id, memo)) is { } straight) return straight;
        if (geometry.Read(beam.Owner, BeamClass) is not { } body) return null;
        var framed = body.AsSpan(FindClassFrame(body, BeamClass));
        return ReadMemberProfile(framed, beam.Owner, beam: true, geometry) is { } profile
            ? ProjectBeamProfile(beam, profile) : null;
    }

    private static (Elevations Height, double Left, double Right)? ProjectBeamProfile(BeamPrism beam, PlaMemberProfile profile)
    {
        double z0 = double.PositiveInfinity, z1 = double.NegativeInfinity;
        double t0 = double.PositiveInfinity, t1 = double.NegativeInfinity;
        double vertical = Math.Atan2(beam.CosRoll, beam.SinRoll);
        foreach (var component in profile.Components)
        {
            var ring = component.Rings[0];
            foreach (var point in ring) Include(point.U, point.Z);
            foreach (var arc in component.Arcs ?? [])
            {
                if (arc.RingIndex != 0) continue;
                var p = ring[arc.StartVertexIndex];
                var q = ring[(arc.StartVertexIndex + 1) % ring.Count];
                if (ReadCircularEdge(new(p.U, p.Z), new(q.U, q.Z), arc.SweepRadians, out _) is not { } edge) return null;
                for (int i = 0; i < 4; i++)
                {
                    double angle = vertical + i * Math.PI / 2;
                    if (edge.IncludesAngle(angle))
                    {
                        var point = edge.AtAngle(angle);
                        Include(point.X, point.Y);
                    }
                }
            }
        }
        return z1 > z0 && t1 > t0 && double.IsFinite(t0) && double.IsFinite(t1) && CheckedElevations(z0, z1) is { } height
            ? (height, t0, t1) : null;

        void Include(double u, double z)
        {
            double level = beam.Z + beam.SinRoll * u + beam.CosRoll * z;
            double across = beam.CosRoll * u - beam.SinRoll * z;
            z0 = Math.Min(z0, level);
            z1 = Math.Max(z1, level);
            t0 = Math.Min(t0, across);
            t1 = Math.Max(t1, across);
        }
    }
}
