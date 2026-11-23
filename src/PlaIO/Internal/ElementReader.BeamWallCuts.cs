using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid ElementLabelClass = new("1601b50c-a61f-447c-8b74-0f578c003d88");
    private static readonly Guid WallLabelRelation = new("5dd68b40-d2b2-4e13-9238-8343cdb6c423");
    private static readonly Guid ColumnLabelRelation = new("f73ad2dc-973b-4e70-9787-a3c6fee4a2bf");

    private sealed record SavedLabels(Dictionary<(Guid Label, Guid Role), Guid> Owners, HashSet<Guid> Indexed);

    private static SavedLabels ReadLabelOwners(PlaDocument document,
        Dictionary<ulong, List<ObjectDirectoryRecord>> records, HashSet<ushort> labelClasses)
    {
        var owners = new Dictionary<(Guid Label, Guid Role), Guid>();
        var indexed = records.Values.SelectMany(bucket => bucket).Select(record => record.ObjectId).ToHashSet();
        if (indexed.Count != 0)
        {
            foreach (var pool in document.FindCompressedPools())
            {
                if (pool.TotalDecompressed % ExtentUnit != 0 ||
                    !records.TryGetValue((ulong)(pool.TotalDecompressed / ExtentUnit), out var matches)) continue;
                var bytes = pool.Decode();
                foreach (var record in matches)
                    if (ReadIndexedPayload(bytes, record) is { } body) AddLabel(record.ObjectId, body);
            }
        }
        foreach (var block in document.Blocks)
            if (block.Tag == "BL" && block.Version == 1 && block.Guid is { } id && labelClasses.Contains(block.Kind))
                AddLabel(id, document.ReadPayload(block));
        return new SavedLabels(owners, indexed);

        void AddLabel(Guid id, byte[] body)
        {
            bool validClass = body.Length >= 16 && new Guid(body.AsSpan(0, 16)) == ElementLabelClass;
            AddOwner(id, validClass ? ReadSingleRelation(body, WallLabelRelation, 0x12, requireSection: true) : Guid.Empty,
                WallLabelRelation);
            AddOwner(id, validClass ? ReadSingleRelation(body, ColumnLabelRelation, 0x12, requireSection: true) : Guid.Empty,
                ColumnLabelRelation);
        }

        void AddOwner(Guid label, Guid owner, Guid role)
        {
            if (!owners.TryAdd((label, role), owner) && owners[(label, role)] != owner)
                owners[(label, role)] = Guid.Empty;
        }
    }

    private static bool ProfileWallLeavesBeamCaps(ModelGeometry geometry, Guid beamId, int? layer, Guid junction,
        uint priority, BeamCaps caps)
    {
        if (priority == 0 || geometry.Read(junction, WallBeamJunctionClass) is not { } contact ||
            ReadSingleRelation(contact, BeamWallRelation, requireSection: true) != beamId)
            return false;
        Guid wallId = ReadSingleRelation(contact, WallBeamRelation, requireSection: true);
        if (geometry.Read(wallId, WallClass) is not { } body || !HasGeometryRelation(body, WallBeamRelation, junction))
            return false;
        return ProfileWallMissesBoxes(geometry, wallId, body, layer, priority, [caps.First, caps.Last]);
    }

    private static bool ProfileWallMissesBoxes(ModelGeometry geometry, Guid wallId, byte[] body, int? layer, uint priority,
        IReadOnlyList<PlaBoundingBox> boxes, bool allowPartialPlan = false)
    {
        var record = body.AsSpan(FindClassFrame(body, WallClass));
        var location = ReadElementLocation(record, "Wall", 0);
        var settings = AfterCountedString(record, 197);
        if (settings.Length < 503 || settings[184] != 0 || settings[238] > 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(settings[239..]) != 0 || ReadDouble(settings, 243) != 0 ||
            location.TopStoryPartition != Guid.Empty || !SameIntersectionGroup(geometry, layer, location.LayerIndex) ||
            geometry.StoryElevation(location.HomeStoryIndex) is not { } story ||
            ReadWallProfile(record, settings, wallId, geometry) is not { } profile ||
            !HasSupportedSlabContacts(record, Guid.Empty, Guid.Empty, (key, id) =>
                UnjoinedWallChildRelations.Contains(key) || key == WallProfileRelation || key == PlanContourRelation ||
                key == FirstWallEndRelation || key == SecondWallEndRelation || key == WallColumnRelation ||
                key == WallSlabRelation || key == WallBeamRelation || key == WallLabelRelation && geometry.OwnsLabel(wallId, id, key)))
            return false;
        double originZ = story + ReadDouble(settings, 161);
        double x = ReadDouble(settings, 0), y = ReadDouble(settings, 8), x1 = ReadDouble(settings, 16), y1 = ReadDouble(settings, 24);
        double length = double.Hypot(x1 - x, y1 - y);
        if (!double.IsFinite(originZ) || !double.IsFinite(length) || length <= JunctionTolerance ||
            ProjectBeamSection(default, profile.Components, 1, 0) is not { } extent)
            return false;
        double rx = (y1 - y) / length, ry = (x - x1) / length;
        var start = new PlaPlanPoint(x, y);
        var end = new PlaPlanPoint(x1, y1);
        bool direct = ReadProfileWallRectangle(settings, start, end, extent.Min, extent.Max, allowPartialPlan) is not null;
        bool reflected = ReadProfileWallRectangle(settings, start, end, -extent.Max, -extent.Min, allowPartialPlan) is not null;
        if (!direct && !reflected) return false;
        foreach (var component in profile.Components)
        {
            if (geometry.Attributes.Material.GetValueOrDefault(component.BuildingMaterialIndex) is not (> 0 and { } p))
                return false;
            if (p <= priority) continue;
            if (direct && !boxes.All(box => MissesBox(component, box, 1)) ||
                reflected && !boxes.All(box => MissesBox(component, box, -1)))
                return false;
        }
        return true;

        // An unbounded sweep of the placed wall section also covers possible axial extensions at joins.
        bool MissesBox(PlaProfileComponent component, PlaBoundingBox cap, int sign)
        {
            var (min, max) = ProjectRectangle((cap.XMin, cap.YMin, cap.XMax, cap.YMax), x, y, sign * rx, sign * ry);
            return ProfileMissesRectangle(component, min - JunctionTolerance, max + JunctionTolerance,
                cap.ZMin - originZ - JunctionTolerance, cap.ZMax - originZ + JunctionTolerance);
        }
    }

    private static bool ProfileMissesRectangle(PlaProfileComponent component, double left, double right, double bottom, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(bottom) || !double.IsFinite(top) ||
            !(left < right && bottom < top))
            return false;
        var ring = component.Rings[0].Select(p => new PlaPlanPoint(p.U, p.Z)).ToArray();
        var arced = (component.Arcs ?? []).Where(a => a.RingIndex == 0).ToDictionary(a => a.StartVertexIndex, a => a.SweepRadians);
        var edges = new SlabEdge[ring.Length];
        PlaPlanPoint[] corners = [new(left, bottom), new(right, bottom), new(right, top), new(left, top)];
        for (int i = 0; i < edges.Length; i++)
        {
            var start = ring[i];
            var end = ring[(i + 1) % ring.Length];
            if (start.X >= left && start.X <= right && start.Y >= bottom && start.Y <= top) return false;
            if (!arced.TryGetValue(i, out double sweep)) edges[i] = new SlabEdge(start, end, 0);
            else if (ReadCircularEdge(start, end, sweep, out _) is { } arc) edges[i] = arc;
            else return false;
            for (int j = 0; j < corners.Length; j++)
                if (SlabEdgesIntersect(edges[i], new SlabEdge(corners[j], corners[(j + 1) % corners.Length], 0), null))
                    return false;
        }
        return !CurvedRingContainsPoint(ring, edges, corners[0]);
    }
}
