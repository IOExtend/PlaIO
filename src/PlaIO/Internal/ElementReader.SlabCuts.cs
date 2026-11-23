using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid BeamSlabJunctionClass = new("7259c45f-97e3-434a-bc9e-fe3bcf980947");
    private static readonly Guid SlabBeamRelation = new("d50bcd56-a71c-40f8-a3f1-551d14a4762b");
    private static readonly Guid BeamSlabRelation = new("4df5ee09-8368-4f3b-ad64-056115b4c0ab");
    private static readonly Guid SlabLevelDimensionClass = new("690878fa-b11f-46c9-953c-3864d85bdee6");
    private static readonly Guid SlabLevelDimensionRelation = new("9176a8c6-f502-440f-8ef3-f0a700178e4a");

    private static WallStrip? ReadJoinedWallStrip(ModelGeometry geometry, Guid wallId, WallStrip strip, ElementLocation location)
    {
        if (geometry.Read(wallId, WallClass) is not { } wall) return null;
        int start = FindRelationshipSection(wall, wall.Length);
        if (start < 0) return null;
        ReadOnlySpan<byte> relations = wall.AsSpan(start);
        var expanded = strip;
        while (relations.Length >= 17)
        {
            var entry = relations;
            if (!TryReadGeometryRelation(ref relations, out byte tag, out Guid key, out _)) return null;
            if (key != FirstWallEndRelation && key != SecondWallEndRelation) continue;
            if (tag is not (1 or 0x11)) return null;
            var ids = ReadRelationIds(entry, relations, tag);
            for (int i = 0; i < ids.Length; i += 16)
            {
                var id = new Guid(ids.Slice(i, 16));
                if (geometry.Read(id, WallWallJunctionClass) is not { } joint ||
                    ReadWallJunctionPeers(joint, wallId, key) is not { } peers ||
                    ReadWallJunctionStation(geometry, joint, strip, WallCapPlanClearance) is not { } station)
                    return null;
                var (along, across) = station;
                foreach (var (relation, neighborId) in peers)
                {
                    if (geometry.Read(neighborId, WallClass) is not { } neighbor ||
                        !HasGeometryRelation(neighbor, relation, id)) return null;
                    var record = neighbor.AsSpan(FindClassFrame(neighbor, WallClass));
                    var other = AfterCountedString(record, 197);
                    var contour = ReadWallContour(geometry, record, neighborId);
                    (double X0, double Y0, double X1, double Y1)? inline = null;
                    if (contour is null && HasEmptyWallPlanCache(other)) ReadWallReferencePlan(other, out inline);
                    if (!SameIntersectionGroup(geometry, location.LayerIndex, ReadElementLocation(record, "Wall", 0).LayerIndex))
                        return null;
                    if (ReadWallStrip(other, contour, inline) is not { } peer)
                    {
                        if (Math.Abs(across - strip.Line) > JunctionTolerance || ReadWallReferencePlan(other, out _) is not { } plan ||
                            CrossingWallInterval(strip, plan, station) is not { } interval) return null;
                        expanded = expanded with { Start = Math.Min(expanded.Start, interval.Start), End = Math.Max(expanded.End, interval.End) };
                        continue;
                    }
                    if (peer.AlongX == strip.AlongX)
                    {
                        // A collinear butt ends at the common station even when the face offsets differ.
                        if (!(AreCollinearWallEnds(strip, peer) && PointOnStrip(peer, along, across)) &&
                            !WallButtAtStation(strip, peer, station))
                            return null;
                        continue;
                    }
                    if (Math.Abs(across - strip.Line) > JunctionTolerance || !PointOnStrip(peer, across, along)) return null;
                    // Perpendicular cleanup may extend the wall along its axis as far as the peer's outer face.
                    expanded = expanded with { Start = Math.Min(expanded.Start, peer.AcrossMin), End = Math.Max(expanded.End, peer.AcrossMax) };
                }
            }
        }
        return relations is [0] ? expanded : null;

        static bool PointOnStrip(WallStrip s, double along, double across) =>
            Math.Abs(across - s.Line) <= JunctionTolerance && along >= s.Start - JunctionTolerance && along <= s.End + JunctionTolerance;
    }

    private static (double Along, double Across)? ReadWallJunctionStation(ModelGeometry geometry, ReadOnlySpan<byte> joint,
        WallStrip strip, double acrossTolerance = JunctionTolerance)
    {
        Guid pointId = ReadSingleRelation(joint, WallJunctionPointRelation, requireSection: true);
        if (geometry.Read(pointId, WallJunctionPointClass) is not { Length: >= 62 } point) return null;
        double along = ReadDouble(point, strip.AlongX ? 46 : 54), across = ReadDouble(point, strip.AlongX ? 54 : 46);
        return Math.Abs(across - strip.Line) <= acrossTolerance &&
            along >= strip.Start - JunctionTolerance && along <= strip.End + JunctionTolerance ? (along, across) : null;
    }

    private static bool WallButtAtStation(WallStrip a, WallStrip b, (double Along, double Across) station)
    {
        if (Math.Abs(a.Line - b.Line) > WallCapPlanClearance ||
            Math.Abs(station.Across - b.Line) > WallCapPlanClearance ||
            Math.Min(a.AcrossMax, b.AcrossMax) <= Math.Max(a.AcrossMin, b.AcrossMin)) return false;
        // Both rectangular prisms terminate at the saved joint station. Only their transverse coordinates can differ.
        return AtEnd(a.Start) && AtEnd(b.End) || AtEnd(a.End) && AtEnd(b.Start);

        bool AtEnd(double end) => Math.Abs(end - station.Along) <= JunctionTolerance;
    }

    private static (double Start, double End)? CrossingWallInterval(WallStrip strip, PlaWallReferencePlan peer,
        (double Along, double Across) station)
    {
        double dx = peer.End.X - peer.Start.X, dy = peer.End.Y - peer.Start.Y, length = double.Hypot(dx, dy);
        double x = (strip.AlongX ? station.Along : station.Across) - peer.Start.X;
        double y = (strip.AlongX ? station.Across : station.Along) - peer.Start.Y;
        double at = (x * dx + y * dy) / length, offset = (x * dy - y * dx) / length;
        double along = (strip.AlongX ? dy : -dx) / length, across = (strip.AlongX ? -dx : dy) / length;
        if (Math.Abs(along) <= JunctionTolerance || Math.Abs(offset) > JunctionTolerance ||
            at < -JunctionTolerance || at > length + JunctionTolerance) return null;

        // Project the intersection of the two wall strips onto the target wall's axis.
        double a = (strip.AcrossMin - station.Across) * across, b = (strip.AcrossMax - station.Across) * across;
        double first = (Math.Min(peer.OutsideOffset, peer.InsideOffset) - offset - Math.Max(a, b)) / along;
        double last = (Math.Max(peer.OutsideOffset, peer.InsideOffset) - offset - Math.Min(a, b)) / along;
        return (station.Along + Math.Min(first, last), station.Along + Math.Max(first, last));
    }

    private static List<(Guid Relation, Guid Owner)>? ReadWallJunctionPeers(ReadOnlySpan<byte> joint, Guid wall, Guid ownRelation)
    {
        int start = FindRelationshipSection(joint, joint.Length);
        if (start < 0) return null;
        var data = joint[start..];
        var peers = new List<(Guid Relation, Guid Owner)>();
        bool found = false;
        while (data.Length >= 17)
        {
            var entry = data;
            if (!TryReadGeometryRelation(ref data, out byte tag, out Guid key, out _)) return null;
            if (tag == 0x12 && key == WallJunctionPointRelation) continue;
            if (tag is not (2 or 0x12) || key != FirstWallEndRelation && key != SecondWallEndRelation) return null;
            var ids = ReadRelationIds(entry, data, tag);
            for (int i = 0; i < ids.Length; i += 16)
            {
                var owner = new Guid(ids.Slice(i, 16));
                if (owner == wall)
                {
                    if (key != ownRelation || found) return null;
                    found = true;
                }
                else
                {
                    if (owner == Guid.Empty || peers.Exists(p => p.Owner == owner)) return null;
                    peers.Add((key, owner));
                }
            }
        }
        return found && peers.Count != 0 && data is [0] ? peers : null;
    }

    private static bool SlabContactKeepsCuttingBand(ModelGeometry geometry, Guid slab, Guid key, Guid id, uint wallPriority,
        double home, (double X0, double Y0, double X1, double Y1) footprint, Elevations band)
    {
        if (key == WallLabelRelation) return geometry.SlabLabelIsMetadata(slab, id, key);
        if (key == ElementPropertyOverrideLink)
            return geometry.Read(id, PropertyOverrideClass) is { } property &&
                ReadSingleRelation(property, key, requireSection: true) == slab;
        if (key == CutElementRelation || key == OpeningHostRelation)
            return SlabOpeningMissesStrip(geometry, slab, key, id, footprint, home, band, out _);
        if (key == SlabLevelDimensionRelation)
            return geometry.Read(id, SlabLevelDimensionClass) is { } dimension &&
                ReadSingleRelation(dimension, key, requireSection: true) == slab;
        bool wallContact = key == SlabWallRelation || key == AlternateSlabWallRelation;
        if (wallContact)
        {
            // An equal-priority wall cannot remove the slab skins that cut the target wall.
            return ReadSlabWallPeer(geometry, slab, key, id, out Guid wall) is { } other &&
                ReadWallPriority(geometry, wall, other) is { } priority && (priority <= wallPriority ||
                    SlabContactWallMissesStrip(geometry, wall, other, footprint, home, band));
        }
        var (type, ownerRelation) = key == SlabBeamRelation ? (BeamSlabJunctionClass, BeamSlabRelation) :
            key == SlabColumnRelation ? (SlabColumnJunctionClass, ColumnSlabRelation) : default;
        if (geometry.Read(id, type) is not { } junction ||
            ReadSingleRelation(junction, key, requireSection: true) != slab) return false;
        Guid owner = ReadSingleRelation(junction, ownerRelation, requireSection: true);
        if (key == SlabColumnRelation)
            return SlabColumnMissesWall(geometry, owner, id, wallPriority, home, footprint, band);

        if (geometry.Read(owner, BeamMemoClass) is not { } memo || !HasGeometryRelation(memo, ownerRelation, id)) return false;
        var hatch = ReadBeamHatch(geometry, owner, memo);
        if (hatch.IsEmpty ||
            geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..])) is not { } beamPriority)
            return false;
        if (beamPriority < wallPriority) return true;
        if (ReadBeamPrism(geometry, owner, memo, home) is not { } beam || BeamSectionExtents(beam, hatch) is not { } section) return false;
        if (DisjointHeights(section.Height.Bottom, section.Height.Top, band)) return true;
        var (left, right) = ProjectRectangle(footprint, beam.X, beam.Y, beam.Dy, -beam.Dx);
        if (right <= section.Left + JunctionTolerance || left >= section.Right - JunctionTolerance) return true;
        var (from, to) = ProjectRectangle(footprint, beam.X, beam.Y, beam.Dx, beam.Dy);
        var rectangle = ReadBeamRectangle(hatch);
        return (to <= JunctionTolerance || from >= beam.Length - JunctionTolerance) &&
            HasOnlyBeamTargetContact(memo, BeamSlabRelation, id, beam.Owner, PreservesClearance);

        bool PreservesClearance(Guid relation, Guid joint)
        {
            Guid jointClass = relation == BeamSlabRelation ? BeamSlabJunctionClass :
                relation == BeamWallRelation ? WallBeamJunctionClass :
                relation == BeamColumnRelation ? BeamColumnJunctionClass : Guid.Empty;
            if (jointClass != Guid.Empty)
                return geometry.Read(joint, jointClass) is { } body &&
                    ReadSingleRelation(body, relation, requireSection: true) == owner;

            return rectangle is { } own && BeamJointMissesFootprint(geometry, owner, relation, joint, beam, own, home, (from, to));
        }
    }

    private static byte[]? ReadSlabWallPeer(ModelGeometry geometry, Guid slab, Guid role, Guid junction, out Guid wall)
    {
        wall = Guid.Empty;
        if ((role != SlabWallRelation && role != AlternateSlabWallRelation) ||
            geometry.Read(junction, WallSlabJunctionClass) is not { } contact ||
            ReadSingleRelation(contact, role, requireSection: true) != slab) return null;
        wall = ReadSingleRelation(contact, WallSlabRelation, requireSection: true);
        return geometry.Read(wall, WallClass) is { } body && HasGeometryRelation(body, WallSlabRelation, junction) ? body : null;
    }

    private static bool BeamJointMissesFootprint(ModelGeometry geometry, Guid owner, Guid relation, Guid joint,
        BeamPrism beam, (double HalfWidth, double Depth) own, double home, (double Min, double Max) along)
    {
        if (beam.SinRoll != 0 || ReadBeamAxisJunction(geometry, owner, relation, joint) is not { } link ||
            geometry.Read(link.Peer, BeamMemoClass) is not { } peerMemo ||
            !HasGeometryRelation(peerMemo, link.Reciprocal, joint) ||
            ReadBeamPrism(geometry, link.Peer, peerMemo, home) is not { SinRoll: 0 } peer ||
            !SameIntersectionGroup(geometry, beam.Layer, peer.Layer) ||
            !IsOnBeamJoint(beam, link.X, link.Y, IsBeamEndRelation(relation)) ||
            !IsOnBeamJoint(peer, link.X, link.Y, IsBeamEndRelation(link.Reciprocal)) ||
            ReadBeamRectangle(ReadBeamHatch(geometry, link.Peer, peerMemo)) is not { } other)
            return false;

        // The join can change the beam's end only inside the intersection of the two section strips.
        double direction = beam.Dx * peer.Dx + beam.Dy * peer.Dy;
        const double levelTolerance = 1e-7;
        if (Math.Abs(direction) > JunctionTolerance &&
            (Math.Abs(own.HalfWidth - other.HalfWidth) > JunctionTolerance ||
                Math.Abs(own.Depth - other.Depth) > JunctionTolerance || Math.Abs(beam.Z - peer.Z) > levelTolerance))
            return false;
        if (ProjectBeamJunctionInterval(beam, peer, link.X, link.Y, -own.HalfWidth, own.HalfWidth,
                -other.HalfWidth, other.HalfWidth) is { } reach)
            return along.Max < reach.Min - JunctionTolerance || along.Min > reach.Max + JunctionTolerance;

        // At a collinear butt joint each beam continues away from the shared end.
        double station = (link.X - beam.X) * beam.Dx + (link.Y - beam.Y) * beam.Dy;
        double peerStation = (link.X - peer.X) * peer.Dx + (link.Y - peer.Y) * peer.Dy;
        return IsBeamEndRelation(relation) && IsBeamEndRelation(link.Reciprocal) &&
            beam.Dx * peer.Dy == beam.Dy * peer.Dx &&
            (beam.Length / 2 - station) * (peer.Length / 2 - peerStation) * direction < 0;
    }

    private static bool SharedBeamPreservesSlabCoverage(ModelGeometry geometry, Guid wall, Guid slab, Guid id,
        ElementLocation location, double home, uint wallPriority, (double X0, double Y0, double X1, double Y1) footprint)
    {
        if (geometry.Read(id, BeamSlabJunctionClass) is not { } joint ||
            ReadSingleRelation(joint, SlabBeamRelation, requireSection: true) != slab)
            return false;
        Guid beamId = ReadSingleRelation(joint, BeamSlabRelation, requireSection: true);
        if (geometry.Read(beamId, BeamMemoClass) is not { } memo ||
            ReadBeamPrism(geometry, beamId, memo, home) is not { SinRoll: 0 } beam ||
            !SameIntersectionGroup(geometry, beam.Layer, location.LayerIndex))
            return false;
        var hatch = ReadBeamHatch(geometry, beamId, memo);
        if (ReadBeamRectangle(hatch) is not { } section ||
            geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..])) is not { } priority ||
            priority < wallPriority || ReadConnectedSlabPriority(geometry, slab, id) is not { } slabPriority || priority < slabPriority)
            return false;

        // Where this beam replaces slab material, it must also cut the target wall.
        bool cutsWall = false;
        var along = ProjectRectangle(footprint, beam.X, beam.Y, beam.Dx, beam.Dy);
        return HasOnlyBeamTargetContact(memo, BeamSlabRelation, id, beam.Owner, KeepsBeam) && cutsWall;

        bool KeepsBeam(Guid key, Guid contact)
        {
            if (key == BeamWallRelation)
            {
                if (ReadBeamStructuralNeighbor(geometry, beamId, key, contact) is not { } peer ||
                    ReadWallPriority(geometry, peer.Id, peer.Body) is not { } p || p > priority) return false;
                if (peer.Id != wall) return true;
                if (cutsWall) return false;
                cutsWall = true;
                return true;
            }
            if (key == BeamSlabRelation) return BeamWinsContact(geometry, beamId, key, contact, priority);
            if (key == BeamColumnRelation)
                return TryReadBeamColumnCut(geometry, beamId, contact, priority, out var plan, allowVerifiedNonWrappingWalls: true) &&
                    (plan is null || OpeningPlansDisjoint(footprint, plan.Value));
            return BeamJointMissesFootprint(geometry, beamId, key, contact, beam, section, home, along);
        }
    }

    private static bool SlabContactWallMissesStrip(ModelGeometry geometry, Guid owner, byte[] body,
        (double X0, double Y0, double X1, double Y1) footprint, double home, Elevations band)
    {
        var record = body.AsSpan(FindClassFrame(body, WallClass));
        var settings = ReadWallSettings(body);
        var location = ReadElementLocation(record, "Wall", 0);
        if (ReadWallReferencePlan(settings, out var inline) is not { } axis ||
            !HasBoundedWallReferenceBody(geometry, owner, record)) return false;
        if (geometry.StoryElevation(location.HomeStoryIndex) is { } peerHome &&
            ReadWallReferencePlanes(settings, location, geometry) is { } levels &&
            DisjointHeights(levels.Bottom + peerHome - home, levels.Top + peerHome - home, band)) return true;

        double dx = axis.End.X - axis.Start.X, dy = axis.End.Y - axis.Start.Y, length = double.Hypot(dx, dy);
        var offsets = ProjectRectangle(footprint, axis.Start.X, axis.Start.Y, dy / length, -dx / length);
        if (offsets.Max < Math.Min(axis.InsideOffset, axis.OutsideOffset) - JunctionTolerance ||
            offsets.Min > Math.Max(axis.InsideOffset, axis.OutsideOffset) + JunctionTolerance)
            return true;
        return ReadWallStrip(settings, ReadWallContour(geometry, record, owner), inline) is { } strip &&
            ReadJoinedWallStrip(geometry, owner, strip, location) is { } expanded && OpeningPlansDisjoint(footprint, expanded.Footprint);
    }

    private static bool HasBoundedWallReferenceBody(ModelGeometry geometry, Guid owner, ReadOnlySpan<byte> wall)
    {
        int start = FindRelationshipSection(wall, wall.Length);
        if (start < 0) return false;
        var relations = wall[start..];
        while (relations.Length >= 17)
        {
            var entry = relations;
            if (!TryReadGeometryRelation(ref relations, out byte tag, out Guid role, out _)) return false;
            if (role == ElementAssemblyRelation)
            {
                var pairs = ReadRelationIds(entry, relations, tag);
                if (tag == 2 && WallIsSubtractionOperator(geometry, owner, pairs)) continue;
                if (tag != 1 || pairs.IsEmpty || pairs.Length % 32 != 0) return false;
                // A verified subtraction cannot extend the wall outside its reference body.
                for (int i = 0; i < pairs.Length; i += 32)
                {
                    Guid slab = new(pairs.Slice(i, 16)), operation = new(pairs.Slice(i + 16, 16));
                    if (ReadSubtractionDirection(geometry, owner, slab, operation) is null ||
                        geometry.Read(slab, SlabClass) is not { } operand ||
                        !HasOperationPair(operand, owner, operation, operationTag: 2))
                        return false;
                }
                continue;
            }
            if (tag == 0x12 && (UnjoinedWallOwnerRelations.Contains(role) || role == DatabaseElementList ||
                    role == WallTopStoryRelation || role == AttributeOwnerRelation) ||
                tag == 2 && (role == ZoneCalculationRelation || role == AttributeOwnerRelation) ||
                tag == 0x11 && (UnjoinedWallChildRelations.Contains(role) || role == LinkedElementSettingsRelation ||
                    role == PlanContourRelation)) continue;
            if (tag is not (1 or 0x11) || role != DrawingRelation && role != WallLabelRelation &&
                role != AttachedAnnotationRelation && role != ElementPropertyOverrideLink && role != DoorHostRelation &&
                role != CutElementRelation && role != OpeningHostRelation && role != WallSlabRelation && role != WallBeamRelation &&
                role != WallColumnRelation && role != FirstWallEndRelation && role != SecondWallEndRelation) return false;
        }
        return relations is [0];
    }

    private static bool CollectBeamForSlabCut(ModelGeometry geometry, Guid wallId, Guid slabId, Guid id,
        ElementLocation wall, double home,
        Elevations slabBand, Elevations bounds, bool cutsBottom, uint wallPriority,
        List<(Guid Memo, Guid Junction)> cutters)
    {
        if (geometry.Read(id, BeamSlabJunctionClass) is not { } joint ||
            ReadSingleRelation(joint, SlabBeamRelation, requireSection: true) != slabId)
            return false;
        Guid memoId = ReadSingleRelation(joint, BeamSlabRelation, requireSection: true);
        if (geometry.Read(memoId, BeamMemoClass) is not { } memo ||
            ReadBeamPrism(geometry, memoId, memo, home) is not { SinRoll: 0 } beam ||
            !SameIntersectionGroup(geometry, beam.Layer, wall.LayerIndex))
            return false;
        var hatch = ReadBeamHatch(geometry, memoId, memo);
        if (BeamPlanBand(beam, hatch) is not { } band ||
            geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..])) is not { } priority ||
            priority < wallPriority || ReadConnectedSlabPriority(geometry, slabId) is not { } slabPriority || priority < slabPriority ||
            (cutsBottom
                ? band.Top <= slabBand.Top || band.Bottom >= slabBand.Top || band.Top >= bounds.Top
                : band.Bottom >= slabBand.Bottom || band.Top <= slabBand.Bottom || band.Bottom <= bounds.Bottom))
            return false;
        Guid contact = Guid.Empty;
        // The beam may replace part of the slab band and still continue the cut towards the wall's interior.
        if (!HasOnlyBeamTargetContact(memo, BeamSlabRelation, id, beam.Owner, KeepsBeam) || contact == Guid.Empty)
            return false;
        cutters.Add((memoId, contact));
        return true;

        bool KeepsBeam(Guid key, Guid other)
        {
            if (IsBeamEndRelation(key) || key == FirstBeamCollisionRelation || key == SecondBeamCollisionRelation)
                return true; // The union verifies axial and transverse beam contacts after collecting all cutters.
            if (key == BeamSlabRelation && geometry.Read(other, BeamSlabJunctionClass) is { } slabJoint &&
                ReadSingleRelation(slabJoint, key, requireSection: true) == memoId)
            {
                Guid slabOther = ReadSingleRelation(slabJoint, SlabBeamRelation, requireSection: true);
                return ReadConnectedSlabPriority(geometry, slabOther, other) is { } p && p <= priority;
            }
            if (key != BeamWallRelation || geometry.Read(other, WallBeamJunctionClass) is not { } wallJoint ||
                ReadSingleRelation(wallJoint, key, requireSection: true) != memoId) return false;
            Guid peer = ReadSingleRelation(wallJoint, WallBeamRelation, requireSection: true);
            if (geometry.Read(peer, WallClass) is not { } wallRecord || !HasGeometryRelation(wallRecord, WallBeamRelation, other))
                return false;
            if (peer != wallId) return ReadWallPriority(geometry, peer, wallRecord) is { } p && p <= priority;
            if (contact != Guid.Empty) return false;
            contact = other;
            return true;
        }
    }

    private static uint? ReadConnectedSlabPriority(ModelGeometry geometry, Guid slab, Guid junction = default)
    {
        if (geometry.Read(slab, SlabClass) is not { } body ||
            junction != Guid.Empty && !HasGeometryRelation(body, SlabBeamRelation, junction)) return null;
        var record = body.AsSpan(FindClassFrame(body, SlabClass));
        return ReadSlabPriority(AfterCountedString(record, ClassElementIdOffset("Slab")), geometry.Attributes);
    }

    private static bool SlabColumnMissesWall(ModelGeometry geometry, Guid memoId, Guid junction, uint wallPriority, double home,
        (double X0, double Y0, double X1, double Y1) footprint, Elevations band)
    {
        var hatch = ReadColumnContactSection(geometry, memoId, ColumnSlabRelation, junction, out var column, includeCircular: true);
        if (hatch.IsEmpty ||
            geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..])) is not { } priority)
            return false;
        if (priority < wallPriority) return true;
        if (!column.Slice(304, ColumnEndPlaneFrame.Length).SequenceEqual(ColumnEndPlaneFrame) ||
            BinaryPrimitives.ReadUInt16LittleEndian(column[368..]) != 1) return false;
        if (IsHorizontal(column[312..]) && IsHorizontal(column[370..]) &&
            ReadDouble(column, 352) < 0 && ReadDouble(column, 410) > 0 &&
            geometry.StoryElevation(ReadElementLocation(column, "Column", 0).HomeStoryIndex) is { } level &&
            DisjointHeights(ReadDouble(column, 328) + level - home, ReadDouble(column, 386) + level - home, band))
            return true;
        return ReadColumnFootprint(column, hatch) is { } plan &&
            (plan.X1 <= footprint.X0 + JunctionTolerance || plan.X0 >= footprint.X1 - JunctionTolerance ||
             plan.Y1 <= footprint.Y0 + JunctionTolerance || plan.Y0 >= footprint.Y1 - JunctionTolerance);
    }

    private static bool DisjointHeights(double bottom, double top, Elevations band) =>
        double.IsFinite(bottom) && double.IsFinite(top) && top > bottom &&
        (top <= band.Bottom + JunctionTolerance || bottom >= band.Top - JunctionTolerance);
}
