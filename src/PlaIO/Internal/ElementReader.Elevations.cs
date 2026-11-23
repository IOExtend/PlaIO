using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private readonly record struct Elevations(double Bottom, double Top, double? StoredProjectBottom = null);
    private readonly record struct WallCut(Guid Junction, bool CoversWallJoins = false, Guid[]? BeamJunctions = null,
        Guid OtherEndJunction = default);

    private static readonly Guid DrawingRelation = new("9898f378-2c25-462e-86b9-5e6b7a9aa0e4");
    private static readonly Guid DefaultElementRelation = new("3b42cf2e-0d77-4d3d-bf03-0b922103f576");
    private static readonly Guid ElementComponentRelation = new("9534032d-da8d-4c44-b42b-6363688cafc6");
    private static readonly Guid ElementSettingsRelation = new("c10473a6-9941-4108-b463-322b3ed18cfd");
    private static readonly Guid ElementPartitionRelation = new("79b61203-ae84-4d88-a9c6-20ead705e0bc");
    private static readonly Guid ElementCollectionRelation = new("a855b980-fd19-4560-8bc1-7a2b82c4f5aa");
    private static readonly HashSet<Guid> UnjoinedWallChildRelations =
    [
        DefaultElementRelation,
        new("6cf2b574-87db-442c-ad71-859dfffd9a56"),
        ElementComponentRelation,
        ElementSettingsRelation,
    ];

    private static readonly HashSet<Guid> UnjoinedWallOwnerRelations =
    [
        ElementPartitionRelation,
        ElementCollectionRelation,
    ];

    private static Elevations? ReadElevations(ReadOnlySpan<byte> record, string type)
    {
        switch (type)
        {
            case "Slab":
                {
                    var planes = AfterCountedString(record, ClassElementIdOffset(type));
                    if (!HasSupportedSlabReferencePlane(planes)) return null;

                    double reference = BinaryPrimitives.ReadDoubleLittleEndian(planes);
                    double thickness = BinaryPrimitives.ReadDoubleLittleEndian(planes[17..]);
                    if (thickness < 0) return null;
                    double top = reference + BinaryPrimitives.ReadDoubleLittleEndian(planes[9..]);
                    // shortcut: these are the saved slab planes, use evaluated geometry for model operations that alter them.
                    return CheckedElevations(top - thickness, top);
                }
            case "Zone":
                {
                    var levels = ReadZoneSettings(record);
                    if (levels.Length < 24) return null;

                    double height = BinaryPrimitives.ReadDoubleLittleEndian(levels[8..]);
                    if (height < 0) return null;
                    double bottom = BinaryPrimitives.ReadDoubleLittleEndian(levels) +
                        BinaryPrimitives.ReadDoubleLittleEndian(levels[16..]);
                    return CheckedElevations(bottom, bottom + height);
                }
            default:
                return null;
        }
    }

    private static ReadOnlySpan<byte> ReadZoneSettings(ReadOnlySpan<byte> record) =>
        AfterCountedString(AfterCountedString(record, 253), sizeof(ushort));

    private static Elevations? ReadWallElevations(ReadOnlySpan<byte> record, Guid wallId, ElementLocation location,
        ModelGeometry geometry, (double X0, double Y0, double X1, double Y1)? authoredFootprint, PlaWallReferencePlan? referencePlan,
        PlaWallProfile? profile, out PlaBoundingBox? boundingBox, out PlaWallExtrusion? extrusion, out List<Guid>? isolatedDoors,
        out bool needsDoorSolid, out (double? Bottom, double? Top) retainedCap)
    {
        boundingBox = null;
        extrusion = null;
        isolatedDoors = null;
        needsDoorSolid = false;
        retainedCap = default;
        const int id = 197;
        if (record.Length < id || !record[(id - ElementIdFrame.Length)..].StartsWith(ElementIdFrame))
            return null;

        var settings = AfterCountedString(record, id);
        bool emptyOutline = HasUncachedDoorWallOutline(record, settings);
        if ((!emptyOutline && settings.Length < 563) ||
            BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) is not (1 or 2 or 3)) return null;
        boundingBox = emptyOutline ? null : ReadSavedWallBox(settings);
        if (ReadWallReferencePlanes(settings, location, geometry) is not { } bounds) return null;
        if (!emptyOutline && boundingBox is null && geometry.StoryElevation(location.HomeStoryIndex) is { } story)
            boundingBox = ReadWallBoxWithoutPlanCache(settings, location, authoredFootprint, profile, bounds, story);

        double boxBottom = emptyOutline ? double.NaN : ReadDouble(settings, 547);
        double boxTop = emptyOutline ? double.NaN : ReadDouble(settings, 555);
        // The reference planes and the saved world-space envelope must describe the same height.
        var contour = ReadWallContour(geometry, record, wallId);
        bool hasWorldEnvelope = Math.Abs((boxTop - boxBottom) - (bounds.Top - bounds.Bottom)) <= JunctionTolerance;
        var inline = emptyOutline || boundingBox is not null && HasEmptyWallPlanCache(settings) ? authoredFootprint : null;
        var wall = emptyOutline && inline is null ? null : ReadWallStrip(settings, contour, inline);
        bool needsAuthoredFootprint = !hasWorldEnvelope && (wall is null || contour is null && inline is null);
        if (needsAuthoredFootprint && authoredFootprint is null) return null;
        if (!HasSupportedWallRelations(record, settings, wallId, location, geometry, bounds, wall, default, emptyOutline,
            out var evaluated, out var cutter, out bool onlyWallJunctions, out bool unjoined, out var doorIds, out var survivingCap))
        {
            if (!needsAuthoredFootprint && (!hasWorldEnvelope || geometry.StoryElevation(location.HomeStoryIndex) is not { } homeLevel ||
                Math.Abs(bounds.Bottom + homeLevel - boxBottom) <= JunctionTolerance))
                retainedCap = survivingCap;
            return null;
        }
        if (needsAuthoredFootprint && !unjoined) return null;
        if (cutter.Junction != Guid.Empty &&
            !HasSupportedWallRelations(record, settings, wallId, location, geometry, evaluated, wall, cutter, false,
                out _, out _, out _, out _, out _, out _))
            return null;
        if (boundingBox is null && onlyWallJunctions && contour is { } plan && wall is not null)
            boundingBox = ProjectBoundingBox(plan, evaluated, geometry.StoryElevation(location.HomeStoryIndex));
        if (boundingBox is null && (unjoined || doorIds is not null) && authoredFootprint is { } authored)
            boundingBox = ProjectBoundingBox(authored, evaluated, geometry.StoryElevation(location.HomeStoryIndex));
        if (unjoined && boundingBox is { } box && geometry.StoryElevation(location.HomeStoryIndex) is { } home)
            extrusion = ReadWallExtrusion(settings, referencePlan, profile, evaluated, home, box);
        isolatedDoors = doorIds;
        needsDoorSolid = emptyOutline && doorIds is not null;
        return hasWorldEnvelope ? evaluated with { StoredProjectBottom = boxBottom + (evaluated.Bottom - bounds.Bottom) } : evaluated;
    }

    private static Elevations? ReadWallReferencePlanes(ReadOnlySpan<byte> settings, ElementLocation location, ModelGeometry geometry)
    {
        if (settings.Length < 169) return null;
        double bottom = ReadDouble(settings, 161);
        double top = bottom + ReadDouble(settings, 153);
        if (location.TopStoryPartition != Guid.Empty)
        {
            if (geometry.LinkedElevation(location.TopStoryPartition) is not { } linkedStory ||
                geometry.StoryElevation(location.HomeStoryIndex) is not { } home) return null;
            top = linkedStory - home + ReadDouble(settings, 145);
        }
        return top > bottom ? CheckedElevations(bottom, top) : null;
    }

    private static bool HasSupportedWallRelations(ReadOnlySpan<byte> record, ReadOnlySpan<byte> settings,
        Guid wallId, ElementLocation location, ModelGeometry geometry, Elevations bounds, WallStrip? wall, WallCut validatedCut,
        bool requiresIsolated,
        out Elevations evaluated, out WallCut cutter, out bool onlyWallJunctions, out bool unjoined, out List<Guid>? isolatedDoors,
        out (double? Bottom, double? Top) retainedCap)
    {
        evaluated = bounds;
        cutter = default;
        onlyWallJunctions = true;
        unjoined = false;
        isolatedDoors = null;
        retainedCap = default;
        List<Guid>? doorOwners = null;
        int start = FindRelationshipSection(record, record.Length);
        if (start < 0) return false;
        var data = record[start..];
        var blocked = new List<WallCapObstacle>();
        List<WallCapObstacle>? doorCuts = null;
        var contacts = new List<(Guid Id, Guid Key, uint Priority)>();
        HashSet<Guid>? slabLinks = null;
        HashSet<Guid>? slabs = null;
        List<WallSlabOperation>? operations = null;
        List<Elevations>? openings = null;
        var profile = ReadWallProfileVector(geometry, wallId, record);
        bool basic = IsBasicWall(settings);
        if (BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) == 3 && profile.IsEmpty) return false;
        uint? slabCutPriority = basic ? null : ReadCompositeWallCutPriority(settings, geometry.Attributes);
        while (data.Length >= 17)
        {
            var entry = data;
            if (!TryReadGeometryRelation(ref data, out byte tag, out Guid key, out Guid target)) return false;
            if (key == WallTopStoryRelation)
            {
                if (tag != 0x12 || target == Guid.Empty || target != location.TopStoryPartition) return false;
                continue;
            }
            if ((tag is 1 or 0x11) && key == DrawingRelation ||
                tag == 0x11 && (UnjoinedWallChildRelations.Contains(key) || key == LinkedElementSettingsRelation) ||
                tag == 2 && key == AttributeOwnerRelation ||
                tag == 0x12 && (key == DatabaseElementList || key == AttributeOwnerRelation || UnjoinedWallOwnerRelations.Contains(key)))
                continue;
            if (tag == 0x11 && key == PlanContourRelation &&
                geometry.Read(target, WallPlanContourClass) is { } contour &&
                ReadSingleRelation(contour, PlanContourRelation, requireSection: true) == wallId)
                continue;
            if (tag == 0x11 && key == WallProfileRelation && !profile.IsEmpty) continue;
            if (key == DoorHostRelation)
            {
                if (tag is not (1 or 0x11) || wall is not { } wallStrip || doorCuts is not null) return false;
                var doorIds = ReadRelationIds(entry, data, tag);
                if (doorIds.IsEmpty) return false;
                doorCuts = [];
                doorOwners = [];
                for (int i = 0; i < doorIds.Length; i += 16)
                {
                    Guid door = new(doorIds.Slice(i, 16));
                    if (!ReadDoorCapObstacles(geometry, wallId, door, settings, wallStrip, doorCuts))
                        return false;
                    doorOwners.Add(door);
                }
                onlyWallJunctions = false;
                continue;
            }
            if (key == CutElementRelation || key == OpeningHostRelation)
            {
                if (requiresIsolated || tag is not (1 or 0x11)) return false;
                var openingIds = ReadRelationIds(entry, data, tag);
                if (openingIds.IsEmpty) return false;
                for (int i = 0; i < openingIds.Length; i += 16)
                {
                    if (ReadWallOpeningContact(geometry, record, wallId, key, new Guid(openingIds.Slice(i, 16))) is not { } opening)
                        return false;
                    (openings ??= []).Add(opening);
                }
                onlyWallJunctions = false;
                continue;
            }
            if (key == ElementAssemblyRelation)
            {
                var pairs = ReadRelationIds(entry, data, tag);
                if (tag == 2 && WallIsSubtractionOperator(geometry, wallId, pairs)) continue;
                if (requiresIsolated || tag != 1 || wall is not { } operandTarget || operations is not null ||
                    BinaryPrimitives.ReadUInt16LittleEndian(settings[227..]) != 1 ||
                    ReadWallSlabOperations(geometry, wallId, pairs,
                        (ReadJoinedWallStrip(geometry, wallId, operandTarget, location) ?? operandTarget).Footprint) is not { } parsed) return false;
                operations = parsed;
                continue;
            }
            if (key == ZoneCalculationRelation)
            {
                if (tag != 2 || !WallBoundsZones(geometry, wallId, ReadRelationIds(entry, data, tag))) return false;
                continue;
            }
            if (tag is 1 or 0x11 && (key == AttachedAnnotationRelation || key == WallLabelRelation || key == ElementPropertyOverrideLink))
            {
                var references = ReadRelationIds(entry, data, tag);
                if (references.IsEmpty) return false;
                for (int offset = 0; offset < references.Length; offset += 16)
                {
                    var reference = new Guid(references.Slice(offset, 16));
                    if (reference == Guid.Empty ||
                        key == WallLabelRelation && !geometry.OwnsLabel(wallId, reference, key) ||
                        key == AttachedAnnotationRelation && !IsAttachedAnnotation(geometry, wallId, reference))
                        return false;
                }
                continue;
            }
            if (requiresIsolated || tag is not (1 or 0x11) || (key != FirstWallEndRelation && key != SecondWallEndRelation &&
                    key != WallSlabRelation && key != WallBeamRelation && key != WallColumnRelation) ||
                ReadLowestWallPriority(settings, geometry.Attributes, profile) is not { } priority ||
                priority == 0)
                return false;
            if (validatedCut.Junction != Guid.Empty && !validatedCut.CoversWallJoins &&
                (key == FirstWallEndRelation || key == SecondWallEndRelation)) return false;
            onlyWallJunctions &= key == FirstWallEndRelation || key == SecondWallEndRelation;
            var ids = tag == 1 ? entry.Slice(21, entry.Length - data.Length - 21) : entry.Slice(17, 16);
            for (int i = 0; i < ids.Length; i += 16)
            {
                var id = new Guid(ids.Slice(i, 16));
                if (id == Guid.Empty) return false;
                if (key == WallSlabRelation)
                {
                    if (!(slabLinks ??= []).Add(id) || ReadWallSlabContact(geometry, wallId, id, out Guid slab) is null)
                        return false;
                    // Separate reciprocal junctions can refer to the same cutting solid.
                    if (!(slabs ??= []).Add(slab)) continue;
                }
                contacts.Add((id, key, priority));
            }
        }
        if (data is not [0]) return false;
        if (operations is not null)
            contacts.RemoveAll(c => c.Key == WallSlabRelation &&
                SlabJunctionIsInactive(geometry, wallId, location.LayerIndex, c.Id, fromWall: true));
        bool isolated = contacts.Count == 0 && operations is null && openings is null;
        unjoined = isolated && doorCuts is null;
        if (isolated) isolatedDoors = doorOwners;
        if (validatedCut.Junction == Guid.Empty && wall is { } targetWall)
        {
            if (ResolveJoinedWallSlabs(geometry, wallId, location, settings, targetWall, bounds, contacts, operations, basic,
                slabCutPriority, doorCuts, out bool unresolvedCoverage, out var joinedEnds) is { } jointCut)
            {
                evaluated = jointCut;
                return WallOpeningsMissEnds(openings, evaluated);
            }
            retainedCap = (CheckWallEndOpeningClearance(joinedEnds.Bottom, openings),
                CheckWallEndOpeningClearance(joinedEnds.Top, openings));
            // Separate tests of the cutters cannot establish the result of an unresolved combined cut.
            if (unresolvedCoverage || operations?.Exists(op => op.MayLoseMaterial) == true &&
                contacts.Exists(c => c.Key == WallSlabRelation || c.Key == WallBeamRelation || c.Key == WallColumnRelation))
                return false;
        }
        if (doorCuts is not null) blocked.AddRange(doorCuts);
        // A slab cut can account for beam contacts that cannot be resolved in isolation.
        foreach (var (id, key, priority) in contacts.OrderBy(c => c.Key == WallBeamRelation))
        {
            if ((key == WallSlabRelation || key == WallBeamRelation) &&
                    (id == validatedCut.Junction || id == validatedCut.OtherEndJunction) ||
                key == WallBeamRelation && ((validatedCut.BeamJunctions?.Contains(id) ?? false) ||
                    (cutter.BeamJunctions?.Contains(id) ?? false))) continue;
            if (!WallKeepsItsPlanes(geometry, wallId, id, key, priority, location, bounds, wall, basic, slabCutPriority, blocked,
                out var clipped, out var wallCut)) return false;
            if (clipped == bounds) continue;
            if (validatedCut.Junction != Guid.Empty) return false;
            if (cutter.Junction != Guid.Empty)
            {
                if (!cutter.CoversWallJoins || !wallCut.CoversWallJoins || cutter.OtherEndJunction != Guid.Empty ||
                    cutter.BeamJunctions is not null || wallCut.BeamJunctions is not null ||
                    (clipped.Bottom != bounds.Bottom) == (evaluated.Bottom != bounds.Bottom) ||
                    (clipped.Top != bounds.Top) == (evaluated.Top != bounds.Top) ||
                    CheckedElevations(Math.Max(evaluated.Bottom, clipped.Bottom), Math.Min(evaluated.Top, clipped.Top))
                        is not { } intersection || intersection.Bottom >= intersection.Top)
                    return false;
                cutter = cutter with { OtherEndJunction = wallCut.Junction };
                evaluated = intersection;
                continue;
            }
            cutter = wallCut;
            evaluated = clipped;
        }
        if (operations is not null)
        {
            if (cutter.Junction != Guid.Empty || validatedCut.Junction != Guid.Empty ||
                ResolveWallSlabOperations(geometry, location, wall!.Value, bounds, operations, blocked) is not { } operationBounds ||
                contacts.Count != 0 && operationBounds != bounds)
                return false;
            onlyWallJunctions = false;
            evaluated = operationBounds;
            return WallOpeningsMissEnds(openings, evaluated);
        }
        if (!WallOpeningsMissEnds(openings, evaluated)) return false;
        // A proven cut is checked again at the surviving heights, with fresh blocked intervals.
        if (blocked.Count == 0 || cutter.CoversWallJoins) return true;
        var (bottom, top) = WallCapsWithMaterial(wall!.Value, bounds, blocked);
        if (bottom && top) return true;
        if (cutter.Junction == Guid.Empty && validatedCut.Junction == Guid.Empty && blocked.TrueForAll(obstacle => obstacle.HasVerifiedExtent))
            retainedCap = (retainedCap.Bottom ?? (bottom ? bounds.Bottom : null), retainedCap.Top ?? (top ? bounds.Top : null));
        return false;
    }

    private static double? CheckWallEndOpeningClearance(double? elevation, List<Elevations>? openings) =>
        elevation is { } end && (openings is null ||
            openings.All(o => end < o.Bottom - JunctionTolerance || end > o.Top + JunctionTolerance)) ? end : null;

    private static bool WallOpeningsMissEnds(List<Elevations>? openings, Elevations bounds) =>
        openings is null || openings.All(o => MissesEndFaces(o.Bottom, o.Top, bounds, bounds.Bottom, bounds.Top));

    private static bool IsBasicWall(ReadOnlySpan<byte> settings) =>
        BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) == 1;

    private static uint? ReadCompositeWallCutPriority(ReadOnlySpan<byte> settings, JunctionAttributes attributes)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) != 2 ||
            attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[229..])) is not { } skins ||
            skins.Any(skin => !double.IsFinite(skin.Thickness) || skin.Thickness <= 0) ||
            !(Math.Abs(skins.Sum(skin => skin.Thickness) - ReadDouble(settings, 77)) <= JunctionTolerance))
            return null;
        // The last surviving wall skin controls the elevation of the cut end.
        return skins.Max(skin => skin.Priority);
    }

    private static uint? ReadLowestWallPriority(ReadOnlySpan<byte> settings, JunctionAttributes attributes, ReadOnlySpan<byte> profile)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(settings[227..]) != 1) return null;
        return BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) switch
        {
            1 => attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(settings[215..])),
            2 => attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[229..]))?.Min(skin => skin.Priority),
            3 => ReadWallProfilePriority(profile, attributes.Material, lowest: true),
            _ => null,
        };
    }

    private static bool TryReadGeometryRelation(ref ReadOnlySpan<byte> section, out byte tag, out Guid key,
        out Guid target)
    {
        tag = 0;
        key = target = Guid.Empty;
        if (section.Length < 17) return false;
        tag = section[0];
        key = new Guid(section.Slice(1, 16));
        var entries = section[17..];
        if (tag is 1 or 2)
        {
            if (entries.Length < 4) return false;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(entries);
            if (count > (entries.Length - 4) / 16) return false;
            section = entries[(4 + (int)count * 16)..];
            return true;
        }
        if (tag is not (0x11 or 0x12) || entries.Length < 16) return false;
        target = new Guid(entries[..16]);
        section = entries[16..];
        return true;
    }

    private static ReadOnlySpan<byte> ReadRelationIds(ReadOnlySpan<byte> entry, ReadOnlySpan<byte> remainder, byte tag) =>
        tag is 1 or 2 ? entry.Slice(21, entry.Length - remainder.Length - 21) : entry.Slice(17, 16);

    private static ReadOnlySpan<byte> AfterCountedString(ReadOnlySpan<byte> record, int offset)
    {
        if (offset > record.Length || BinaryPrimitives.ReadUInt16LittleEndian(record[(offset - 2)..]) != 1 ||
            ReadElementId(record[offset..], out int consumed) is null)
            return default;
        return record[(offset + consumed)..];
    }

    private static Elevations? CheckedElevations(double bottom, double top) =>
        double.IsFinite(bottom) && double.IsFinite(top) ? new Elevations(bottom, top) : null;

    private static double? ToProjectZero(double? value, double? storyElevation)
    {
        double? sum = value + storyElevation;
        return sum is { } z && double.IsFinite(z) ? z : null;
    }
}
