using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static PlaColumnCutMesh? ReadObliqueColumn(ReadOnlySpan<byte> column, ReadOnlySpan<byte> hatch,
        PlaMemberProfile? profile, Guid memo, List<(Guid Key, Guid Id)>? contacts, ElementLocation location,
        ModelGeometry geometry, double angle)
    {
        if (profile is null || contacts is null || !double.IsFinite(angle) ||
            geometry.StoryElevation(location.HomeStoryIndex) is not { } home ||
            ReadStraightProfileSection(profile.Components) is not { Count: 1 } regions || regions[0].Count != 4 ||
            ReadColumnFootprint(column, hatch, angle) is not { } plan ||
            geometry.Attributes.Material.GetValueOrDefault(profile.Components[0].BuildingMaterialIndex) is not (> 0 and { } priority))
            return null;
        var (sin, cos) = Math.SinCos(angle);
        if (Math.Abs(sin) > JunctionTolerance && Math.Abs(cos) > JunctionTolerance) return null;
        var ring = regions[0];
        if (ring.Where((p, i) => p.X != ring[(i + 1) % ring.Count].X && p.Y != ring[(i + 1) % ring.Count].Y).Any()) return null;

        var lowNormal = ReadBeamVector(column, 336);
        var highNormal = ReadBeamVector(column, 394);
        bool alongX = Math.Abs(lowNormal.Y) <= JunctionTolerance && Math.Abs(highNormal.Y) <= JunctionTolerance;
        if (!lowNormal.IsUnit || !highNormal.IsUnit || !(lowNormal.Z < -JunctionTolerance && highNormal.Z > JunctionTolerance) ||
            !alongX && (Math.Abs(lowNormal.X) > JunctionTolerance || Math.Abs(highNormal.X) > JunctionTolerance)) return null;

        var origin = new PlaColumnMeshPoint(ReadDouble(column, 312), ReadDouble(column, 320), home);
        double start = alongX ? plan.X0 - origin.X : plan.Y0 - origin.Y;
        double end = alongX ? plan.X1 - origin.X : plan.Y1 - origin.Y;
        double minAcross = alongX ? origin.Y - plan.Y1 : plan.X0 - origin.X;
        double maxAcross = alongX ? origin.Y - plan.Y0 : plan.X1 - origin.X;
        double lowZ = ReadDouble(column, 328), highZ = ReadDouble(column, 386);
        double lowSlope = -(alongX ? lowNormal.X : lowNormal.Y) / lowNormal.Z;
        double highSlope = -(alongX ? highNormal.X : highNormal.Y) / highNormal.Z;
        PlaPlanPoint[] section = [new(start, lowZ + lowSlope * start), new(end, lowZ + lowSlope * end),
            new(end, highZ + highSlope * end), new(start, highZ + highSlope * start)];
        if (!(section[3].Y > section[0].Y && section[2].Y > section[1].Y)) return null;
        var height = new Elevations(section.Min(p => p.Y), section.Max(p => p.Y));
        var box = MakeBoundingBox(plan, height.Bottom + home, height.Top + home);
        if (box is null) return null;

        var masks = new List<ColumnBeamMask>();
        var seen = new HashSet<Guid>();
        foreach (var (role, contact) in contacts)
        {
            if (role == ColumnWallRelation)
            {
                if (geometry.Read(contact, WallColumnJunctionClass) is not { } wallJunction ||
                    ReadSingleRelation(wallJunction, role, requireSection: true) != memo) return null;
                Guid wallId = ReadSingleRelation(wallJunction, WallColumnRelation, requireSection: true);
                if (geometry.Read(wallId, WallClass) is not { } wall || !HasGeometryRelation(wall, WallColumnRelation, contact) ||
                    !WallClearsObliqueColumn(geometry, wallId, wall, location.LayerIndex, priority, box)) return null;
                continue;
            }
            if (role != ColumnBeamRelation || geometry.Read(contact, BeamColumnJunctionClass) is not { } joint ||
                ReadSingleRelation(joint, role, requireSection: true) != memo) return null;
            Guid beamId = ReadSingleRelation(joint, BeamColumnRelation, requireSection: true);
            if (ReadBeamPriority(geometry, beamId, contact) is not (> 0 and { } beamPriority)) return null;
            if (beamPriority <= priority || !seen.Add(beamId)) continue;
            if (geometry.Read(beamId, BeamMemoClass) is not { } beamMemo ||
                ReadStraightBeamAxis(geometry, beamId, beamMemo, home, out var ends, readAnnotations: true) is not { } beam ||
                !SameIntersectionGroup(geometry, beam.Layer, location.LayerIndex) ||
                !HasOnlyBeamTargetContact(beamMemo, BeamColumnRelation, contact, beam.Owner, (r, j) =>
                    BeamKeepsObliqueColumnCut(geometry, beamId, beam, ends, beamPriority, r, j, home, box)) ||
                !AddObliqueBeamMasks(geometry, beamId, beamMemo, beam, ends, origin, alongX, minAcross, maxAcross, masks))
                return null;
        }

        return masks.Count > 0 ? BuildColumnCutMesh(section, minAcross, maxAcross, masks, origin,
            (p, across) => alongX ? new(p.X, -across, p.Y) : new(across, p.X, p.Y)) : null;
    }

    private static bool AddObliqueBeamMasks(ModelGeometry geometry, Guid beamId, byte[] memo, BeamPrism axis, BeamEndPlanes ends,
        PlaColumnMeshPoint origin, bool alongX, double minAcross, double maxAcross, List<ColumnBeamMask> masks)
    {
        double direction = alongX ? axis.Dx : axis.Dy;
        double acrossDirection = alongX ? -axis.Dy : axis.Dx;
        if (Math.Abs(acrossDirection * axis.Length) > JunctionTolerance || Math.Abs(axis.SinRoll) > JunctionTolerance ||
            !ends.StartNormal.IsUnit || !ends.EndNormal.IsUnit ||
            Math.Abs(alongX ? ends.StartNormal.Y : ends.StartNormal.X) > JunctionTolerance ||
            Math.Abs(alongX ? ends.EndNormal.Y : ends.EndNormal.X) > JunctionTolerance ||
            geometry.Read(axis.Owner, BeamClass) is not { } body) return false;

        var record = body.AsSpan(FindClassFrame(body, BeamClass));
        IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? profile;
        if (ReadMemberProfile(record, axis.Owner, beam: true, geometry) is { } section)
            profile = ReadStraightProfileSection(section.Components);
        else if (ReadBeamRectangle(ReadBeamHatch(geometry, beamId, memo)) is { } rect)
            profile = [[new(-rect.HalfWidth, -rect.Depth), new(rect.HalfWidth, -rect.Depth), new(rect.HalfWidth, 0), new(-rect.HalfWidth, 0)]];
        else
            return false;
        if (profile is null || profile.Any(r => r.Where((p, i) => p.X != r[(i + 1) % r.Count].X && p.Y != r[(i + 1) % r.Count].Y).Any()))
            return false;

        double length = double.Hypot(axis.Length, ends.Rise);
        double heading = Math.CopySign(axis.Length / length, direction), pitch = ends.Rise / length;
        double lateral = alongX ? origin.Y - axis.Y : axis.X - origin.X;
        double start = alongX ? axis.X - origin.X : axis.Y - origin.Y;
        double end = start + Math.CopySign(axis.Length, direction);
        double startDot = (alongX ? ends.StartNormal.X : ends.StartNormal.Y) * heading + ends.StartNormal.Z * pitch;
        double endDot = (alongX ? ends.EndNormal.X : ends.EndNormal.Y) * heading + ends.EndNormal.Z * pitch;
        if (!(startDot < -JunctionTolerance && endDot > JunctionTolerance)) return false;

        var knots = profile.SelectMany(r => r).Select(p => p.X).Distinct().Order().ToArray();
        for (int k = 1; k < knots.Length; k++)
        {
            double a = lateral + Math.CopySign(1, direction) * knots[k - 1], b = lateral + Math.CopySign(1, direction) * knots[k];
            double first = Math.Max(minAcross, Math.Min(a, b)), last = Math.Min(maxAcross, Math.Max(a, b));
            if (!(last > first)) continue;
            double sample = (knots[k - 1] + knots[k]) / 2;
            var edges = new List<double>();
            foreach (var ring in profile)
                for (int i = 0; i < ring.Count; i++)
                {
                    var p = ring[i];
                    var q = ring[(i + 1) % ring.Count];
                    if (sample > Math.Min(p.X, q.X) && sample < Math.Max(p.X, q.X)) edges.Add(p.Y);
                }
            if (edges.Count % 2 != 0) return false;
            edges.Sort();
            for (int i = 0; i < edges.Count; i += 2)
            {
                double z0 = edges[i], z1 = edges[i + 1];
                PlaPlanPoint[] polygon = [AtEnd(start, axis.Z, z0, ends.StartNormal, startDot),
                    AtEnd(end, axis.Z + ends.Rise, z0, ends.EndNormal, endDot),
                    AtEnd(end, axis.Z + ends.Rise, z1, ends.EndNormal, endDot),
                    AtEnd(start, axis.Z, z1, ends.StartNormal, startDot)];
                if (!polygon.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)) ||
                    Math.CopySign(1, direction) * (polygon[1].X - polygon[0].X) <= JunctionTolerance ||
                    Math.CopySign(1, direction) * (polygon[2].X - polygon[3].X) <= JunctionTolerance || masks.Count >= 48)
                    return false;
                if (direction < 0) Array.Reverse(polygon);
                masks.Add(new ColumnBeamMask(first, last, polygon));
            }
        }
        return true;

        PlaPlanPoint AtEnd(double x, double z, double offset, BeamVector normal, double dot)
        {
            double upX = -Math.CopySign(1, direction) * pitch;
            double upZ = axis.Length / length;
            double slide = -offset * ((alongX ? normal.X : normal.Y) * upX + normal.Z * upZ) / dot;
            return new(x + offset * upX + slide * heading, z + offset * upZ + slide * pitch);
        }
    }

    private static bool BeamKeepsObliqueColumnCut(ModelGeometry geometry, Guid beamId, BeamPrism beam, BeamEndPlanes ends,
        uint priority, Guid role, Guid id, double home, PlaBoundingBox target)
    {
        var plan = (target.XMin, target.YMin, target.XMax, target.YMax);
        if (role == BeamColumnRelation) return ReadBeamStructuralNeighbor(geometry, beamId, role, id) is not null;
        if (role == BeamWallRelation)
            return BeamWinsContact(geometry, beamId, role, id, priority) ||
                ReadBeamStructuralNeighbor(geometry, beamId, role, id) is { } wall &&
                WallClearsObliqueColumn(geometry, wall.Id, wall.Body, beam.Layer, priority, target);
        if (role == BeamSlabRelation)
            return BeamWinsContact(geometry, beamId, role, id, priority) ||
                ReadBeamSlabCutBox(geometry, beamId, id, new(target.ZMin, target.ZMax), out var slab) &&
                (slab is null || ClearPlan(slab, plan));

        if (ReadBeamAxisJunction(geometry, beamId, role, id) is not { } contact ||
            geometry.Read(contact.Peer, BeamMemoClass) is not { } memo || !HasGeometryRelation(memo, contact.Reciprocal, id) ||
            ReadStraightBeamAxis(geometry, contact.Peer, memo, home, out var otherEnds, readAnnotations: true) is not { } other ||
            !SameIntersectionGroup(geometry, beam.Layer, other.Layer) ||
            ReadBeamContactExtents(geometry, contact.Peer, memo, other) is not { } outline) return false;
        if (IsOnBeamJoint(beam, contact.X, contact.Y, IsBeamEndRelation(role)) &&
            IsOnBeamJoint(other, contact.X, contact.Y, IsBeamEndRelation(contact.Reciprocal)))
        {
            var (lo, hi) = ProjectRectangle(plan, other.X, other.Y, other.Dy, -other.Dx);
            if (hi < outline.Left - JunctionTolerance || lo > outline.Right + JunctionTolerance) return true;
        }

        if (!IsBeamEndRelation(role) || !IsBeamEndRelation(contact.Reciprocal) ||
            Math.Abs(beam.Dx * other.Dy - beam.Dy * other.Dx) > JunctionTolerance ||
            geometry.Read(beamId, BeamMemoClass) is not { } ownMemo ||
            ReadBeamCutEnd(geometry, beamId, ownMemo, beam, ends, contact.X, contact.Y) is not { } own ||
            ReadBeamCutEnd(geometry, contact.Peer, memo, other, otherEnds, contact.X, contact.Y) is not { } peer ||
            Math.Abs(own.Normal.Dot(peer.Normal) + 1) > JunctionTolerance) return false;

        // Opposing cap planes bound the join. The entire target must lie on the cutter's side of both planes.
        return PlaneMinimum(peer.Normal, peer.Centre) > JunctionTolerance &&
            PlaneMinimum(own.Normal * -1, own.Centre) > JunctionTolerance;

        double PlaneMinimum(BeamVector normal, BeamVector centre) =>
            ProjectRectangle(plan, centre.X, centre.Y, normal.X, normal.Y).Min +
            normal.Z * ((normal.Z >= 0 ? target.ZMin : target.ZMax) - home - centre.Z);
    }

    private static (BeamVector Normal, BeamVector Centre)? ReadBeamCutEnd(ModelGeometry geometry, Guid beamId, byte[] memo,
        BeamPrism axis, BeamEndPlanes ends, double x, double y)
    {
        if (Math.Abs((x - axis.X) * axis.Dy - (y - axis.Y) * axis.Dx) > JunctionTolerance ||
            geometry.Read(axis.Owner, BeamClass) is not { } record) return null;
        var profile = ReadMemberProfile(record.AsSpan(FindClassFrame(record, BeamClass)), axis.Owner, beam: true, geometry);
        if (ProjectBeamCaps(axis, ends, ReadBeamHatch(geometry, beamId, memo), profile) is not { } caps) return null;
        bool start = Covers(caps.First), end = Covers(caps.Last);
        if (start == end) return null;
        return start ? (ends.StartNormal, new BeamVector(axis.X, axis.Y, axis.Z)) :
            (ends.EndNormal, new BeamVector(axis.X + axis.Dx * axis.Length, axis.Y + axis.Dy * axis.Length, axis.Z + ends.Rise));

        bool Covers(PlaBoundingBox cap) => cap.XMin - JunctionTolerance <= x && x <= cap.XMax + JunctionTolerance &&
            cap.YMin - JunctionTolerance <= y && y <= cap.YMax + JunctionTolerance;
    }

    private static bool WallClearsObliqueColumn(ModelGeometry geometry, Guid wallId, byte[] wall, int? groupLayer,
        uint priority, PlaBoundingBox target)
    {
        var plan = (target.XMin, target.YMin, target.XMax, target.YMax);
        if (ReadWallPriority(geometry, wallId, wall) is > 0 and { } wallPriority && wallPriority <= priority) return true;
        var settings = ReadWallSettings(wall);
        if (ReadSavedWallBox(settings) is { } box && ClearPlan(box, plan)) return true;
        if (ReadWallReferencePlan(settings, out _) is not { } wallPlan)
            return ProfileWallMissesBoxes(geometry, wallId, wall, groupLayer, priority, [target], allowPartialPlan: true);
        double x = wallPlan.End.X - wallPlan.Start.X, y = wallPlan.End.Y - wallPlan.Start.Y;
        double length = double.Hypot(x, y);
        var (lo, hi) = ProjectRectangle(plan, wallPlan.Start.X, wallPlan.Start.Y, y / length, -x / length);
        return hi < Math.Min(wallPlan.OutsideOffset, wallPlan.InsideOffset) - JunctionTolerance ||
            lo > Math.Max(wallPlan.OutsideOffset, wallPlan.InsideOffset) + JunctionTolerance;
    }

    private static bool ClearPlan(PlaBoundingBox box, (double X0, double Y0, double X1, double Y1) plan) =>
        box.XMin > plan.X1 + JunctionTolerance || box.XMax < plan.X0 - JunctionTolerance ||
        box.YMin > plan.Y1 + JunctionTolerance || box.YMax < plan.Y0 - JunctionTolerance;
}
