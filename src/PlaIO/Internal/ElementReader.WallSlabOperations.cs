using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private readonly record struct WallSlabOperation(Elevations Planes, IReadOnlyList<IReadOnlyList<PlaPlanPoint>> Rings,
        int ExtrusionDirection = 0, uint Priority = uint.MaxValue, bool MayLoseMaterial = false);
    private readonly record struct WallSlabSweep(Elevations Bounds, bool BottomConfirmed, bool TopConfirmed);

    private static List<WallSlabOperation>? ReadWallSlabOperations(ModelGeometry geometry, Guid wall, ReadOnlySpan<byte> pairs,
        (double X0, double Y0, double X1, double Y1) footprint)
    {
        if (pairs.IsEmpty || pairs.Length % 32 != 0) return null;
        var operands = new Dictionary<Guid, (Guid Link, int Direction)>();
        for (int i = 0; i < pairs.Length; i += 32)
        {
            var slab = new Guid(pairs.Slice(i, 16));
            var operation = new Guid(pairs.Slice(i + 16, 16));
            if (ReadSubtractionDirection(geometry, wall, slab, operation) is not { } direction ||
                !operands.TryAdd(slab, (operation, direction))) return null;
        }
        var operations = new List<WallSlabOperation>();
        var nested = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var (slab, (operation, direction)) in operands)
        {
            if (geometry.Read(slab, SlabClass) is not { } operand) return null;
            var record = operand.AsSpan(FindClassFrame(operand, SlabClass));
            var settings = AfterCountedString(record, ClassElementIdOffset("Slab"));
            if (ReadWallSlabPlan(geometry, record, settings, out var planes) is not { } rings)
                return null;

            uint? priority = ReadSlabOperandPriority(settings, geometry.Attributes);
            if (priority is not > 0) return null;
            bool mayLoseMaterial = false;
            bool hasReciprocal = false;
            int? slabLayer = ReadElementLocation(record, "Slab", 0).LayerIndex;
            if (!HasSupportedSlabContacts(record, Guid.Empty, Guid.Empty,
                    (role, id) => role == SlabWallRelation && SlabJunctionIsInactive(geometry, slab, slabLayer, id, fromWall: false) ||
                        SlabContactKeepsCuttingBand(geometry, slab, role, id, priority.Value, 0, footprint, planes) ||
                        WallMayCutOperator(role, id),
                    acceptsOperatorTarget: (target, link) =>
                    {
                        if (!IsSlabOperatorTarget(geometry, target, slab, link)) return false;
                        if (target != wall) return true;
                        if (hasReciprocal || link != operation) return false;
                        hasReciprocal = true;
                        return true;
                    },
                    acceptsTargetOperand: (other, link) =>
                    {
                        // A nested subtraction cannot change the combined wall cut if the same cutter
                        // independently cuts the wall in the same direction.
                        if (!operands.TryGetValue(other, out var replacement) || replacement.Direction != direction ||
                            ReadSubtractionDirection(geometry, slab, other, link) != direction ||
                            geometry.Read(other, SlabClass) is not { } nestedOperand ||
                            !HasOperationPair(nestedOperand, slab, link, operationTag: 2))
                            return false;
                        if (!nested.TryGetValue(slab, out var cutters)) nested.Add(slab, cutters = []);
                        return cutters.Add(other);
                    }) || !hasReciprocal)
                return null;
            operations.Add(new WallSlabOperation(planes, rings, direction, MayLoseMaterial: mayLoseMaterial));

            bool WallMayCutOperator(Guid role, Guid id)
            {
                if (!HasSlabWallPeerWithPriority(geometry, slab, role, id)) return false;
                // Automatic wall cleanup can remove parts of the saved slab prism.
                mayLoseMaterial = true;
                return true;
            }
        }

        if (nested.Count == 0) return operations;
        var ready = new Queue<Guid>(operands.Keys.Where(slab => !nested.ContainsKey(slab)));
        var dependents = nested.SelectMany(pair => pair.Value.Select(cutter => (Cutter: cutter, Dependent: pair.Key)))
            .ToLookup(edge => edge.Cutter, edge => edge.Dependent);
        int verified = 0;
        while (ready.TryDequeue(out var cutter))
        {
            verified++;
            foreach (var dependent in dependents[cutter])
                if (nested[dependent].Remove(cutter) && nested[dependent].Count == 0) ready.Enqueue(dependent);
        }
        return verified == operands.Count ? operations : null;
    }

    private static bool IsSlabOperatorTarget(ModelGeometry geometry, Guid target, Guid slab, Guid link) =>
        ReadSubtractionDirection(geometry, target, slab, link) is not null &&
        (geometry.Read(target, WallClass) ?? geometry.Read(target, SlabClass)) is { } other && HasOperationPair(other, slab, link);

    private static bool WallIsSubtractionOperator(ModelGeometry geometry, Guid wall, ReadOnlySpan<byte> pairs)
    {
        if (pairs.IsEmpty || pairs.Length % 32 != 0) return false;
        for (int i = 0; i < pairs.Length; i += 32)
        {
            var target = new Guid(pairs.Slice(i, 16));
            var operation = new Guid(pairs.Slice(i + 16, 16));
            if (ReadSubtractionDirection(geometry, target, wall, operation) is null ||
                (geometry.Read(target, ElementAssemblyClass) ?? geometry.Read(target, SlabClass) ??
                    geometry.Read(target, WallClass)) is not { } body ||
                !HasOperationPair(body, wall, operation)) return false;
        }
        return true;
    }

    private static bool SlabJunctionIsInactive(ModelGeometry geometry, Guid owner, int? ownerLayer, Guid junction, bool fromWall)
    {
        var (role, peerRole, peerClass, peerType) = fromWall ? (WallSlabRelation, SlabWallRelation, SlabClass, "Slab") :
            (SlabWallRelation, WallSlabRelation, WallClass, "Wall");
        if (ownerLayer is not { } layer || geometry.Attributes.LayerGroup.GetValueOrDefault(layer) is not { } group ||
            geometry.Read(junction, WallSlabJunctionClass) is not { } link ||
            ReadSingleRelation(link, role, requireSection: true) != owner)
            return false;
        Guid peerId = ReadSingleRelation(link, peerRole, requireSection: true);
        if (geometry.Read(peerId, peerClass) is not { } peer || !HasGeometryRelation(peer, peerRole, junction) ||
            ReadElementLocation(peer.AsSpan(FindClassFrame(peer, peerClass)), peerType, 0).LayerIndex is not { } peerLayer ||
            geometry.Attributes.LayerGroup.GetValueOrDefault(peerLayer) is not { } peerGroup) return false;
        return group == 0 || group != peerGroup;
    }

    private static List<WallSlabOperation>? ReadWallJointSlabBands(ModelGeometry geometry, ElementLocation location, Guid wall,
        WallStrip wallStrip, Elevations bounds, List<(Guid Id, Guid Key, uint Priority)> contacts, bool basic,
        bool allowSharedBeamCoverage,
        out List<(WallSlabOperation Cut, bool IsBottom)> completeCaps)
    {
        completeCaps = [];
        if (geometry.StoryElevation(location.HomeStoryIndex) is not { } home) return null;
        var bands = new List<WallSlabOperation>();
        foreach (var (joint, _, priority) in contacts)
        {
            if (ReadWallSlabContact(geometry, wall, joint, out Guid slab) is not { } body) return null;
            var record = body.AsSpan(FindClassFrame(body, SlabClass));
            int? slabLayer = ReadElementLocation(record, "Slab", 0).LayerIndex;
            if (location.LayerIndex is not { } wallLayer || slabLayer is not { } layer ||
                geometry.Attributes.LayerGroup.GetValueOrDefault(wallLayer) is not { } wallGroup ||
                geometry.Attributes.LayerGroup.GetValueOrDefault(layer) is not { } slabGroup) return null;
            if (wallGroup == 0 || wallGroup != slabGroup) continue;
            var settings = AfterCountedString(record, ClassElementIdOffset("Slab"));
            if (ReadSlabPriority(settings, geometry.Attributes) is not { } maximum) return null;
            if (maximum < priority) continue;
            if (ReadWallSlabPlan(geometry, record, settings, out var planes) is not { } rings) return null;
            bool composite = BinaryPrimitives.ReadUInt16LittleEndian(settings[83..]) == 2;
            var skins = !composite
                ? [new SlabSkin(planes.Top - planes.Bottom, maximum)]
                : geometry.Attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[87..]));
            if (skins is null) return null;
            double high = planes.Top, cuttingBottom = double.PositiveInfinity, cuttingTop = double.NegativeInfinity;
            foreach (var skin in skins)
            {
                if (!double.IsFinite(skin.Thickness) || skin.Thickness <= 0) return null;
                double low = high - skin.Thickness;
                // Only the part of a slab skin inside the wall's height can lose cutting coverage.
                var band = new Elevations(Math.Max(low - home, bounds.Bottom), Math.Min(high - home, bounds.Top));
                if (skin.Priority >= priority && band.Top > band.Bottom)
                {
                    bool mayLoseMaterial = false;
                    if (!HasSupportedSlabContacts(record, SlabWallRelation, joint,
                        (key, id) => key == SlabWallRelation && IsSameWallSlabPair(geometry, wall, slab, id) ||
                            SlabContactKeepsCuttingBand(geometry, slab, key, id, skin.Priority, home, wallStrip.Footprint, band) ||
                            allowSharedBeamCoverage && key == SlabBeamRelation &&
                            SharedBeamPreservesSlabCoverage(geometry, wall, slab, id, location, home, priority, wallStrip.Footprint) ||
                            MayLoseAfterWallContact(key, id),
                        acceptsOperatorTarget: (target, operation) => IsSlabOperatorTarget(geometry, target, slab, operation)))
                        return null;
                    bands.Add(new WallSlabOperation(new Elevations(low, high), rings, Priority: skin.Priority,
                        MayLoseMaterial: mayLoseMaterial));
                    cuttingBottom = Math.Min(cuttingBottom, low);
                    cuttingTop = Math.Max(cuttingTop, high);

                    bool MayLoseAfterWallContact(Guid role, Guid id)
                    {
                        if (!HasSlabWallPeerWithPriority(geometry, slab, role, id)) return false;
                        mayLoseMaterial = true;
                        return true;
                    }
                }
                high = low;
            }
            if (Math.Abs(high - planes.Bottom) > JunctionTolerance) return null;
            double floor = bounds.Bottom + home, ceiling = bounds.Top + home;
            bool lowerGap = planes.Bottom <= floor + JunctionTolerance && cuttingBottom > floor + JunctionTolerance;
            bool upperGap = planes.Top >= ceiling - JunctionTolerance && cuttingTop < ceiling - JunctionTolerance;
            if (basic && composite && settings[8] == 0 && double.IsFinite(cuttingBottom) && (lowerGap || upperGap) &&
                WallPlanHasGap(wallStrip.Footprint, [rings]) is false)
            {
                // A complete slab cut can remove the wall cap beyond its cutting skins.
                if (ClipBySlab(geometry, location, record, settings, wallStrip.Footprint, bounds, bounds.Bottom, bounds.Top,
                    out var beams, priority, (wall, slab, joint), allowSharedBeamCoverage: allowSharedBeamCoverage) is not { } clip ||
                    beams is not null)
                    return null;
                if (lowerGap && clip.Bottom > bounds.Bottom && Math.Abs(clip.Bottom + home - cuttingTop) <= JunctionTolerance)
                    completeCaps.Add((new WallSlabOperation(new Elevations(floor, cuttingTop), rings, Priority: priority), true));
                if (upperGap && clip.Top < bounds.Top && Math.Abs(clip.Top + home - cuttingBottom) <= JunctionTolerance)
                    completeCaps.Add((new WallSlabOperation(new Elevations(cuttingBottom, ceiling), rings, Priority: priority), false));
            }
        }
        return bands;
    }

    private static bool HasSlabWallPeerWithPriority(ModelGeometry geometry, Guid slab, Guid role, Guid id) =>
        ReadSlabWallPeer(geometry, slab, role, id, out Guid peer) is { } body && ReadWallPriority(geometry, peer, body) is > 0;

    private static Elevations? ResolveJoinedWallSlabs(ModelGeometry geometry, Guid wall, ElementLocation location,
        ReadOnlySpan<byte> settings, WallStrip strip, Elevations bounds, List<(Guid Id, Guid Key, uint Priority)> contacts,
        List<WallSlabOperation>? operations, bool basic, uint? compositePriority, List<WallCapObstacle>? doorCuts,
        out bool unresolvedCoverage, out (double? Bottom, double? Top) verifiedEnds)
    {
        unresolvedCoverage = false;
        verifiedEnds = default;
        var slabs = contacts.Where(c => c.Key == WallSlabRelation).ToList();
        bool allowSharedBeamCoverage = basic && slabs.Count > 1 && operations is null && contacts.Exists(c => c.Key == WallBeamRelation);
        if (slabs.Count == 0 || !basic && compositePriority is null ||
            contacts.Any(c => c.Key != WallSlabRelation && c.Key != FirstWallEndRelation && c.Key != SecondWallEndRelation &&
                !(allowSharedBeamCoverage && c.Key == WallBeamRelation)) ||
            ReadJoinedWallStrip(geometry, wall, strip, location) is not { } expanded ||
            ReadWallJointSlabBands(geometry, location, wall, expanded, bounds, slabs, basic, allowSharedBeamCoverage,
                out var completeCaps) is not { Count: > 0 } bands)
            return null;
        if (operations is not null) bands.AddRange(operations);
        unresolvedCoverage = bands.Exists(b => b.MayLoseMaterial);
        uint strongest = compositePriority ?? slabs[0].Priority;
        var strongestBands = bands.Where(b => b.Priority >= strongest).ToList();
        var skins = ReadWallSkinStrips(settings, geometry.Attributes, strip, slabs[0].Priority, strongest);
        if (ResolveWallSlabOperations(geometry, location, expanded, bounds, strongestBands, []) is not { } outer)
        {
            verifiedEnds = ResolveSingleEnd();
            return null;
        }
        // Combine a separately verified cap with the sweep where the sweep already moves the opposite cap.
        var extra = completeCaps.Where(c => c.IsBottom ? outer.Top < bounds.Top : outer.Bottom > bounds.Bottom)
            .Select(c => c.Cut).ToArray();
        if (extra.Length != 0)
        {
            bands.AddRange(extra);
            strongestBands.AddRange(extra);
            if (ResolveWallSlabOperations(geometry, location, expanded, bounds, strongestBands, []) is not { } bothEnds)
                return null;
            outer = bothEnds;
        }
        if (outer == bounds && operations is null)
        {
            unresolvedCoverage = false;
            return null;
        }

        if (skins is null || ReadObstacles(outer) is not { } blocked) return null;
        if (ResolveWallSlabOperations(geometry, location, strip, bounds, bands, blocked, skins) != outer)
        {
            verifiedEnds = ResolveSingleEnd();
            return null;
        }
        return outer;

        List<WallCapObstacle>? ReadObstacles(Elevations ends)
        {
            var result = new List<WallCapObstacle>();
            if (doorCuts is not null) result.AddRange(doorCuts);
            foreach (var (id, key, priority) in contacts)
            {
                if (key == WallSlabRelation) continue;
                if (!WallKeepsItsPlanes(geometry, wall, id, key, priority, location, ends, strip, basic, compositePriority,
                    result, out var levels, out _, trackPartialBeamCuts: allowSharedBeamCoverage) || levels != ends) return null;
            }
            return result;
        }

        (double? Bottom, double? Top) ResolveSingleEnd()
        {
            if (skins is null || allowSharedBeamCoverage || completeCaps.Count != 0 ||
                geometry.StoryElevation(location.HomeStoryIndex) is not { } home ||
                SweepWallSlabOperations(home, expanded, bounds, strongestBands.Where(b => !b.MayLoseMaterial), [], null)
                    is not { Bounds: var envelope } ||
                ReadObstacles(envelope) is not { } obstacles ||
                SweepWallSlabOperations(home, strip, bounds, bands, obstacles, skins, confirmEndSections: true) is not { } inner)
                return default;

            // The expanded wall with only guaranteed cuts encloses the evaluated wall.
            // Material under every possible cut in the authored section is inside it.
            return (inner.BottomConfirmed && inner.Bounds.Bottom == envelope.Bottom ? envelope.Bottom : null,
                inner.TopConfirmed && inner.Bounds.Top == envelope.Top ? envelope.Top : null);
        }
    }

    private static (WallStrip Strip, uint Priority)[]? ReadWallSkinStrips(ReadOnlySpan<byte> settings, JunctionAttributes attributes,
        WallStrip strip, uint lowest, uint highest)
    {
        if (lowest == highest) return [(strip, highest)];
        if (ReadWallReferencePlan(settings, out _) is not { } plan ||
            attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[229..])) is not { } skins)
            return null;
        double normal = strip.AlongX ? Math.Sign(plan.Start.X - plan.End.X) : Math.Sign(plan.End.Y - plan.Start.Y);
        double edge = strip.Line + normal * plan.OutsideOffset;
        double inside = strip.Line + normal * plan.InsideOffset;
        if (Math.Abs(Math.Min(edge, inside) - strip.AcrossMin) > JunctionTolerance ||
            Math.Abs(Math.Max(edge, inside) - strip.AcrossMax) > JunctionTolerance) return null;
        double sign = Math.Sign(inside - edge);
        var result = new (WallStrip Strip, uint Priority)[skins.Length];
        for (int i = 0; i < skins.Length; i++)
        {
            var skin = skins[i];
            double next = edge + sign * skin.Thickness;
            result[i] = (strip with { AcrossMin = Math.Min(edge, next), AcrossMax = Math.Max(edge, next) }, skin.Priority);
            edge = next;
        }
        return Math.Abs(edge - inside) <= JunctionTolerance ? result : null;
    }

    private static IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? ReadWallSlabPlan(ModelGeometry geometry,
        ReadOnlySpan<byte> record, ReadOnlySpan<byte> settings, out Elevations planes)
    {
        planes = default;
        var polygon = ReadSlabOutline(settings);
        if (geometry.StoryElevation(ReadElementLocation(record, "Slab", 0).HomeStoryIndex) is not { } home ||
            ReadElevations(record, "Slab") is not { } relative || ReadOutlineBounds(polygon) is null ||
            ReadStraightRings(polygon, out _) is not { } rings ||
            !HasVerticalSlabEdges(polygon[(int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[4..])..],
                (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[43..]) - rings.Count) ||
            CheckedElevations(relative.Bottom + home, relative.Top + home) is not { } world || world.Top <= world.Bottom)
            return null;
        planes = world;
        return rings;
    }

    private static uint? ReadSlabOperandPriority(ReadOnlySpan<byte> settings, JunctionAttributes attributes)
    {
        if (ReadSlabPriority(settings, attributes) is not { } priority) return null;
        return BinaryPrimitives.ReadUInt16LittleEndian(settings[83..]) == 1 ? priority :
            attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[87..]))?.Min(s => s.Priority);
    }

    private static int? ReadSubtractionDirection(ModelGeometry geometry, Guid target, Guid cutter, Guid operation)
    {
        if (target == Guid.Empty || cutter == Guid.Empty || target == cutter ||
            geometry.Read(operation, ElementAssemblyMemberClass) is not { Length: 144 } body ||
            FindRelationshipSection(body, body.Length) != 90)
            return null;
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(52));
        double z = ReadDouble(body, 76);
        // Both extrusion directions share the same operation code.
        if (!(type == 1 && z == 0 || type == 3 && z is -1 or 1) ||
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(56)) != 1 || ReadDouble(body, 60) != 0 ||
            ReadDouble(body, 68) != 0) return null;
        var data = body.AsSpan(90);
        return data[0] == 4 && new Guid(data.Slice(1, 16)) == ElementAssemblyRelation &&
            BinaryPrimitives.ReadUInt32LittleEndian(data[17..]) == 2 && new Guid(data[21..37]) == target &&
            new Guid(data[37..53]) == cutter && data[53] == 0 ? (int)z : null;
    }

    private static bool HasOperationPair(ReadOnlySpan<byte> record, Guid other, Guid operation, byte operationTag = 1)
    {
        int start = FindRelationshipSection(record, record.Length);
        if (start < 0) return false;
        var data = record[start..];
        int occurrences = 0;
        while (data.Length >= 17)
        {
            var entry = data;
            if (!TryReadGeometryRelation(ref data, out byte tag, out Guid key, out _)) return false;
            if (key != ElementAssemblyRelation) continue;
            var pairs = ReadRelationIds(entry, data, tag);
            if (tag is not (1 or 2) || pairs.IsEmpty || pairs.Length % 32 != 0) return false;
            if (tag != operationTag) continue;
            for (int i = 0; i < pairs.Length; i += 32)
                if (new Guid(pairs.Slice(i, 16)) == other && new Guid(pairs.Slice(i + 16, 16)) == operation) occurrences++;
        }
        return occurrences == 1 && data is [0];
    }

    private static Elevations? ResolveWallSlabOperations(ModelGeometry geometry, ElementLocation location, WallStrip wall,
        Elevations bounds, List<WallSlabOperation> operations, List<WallCapObstacle> blocked,
        (WallStrip Strip, uint Priority)[]? skins = null)
    {
        if (geometry.StoryElevation(location.HomeStoryIndex) is not { } home ||
            wall.End - wall.Start <= JunctionTolerance || wall.AcrossMax - wall.AcrossMin <= JunctionTolerance) return null;
        bool uncertain = operations.Exists(op => op.MayLoseMaterial);
        if (SweepWallSlabOperations(home, wall, bounds, operations, blocked, skins, confirmEndSections: uncertain)
            is not { BottomConfirmed: true, TopConfirmed: true, Bounds: var complete }) return null;
        // Bracket the effects of edited operators by their original prisms and by omitting them.
        if (!uncertain) return complete;
        return SweepWallSlabOperations(home, wall, bounds, operations.Where(op => !op.MayLoseMaterial), blocked, skins)?.Bounds == complete
            ? complete : null;
    }

    private static WallSlabSweep? SweepWallSlabOperations(double home, WallStrip wall, Elevations bounds,
        IEnumerable<WallSlabOperation> operations, List<WallCapObstacle> blocked, (WallStrip Strip, uint Priority)[]? skins,
        bool confirmEndSections = false)
    {
        double floor = bounds.Bottom + home, ceiling = bounds.Top + home;
        var cuts = operations.Select(o => (Low: o.ExtrusionDirection < 0 ? floor : Math.Max(floor, o.Planes.Bottom),
            High: o.ExtrusionDirection > 0 ? ceiling : Math.Min(ceiling, o.Planes.Top), o.Rings, o.Priority)).Where(m => m.High > m.Low).ToArray();
        var heights = cuts.SelectMany(m => new[] { m.Low, m.High }).Append(floor).Append(ceiling).Distinct().Order().ToArray();
        var obstacles = blocked.Select(m => RectangleRing((wall with { Start = m.Span.Start, End = m.Span.End }).Footprint)).ToArray();
        double bottom = double.PositiveInfinity, top = double.NegativeInfinity;
        bool bottomConfirmed = false, topConfirmed = false;
        for (int i = 1; i < heights.Length; i++)
        {
            double lo = heights[i - 1], hi = heights[i];
            if (hi <= lo + JunctionTolerance) continue;
            var active = cuts.Where(m => m.Low <= lo && m.High >= hi).ToArray();
            bool survives = false;
            bool confirmed = false;
            foreach (var (strip, priority) in skins ?? [(wall, 0u)])
            {
                var masks = active.Where(m => m.Priority >= priority).Select(m => m.Rings).Concat(obstacles).ToArray();
                if (WallPlanHasGap(strip.Footprint, masks) is not { } hasGap) return null;
                if (!hasGap) continue;
                survives = true;
                confirmed = !confirmEndSections || HasWallCapPatch(strip, (strip.Start, strip.End), masks);
                if (confirmed) break;
            }
            if (!survives) continue;
            if (lo < bottom)
            {
                bottom = lo;
                bottomConfirmed = confirmed;
            }
            top = hi;
            topConfirmed = confirmed;
        }
        return CheckedElevations(Math.Abs(bottom - floor) <= JunctionTolerance ? bounds.Bottom : bottom - home,
            Math.Abs(top - ceiling) <= JunctionTolerance ? bounds.Top : top - home) is { } ends
            ? new WallSlabSweep(ends, bottomConfirmed, topConfirmed) : null;
    }

    private static IReadOnlyList<IReadOnlyList<PlaPlanPoint>> RectangleRing((double X0, double Y0, double X1, double Y1) box) =>
        [[new(box.X0, box.Y0), new(box.X1, box.Y0), new(box.X1, box.Y1), new(box.X0, box.Y1)]];

    private static bool? WallPlanHasGap((double X0, double Y0, double X1, double Y1) box,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<PlaPlanPoint>>> masks, Func<PlaPlanPoint, bool>? acceptsGap = null)
    {
        if (masks.Count == 0) return true;
        if (masks.Sum(mask => mask.Sum(r => (long)r.Count)) > 4096) return null;
        var perMask = masks.Select(mask => mask.SelectMany(r => r.Select((p, i) => new PlanarEdge(p, r[(i + 1) % r.Count]))).ToArray()).ToArray();
        var boundary = RectangleRing(box)[0];
        var edges = perMask.SelectMany(mask => mask).Concat(boundary.Select((p, i) => new PlanarEdge(p, boundary[(i + 1) % 4]))).ToArray();
        for (int i = 0; i < edges.Length; i++)
            for (int j = i + 1; j < edges.Length; j++)
                SplitPlanarEdges(edges[i], edges[j]);

        // Between consecutive vertices and crossings, the order of all boundary edges is fixed.
        var slices = edges.SelectMany(e => e.Nodes.Select(n => n.Point.X)).Where(x => x > box.X0 && x < box.X1)
            .Append(box.X0).Append(box.X1).Distinct().Order().ToArray();
        for (int i = 1; i < slices.Length; i++)
        {
            if (slices[i] <= slices[i - 1] + JunctionTolerance) continue;
            double x = slices[i - 1] + (slices[i] - slices[i - 1]) / 2;
            var intervals = new List<(double Start, double End)>();
            foreach (var mask in perMask)
            {
                var crossings = mask.Where(e => (e.Start.X > x) != (e.End.X > x)).Select(e =>
                    e.Start.Y + (x - e.Start.X) / (e.End.X - e.Start.X) * (e.End.Y - e.Start.Y)).Order().ToArray();
                if (crossings.Length % 2 != 0 || crossings.Any(y => !double.IsFinite(y))) return null;
                for (int j = 0; j < crossings.Length; j += 2) intervals.Add((crossings[j], crossings[j + 1]));
            }
            double end = box.Y0;
            foreach (var (lo, hi) in intervals.OrderBy(p => p.Start))
            {
                double stop = Math.Min(lo, box.Y1);
                if (stop > end + JunctionTolerance && AcceptsGap(x, end, stop)) return true;
                end = Math.Max(end, hi);
                if (end >= box.Y1) break;
            }
            if (end < box.Y1 - JunctionTolerance && AcceptsGap(x, end, box.Y1)) return true;
        }
        return false;

        bool AcceptsGap(double x, double low, double high) =>
            acceptsGap is null || acceptsGap(new PlaPlanPoint(x, low + (high - low) / 2));
    }
}
