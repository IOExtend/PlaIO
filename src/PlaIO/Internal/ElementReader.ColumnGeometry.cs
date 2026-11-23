using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid ColumnMemoClass = new("7a1a82ab-69cd-40e6-a7d8-76564f981cf9");
    private static readonly Guid RectangularSectionClass = new("c4191955-87c6-4fb6-b074-5f094fe9f6a6");
    private static readonly Guid ColumnMemoRelation = new("27448621-9adc-43e6-b5aa-fc76e4cd62ce");
    private static readonly Guid SectionProfileRelation = new("63b86c30-a221-421c-84fb-926c694bf611");
    private static readonly Guid FirstColumnPartRelation = new("4e52e27f-cbac-485f-a3a3-89124756a025");
    private static readonly Guid SecondColumnPartRelation = new("d004bcd5-41ed-4ee8-99cb-99da69422f60");
    private static readonly Guid ColumnPartsRelation = new("d5e668e5-acee-4e89-ba9a-47b031d7cf69");
    private static readonly Guid AttributeOwnerRelation = new("f367a8af-cd22-4941-9dc2-71def6f2d0a9");
    private static readonly Guid ColumnPropertySetRelation = new("2107926d-a8fd-4021-a0eb-cce5950eed37");
    private static readonly Guid LinkedElementSettingsRelation = new("f732f229-4ef9-4007-b0e4-6ca945917a3e");
    private static ReadOnlySpan<byte> ColumnEndPlaneFrame => [1, 0, 2, 0, 0, 0, 1, 0];
    private static ReadOnlySpan<byte> SectionVectorFrame => [2, 0, 9, 1];

    private static Elevations? ReadColumnElevations(ReadOnlySpan<byte> column, Guid columnId, ElementLocation location,
        ModelGeometry geometry, PlaMemberProfile? profile, out PlaBoundingBox? boundingBox, out PlaColumnExtrusion? extrusion,
        out PlaColumnCutMesh? cutMesh)
    {
        boundingBox = null;
        extrusion = null;
        cutMesh = null;
        if (column.Length < 426 || !column.Slice(304, ColumnEndPlaneFrame.Length).SequenceEqual(ColumnEndPlaneFrame) ||
            BinaryPrimitives.ReadUInt16LittleEndian(column[368..]) != 1)
            return null;

        var lower = column[312..];
        var upper = column[370..];
        double x = BinaryPrimitives.ReadDoubleLittleEndian(lower);
        double y = BinaryPrimitives.ReadDoubleLittleEndian(lower[8..]);
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            x != BinaryPrimitives.ReadDoubleLittleEndian(upper) || y != BinaryPrimitives.ReadDoubleLittleEndian(upper[8..]))
            return null;

        Guid memoId = ReadColumnPrismMemo(column, columnId, geometry);
        if (memoId == Guid.Empty) return null;
        var memo = geometry.Read(memoId, ColumnMemoClass);
        if (memo is null || ReadColumnSection(memo, columnId, out var junctions) is not { } sectionId) return null;
        var section = geometry.Read(sectionId, RectangularSectionClass);
        var settings = AfterCountedString(column, 735);
        int settingsEnd = FindRelationshipSection(column, column.Length) - 6;
        bool hasPlacementSettings = settings.Length >= 95 && settingsEnd - (column.Length - settings.Length) >= 95;
        double angle = hasPlacementSettings &&
            column[721..].StartsWith(ElementIdFrame) ? ReadDouble(settings, 87) : double.NaN;
        if (section is null && geometry.Read(sectionId, CircularColumnSectionClass) is { } circular)
            return ReadCircularColumn(memoId, circular, lower, upper, new PlaPlanPoint(x, y), location, geometry, junctions,
                out boundingBox, out extrusion);
        int material;
        (double X, double Y)? halfSize = null;
        PlaBoundingBox? profileBox = null;
        if (section is not null)
        {
            if (ReadSingleRelation(section, SectionProfileRelation, requireSection: true) != memoId ||
                (halfSize = ReadCenteredRectangle(section, out material)) is null) return null;
        }
        else
        {
            var hatch = ReadComplexSectionHatch(geometry, memoId, sectionId);
            if (hatch.IsEmpty || !HasSectionArea(hatch)) return null;
            if (profile is not null && double.IsFinite(angle) && ReadDouble(lower, 40) < 0 && ReadDouble(upper, 40) > 0)
                profileBox = ReadProfileColumnBoundingBox(settings, profile, x, y, angle, ReadDouble(lower, 16),
                    ReadDouble(upper, 16), geometry.StoryElevation(location.HomeStoryIndex));
            if (!IsHorizontal(lower) || !IsHorizontal(upper))
            {
                cutMesh = ReadObliqueColumn(column, hatch, profile, memoId, junctions, location, geometry, angle);
                if (cutMesh is not null) boundingBox = profileBox;
                return cutMesh is null ? null : new Elevations(cutMesh.BoundingBox.ZMin - cutMesh.Origin.Z,
                    cutMesh.BoundingBox.ZMax - cutMesh.Origin.Z);
            }
            material = BinaryPrimitives.ReadInt32LittleEndian(hatch[18..]);
        }

        double bottomRadius = halfSize is { } rectangle ? ReadPlaneRadius(lower, rectangle) : 0;
        double topRadius = halfSize is { } dimensions ? ReadPlaneRadius(upper, dimensions) : 0;
        bool unrotated = column.Length >= 735 && column[721..].StartsWith(ElementIdFrame) &&
            settings.Length >= 36 && BinaryPrimitives.ReadDoubleLittleEndian(settings[28..]) == 0;
        if ((bottomRadius != 0 || topRadius != 0) && !unrotated) return null;

        double bottom = BinaryPrimitives.ReadDoubleLittleEndian(lower[16..]);
        double top = BinaryPrimitives.ReadDoubleLittleEndian(upper[16..]);
        if (BinaryPrimitives.ReadDoubleLittleEndian(lower[40..]) >= 0 ||
            BinaryPrimitives.ReadDoubleLittleEndian(upper[40..]) <= 0 ||
            !(top - bottom > bottomRadius + topRadius))
            return null;
        var bounds = CheckedElevations(bottom - bottomRadius, top + topRadius);
        if (bounds is not { } extents) return null;
        bool horizontal = bottomRadius == 0 && topRadius == 0;
        bool solid = double.IsFinite(angle) && (angle == 0 || horizontal) && (halfSize.HasValue || profile is not null);
        bool keepsNominalBox = junctions is null;
        if (junctions is not null)
        {
            var plan = solid && horizontal && halfSize is { } half
                ? new RectangularColumnPlan(new PlaPlanPoint(x, y), half.X, half.Y, angle) : (RectangularColumnPlan?)null;
            bounds = ResolveColumnJunctions(geometry, memoId, junctions, location, material, extents,
                unrotated && halfSize is { } size ? (x - size.X, y - size.Y, x + size.X, y + size.Y) : null,
                bottom + bottomRadius, top - topRadius, section is null, solid, out keepsNominalBox, out solid, plan);
        }
        Elevations[]? bands = null;
        List<ColumnBeamMask>? partialMasks = null;
        if (!solid && horizontal && double.IsFinite(angle) && section is null && profile is not null && junctions is not null &&
            geometry.StoryElevation(location.HomeStoryIndex) is { } level &&
            geometry.Attributes.Material.GetValueOrDefault(material) is > 0 and { } priority &&
            ReadColumnFootprint(column, ReadComplexSectionHatch(geometry, memoId, sectionId), angle) is { } painted &&
            ReadColumnBeamBands(geometry, memoId, junctions, location, priority, level, extents, painted,
                new PlaPlanPoint(x, y), out partialMasks) is { } surviving)
        {
            bands = surviving;
            bounds = new Elevations(bands[0].Bottom, bands[^1].Top);
            solid = true;
        }
        PlaPlanPoint[]? cutSection = null;
        if (partialMasks is { Count: <= 48 } && profile is not null && ReadStraightProfileSection(profile.Components) is { Count: 1 } rings)
            cutSection = OrientColumnSection(rings[0], angle);
        if (!solid && horizontal && double.IsFinite(angle) && halfSize is { } rectangleSize && junctions is not null &&
            geometry.StoryElevation(location.HomeStoryIndex) is { } story &&
            geometry.Attributes.Material.GetValueOrDefault(material) is > 0 and { } columnPriority)
        {
            var (w, d) = rectangleSize;
            var ring = OrientColumnSection([new(-w, -d), new(w, -d), new(w, d), new(-w, d)], angle);
            partialMasks = ReadCoextensiveColumnBeamCut(geometry, memoId, junctions, location, columnPriority, story,
                extents, new PlaPlanPoint(x, y), ring);
            if (partialMasks is not null) cutSection = ring;
        }
        if (cutSection is { } ringOutline && partialMasks is not null &&
            geometry.StoryElevation(location.HomeStoryIndex) is { } cutHome)
        {
            double x0 = ringOutline.Min(p => p.X), x1 = ringOutline.Max(p => p.X);
            double y0 = ringOutline.Min(p => p.Y), y1 = ringOutline.Max(p => p.Y);
            PlaPlanPoint[] envelope = [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];
            var origin = new PlaColumnMeshPoint(x, y, cutHome);
            cutMesh = BuildColumnCutMesh(envelope, extents.Bottom, extents.Top, partialMasks, origin,
                static (p, z) => new PlaColumnMeshPoint(p.X, p.Y, z), ringOutline);
            if (cutMesh is { } cut)
            {
                var box = cut.BoundingBox;
                if (section is null)
                    boundingBox = profileBox;
                else if (halfSize.HasValue && Math.Abs(box.XMin - (x + x0)) <= JunctionTolerance && Math.Abs(box.XMax - (x + x1)) <= JunctionTolerance &&
                    Math.Abs(box.YMin - (y + y0)) <= JunctionTolerance && Math.Abs(box.YMax - (y + y1)) <= JunctionTolerance &&
                    Math.Abs(box.ZMin - (cutHome + extents.Bottom)) <= JunctionTolerance &&
                    Math.Abs(box.ZMax - (cutHome + extents.Top)) <= JunctionTolerance)
                    boundingBox = box;
                return new Elevations(cut.BoundingBox.ZMin - cutHome, cut.BoundingBox.ZMax - cutHome);
            }
        }
        if (section is null && (junctions is null || solid))
            boundingBox = profileBox;
        else if (section is not null && keepsNominalBox && settings.Length >= 95)
        {
            var outline = ReadSectionHatch(section, 141, 482);
            double savedAngle = ReadDouble(settings, 87);
            if ((savedAngle == 0 || horizontal) && ReadColumnFootprint(column, outline, savedAngle) is { } plan)
                boundingBox = ProjectBoundingBox(plan, extents, geometry.StoryElevation(location.HomeStoryIndex));
        }
        if (solid && bounds is { } evaluated && (bands is not null || evaluated == extents || angle == 0 &&
            (evaluated.Bottom == extents.Bottom || evaluated.Bottom >= bottom + bottomRadius) &&
            (evaluated.Top == extents.Top || evaluated.Top <= top - topRadius)) &&
            geometry.StoryElevation(location.HomeStoryIndex) is { } home)
        {
            var low = ReadColumnEndPlane(lower, evaluated.Bottom, extents.Bottom, home);
            var high = ReadColumnEndPlane(upper, evaluated.Top, extents.Top, home);
            extrusion = ReadColumnExtrusion(new PlaPlanPoint(x, y), halfSize, profile, angle, low, high, evaluated, home, bands);
            if (section is not null) boundingBox ??= extrusion?.BoundingBox;
        }
        return bounds;
    }

    private static PlaBoundingBox? ReadProfileColumnBoundingBox(ReadOnlySpan<byte> settings, PlaMemberProfile profile,
        double x, double y, double angle, double bottom, double top, double? home)
    {
        if (!(top > bottom) || settings[0] is not (1 or 4)) return null;
        var (sin, cos) = Math.SinCos(angle);
        if (ProjectBeamSection(default, profile.Components, cos, -sin) is not { } acrossX ||
            ProjectBeamSection(default, profile.Components, sin, cos) is not { } acrossY) return null;

        if (settings[0] == 4)
        {
            double extension = ReadDouble(settings, 69);
            if (!double.IsFinite(extension) || extension < 0 ||
                ProjectBeamSection(default, profile.Components, 1, 0) is not { } u ||
                ProjectBeamSection(default, profile.Components, 0, 1) is not { } v ||
                Math.Abs(u.Max + u.Min) > JunctionTolerance || Math.Abs(v.Max + v.Min) > JunctionTolerance)
                return null;

            // The element box includes the four ends of the floor-plan crosshair.
            double halfU = u.Max + extension, halfV = v.Max + extension;
            double reachX = Math.Max(halfU * Math.Abs(cos), halfV * Math.Abs(sin));
            double reachY = Math.Max(halfU * Math.Abs(sin), halfV * Math.Abs(cos));
            acrossX = (Math.Min(acrossX.Min, -reachX), Math.Max(acrossX.Max, reachX));
            acrossY = (Math.Min(acrossY.Min, -reachY), Math.Max(acrossY.Max, reachY));
        }

        return ProjectBoundingBox((x + acrossX.Min, y + acrossY.Min, x + acrossX.Max, y + acrossY.Max), new(bottom, top), home);
    }

    private static PlaPlanPoint[] OrientColumnSection(IReadOnlyList<PlaPlanPoint> section, double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return section.Select(p => new PlaPlanPoint(cos * p.X - sin * p.Y, sin * p.X + cos * p.Y)).ToArray();
    }

    private static PlaColumnEndPlane ReadColumnEndPlane(ReadOnlySpan<byte> plane, double evaluated, double nominal, double home)
    {
        if (evaluated != nominal) return new PlaColumnEndPlane(evaluated + home, 0, 0);
        double normalZ = ReadDouble(plane, 40);
        return new PlaColumnEndPlane(ReadDouble(plane, 16) + home, -ReadDouble(plane, 24) / normalZ, -ReadDouble(plane, 32) / normalZ);
    }

    private static PlaColumnExtrusion? ReadColumnExtrusion(PlaPlanPoint origin, (double X, double Y)? halfSize,
        PlaMemberProfile? profile, double angle, PlaColumnEndPlane lower, PlaColumnEndPlane upper,
        Elevations extents, double home, Elevations[]? bands)
    {
        IReadOnlyList<IReadOnlyList<PlaPlanPoint>>? rings = halfSize is { } size
            ? [[new(-size.X, -size.Y), new(size.X, -size.Y), new(size.X, size.Y), new(-size.X, size.Y)]]
            : profile is not null ? ReadStraightProfileSection(profile.Components) : null;
        if (rings is null) return null;
        // For a centered rectangle the mean of either planar cap height equals its height at the axis.
        double height = bands is null ? upper.Elevation - lower.Elevation : bands.Sum(b => b.Top - b.Bottom);
        double volume = rings.Sum(SignedRingArea) * height;
        if (!double.IsFinite(volume) || volume <= 0) return null;

        var (sin, cos) = Math.SinCos(angle);
        var oriented = rings.Select(ring => (IReadOnlyList<PlaPlanPoint>)Array.AsReadOnly(ring.Select(p =>
            new PlaPlanPoint(cos * p.X - sin * p.Y, sin * p.X + cos * p.Y)).ToArray())).ToArray();
        if (GroupPlanarContours(oriented) is not { } groups) return null;
        var points = oriented.SelectMany(ring => ring).ToArray();
        var plan = (origin.X + points.Min(p => p.X), origin.Y + points.Min(p => p.Y),
            origin.X + points.Max(p => p.X), origin.Y + points.Max(p => p.Y));
        return ProjectBoundingBox(plan, extents, home) is { } box
            ? new PlaColumnExtrusion(origin, Array.AsReadOnly(groups.Select(g => new PlaColumnExtrusionSection(g.AsReadOnly())).ToArray()),
                lower, upper, box, volume)
            {
                Spans = bands is { Length: > 1 }
                    ? Array.AsReadOnly(bands.Select(b => new PlaColumnSpan(b.Bottom + home, b.Top + home)).ToArray()) : null,
            }
            : null;
    }

    private static bool SameColumnExtrusion(PlaColumnExtrusion? a, PlaColumnExtrusion? b) =>
        a == b || a is not null && b is not null && a.Origin == b.Origin && a.LowerPlane == b.LowerPlane &&
        a.UpperPlane == b.UpperPlane && a.BoundingBox == b.BoundingBox && a.Volume == b.Volume && a.Sections.Count == b.Sections.Count &&
        a.CircularRadius == b.CircularRadius && a.ScheduledCapArea == b.ScheduledCapArea &&
        a.ScheduledLateralArea == b.ScheduledLateralArea &&
        (a.Spans ?? []).SequenceEqual(b.Spans ?? []) &&
        a.Sections.Zip(b.Sections).All(pair => pair.First.Rings.Count == pair.Second.Rings.Count &&
            pair.First.Rings.Zip(pair.Second.Rings).All(rings => rings.First.SequenceEqual(rings.Second)));

    private static bool SameColumnCutMesh(PlaColumnCutMesh? a, PlaColumnCutMesh? b) =>
        a == b || a is not null && b is not null && a.Origin == b.Origin && a.BoundingBox == b.BoundingBox &&
        a.Volume == b.Volume && a.Vertices.SequenceEqual(b.Vertices) && a.Indices.SequenceEqual(b.Indices);

    private static bool HasSectionArea(ReadOnlySpan<byte> hatch)
    {
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(hatch[55..]);
        var points = hatch[59..];
        double x0 = BinaryPrimitives.ReadDoubleLittleEndian(points[16..]);
        double y0 = BinaryPrimitives.ReadDoubleLittleEndian(points[24..]);
        double dx = 0, dy = 0;
        bool hasArea = false;
        for (int i = 2; i < count; i++)
        {
            double x = BinaryPrimitives.ReadDoubleLittleEndian(points[(i * 16)..]) - x0;
            double y = BinaryPrimitives.ReadDoubleLittleEndian(points[(i * 16 + 8)..]) - y0;
            if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
            if (dx == 0 && dy == 0) (dx, dy) = (x, y);
            double determinant = dx * y - dy * x;
            if (double.IsFinite(determinant) && determinant != 0) hasArea = true;
        }
        return hasArea;
    }

    private static bool IsHorizontal(ReadOnlySpan<byte> plane) =>
        BinaryPrimitives.ReadDoubleLittleEndian(plane[24..]) == 0 &&
        BinaryPrimitives.ReadDoubleLittleEndian(plane[32..]) == 0;

    private static ReadOnlySpan<byte> StraightSectionVertices(ReadOnlySpan<byte> hatch)
    {
        if (hatch.IsEmpty) return default;
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(hatch[55..]);
        var points = hatch[59..];
        int end = count * 16;
        if (points.Length - end < 6 || BinaryPrimitives.ReadUInt16LittleEndian(points[end..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(points[(end + 2)..]) != 0 ||
            points[..16].IndexOfAnyExcept((byte)0) >= 0 || !points.Slice(16, 16).SequenceEqual(points.Slice(end - 16, 16)))
            return default;
        return points.Slice(16, end - 32);
    }

    private static double ReadPlaneRadius(ReadOnlySpan<byte> plane, (double X, double Y) halfSize)
    {
        double nx = BinaryPrimitives.ReadDoubleLittleEndian(plane[24..]);
        double ny = BinaryPrimitives.ReadDoubleLittleEndian(plane[32..]);
        double nz = BinaryPrimitives.ReadDoubleLittleEndian(plane[40..]);
        return (Math.Abs(nx) * halfSize.X + Math.Abs(ny) * halfSize.Y) / Math.Abs(nz);
    }

    private static Guid ReadColumnPrismMemo(ReadOnlySpan<byte> column, Guid owner, ModelGeometry geometry) =>
        ReadPrismMemo(column, ColumnMemoRelation, acceptsReference: (role, target) => role == ColumnLabelRelation
            ? geometry.OwnsLabel(owner, target, role) : IsPrismAnnotation(geometry, owner, role, target, allowLinearDimension: true));

    private static Guid ReadPrismMemo(ReadOnlySpan<byte> record, Guid ownerRelation,
        Func<Guid, Guid, bool>? acceptsMember = null, Func<Guid, Guid, bool>? acceptsReference = null)
    {
        int start = FindRelationshipSection(record, record.Length);
        if (start < 0) return Guid.Empty;
        var data = record[start..];
        Guid memo = Guid.Empty;
        bool hasMembership = false;
        while (data.Length >= 17)
        {
            var entry = data;
            if (!TryReadGeometryRelation(ref data, out byte tag, out Guid key, out Guid target)) return Guid.Empty;
            if (tag == 0x11 && key == ownerRelation && memo == Guid.Empty && target != Guid.Empty)
                memo = target;
            else if (tag == 2 && key == ElementAssemblyRelation && acceptsMember is not null && !hasMembership &&
                     ReadRelationIds(entry, data, tag) is { Length: 32 } ids &&
                     acceptsMember(new Guid(ids[..16]), new Guid(ids[16..])))
                hasMembership = true;
            else if (tag is 1 or 0x11 && acceptsReference is not null &&
                     (key == AttachedAnnotationRelation || key == ColumnLabelRelation || AngularDimensionAnchorRelations.Contains(key)))
            {
                var annotations = ReadRelationIds(entry, data, tag);
                if (annotations.IsEmpty) return Guid.Empty;
                for (int i = 0; i < annotations.Length; i += 16)
                    if (!acceptsReference(key, new Guid(annotations.Slice(i, 16)))) return Guid.Empty;
            }
            else if ((tag is 1 or 0x11) && key == DrawingRelation ||
                     tag == 1 && (key == ColumnPartsRelation || key == ElementPropertyOverrideLink) ||
                     tag == 2 && key == AttributeOwnerRelation ||
                     tag == 0x11 && (key == DefaultElementRelation || key == ElementComponentRelation ||
                                     key == ElementSettingsRelation || key == LinkedElementSettingsRelation ||
                                     key == ColumnPropertySetRelation) ||
                     tag == 0x12 && (key == ElementPartitionRelation || key == ElementCollectionRelation ||
                                     key == DatabaseElementList || key == ColumnTopStoryRelation || key == AttributeOwnerRelation))
                continue;
            else
                return Guid.Empty;
        }
        return data is [0] ? memo : Guid.Empty;
    }

    private static Guid? ReadColumnSection(ReadOnlySpan<byte> memo, Guid columnId, out List<(Guid Key, Guid Id)>? junctions)
    {
        junctions = null;
        int start = FindRelationshipSection(memo, memo.Length);
        if (start < 0) return null;
        var data = memo[start..];
        Guid? section = null;
        bool owner = false;
        while (data.Length >= 17)
        {
            var entry = data;
            if (!TryReadGeometryRelation(ref data, out byte tag, out Guid key, out Guid target)) return null;
            if (tag == 0x12 && key == ColumnMemoRelation && target == columnId && !owner)
                owner = true;
            else if (tag == 0x11 && key == SectionProfileRelation && section is null && target != Guid.Empty)
                section = target;
            else if (tag == 1 && key == DrawingRelation)
                continue;
            else if (tag is 1 or 0x11 && (key == ColumnBeamRelation || key == ColumnSlabRelation || key == ColumnWallRelation))
            {
                var ids = tag == 1 ? entry.Slice(21, entry.Length - data.Length - 21) : entry.Slice(17, 16);
                for (int i = 0; i < ids.Length; i += 16)
                    (junctions ??= []).Add((key, new Guid(ids.Slice(i, 16))));
            }
            else if (tag != 0x11 || (key != FirstColumnPartRelation && key != SecondColumnPartRelation && key != DrawingRelation))
                return null;
        }
        return owner && data is [0] ? section : null;
    }

    private static (double X, double Y)? ReadCenteredRectangle(ReadOnlySpan<byte> section, out int material)
    {
        material = 0;
        var hatch = ReadSectionHatch(section, 141, 482);
        if (hatch.Length < 161 || BinaryPrimitives.ReadUInt32LittleEndian(hatch[55..]) != 6) return null;
        double halfWidth = BinaryPrimitives.ReadDoubleLittleEndian(section[91..]) / 2;
        double halfDepth = BinaryPrimitives.ReadDoubleLittleEndian(section[99..]) / 2;
        if (!double.IsFinite(halfWidth) || !double.IsFinite(halfDepth) || halfWidth <= 0 || halfDepth <= 0) return null;

        var points = hatch[59..];
        if (BinaryPrimitives.ReadUInt16LittleEndian(points[96..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(points[98..]) != 0 ||
            points[..16].IndexOfAnyExcept((byte)0) >= 0 || !points.Slice(16, 16).SequenceEqual(points.Slice(80, 16)))
            return null;
        material = BinaryPrimitives.ReadInt32LittleEndian(hatch[18..]);
        return IsCorner(points[16..], -halfWidth, -halfDepth) && IsCorner(points[32..], halfWidth, -halfDepth) &&
            IsCorner(points[48..], halfWidth, halfDepth) && IsCorner(points[64..], -halfWidth, halfDepth)
            ? (halfWidth, halfDepth) : null;
    }

    private static ReadOnlySpan<byte> ReadSectionHatch(ReadOnlySpan<byte> section, int frame, int hatchOffset,
        ushort hatchKind = 2)
    {
        var vector = ReadSectionVector(section, frame);
        if (vector.IsEmpty || BinaryPrimitives.ReadUInt32LittleEndian(vector[69..]) != 1) return default;

        for (int i = hatchOffset; i <= vector.Length - 59; i++)
        {
            var hatch = vector[i..];
            if (ReadHatchSize(hatch, hatchKind) == hatch.Length)
                return hatch;
            if (frame == 141) break;
        }
        return default;
    }

    private static ReadOnlySpan<byte> ReadSectionVector(ReadOnlySpan<byte> section, int frame)
    {
        if (section.Length < frame + 8 || !section[frame..].StartsWith(SectionVectorFrame)) return default;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(section[(frame + 4)..]);
        if (size < 128 || size > section.Length - frame - 8) return default;
        var vector = section.Slice(frame + 8, (int)size);
        return BinaryPrimitives.ReadUInt32LittleEndian(vector) == 128 ? vector : default;
    }

    private static int ReadHatchSize(ReadOnlySpan<byte> hatch, ushort kind)
    {
        if (hatch.Length < 59 || hatch[0] != 3 || hatch[1] != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(hatch[6..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(hatch[8..]) != kind) return 0;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(hatch[2..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(hatch[55..]);
        return length >= 59 && length <= hatch.Length - 6 && count >= 4 && count <= (length - 59) / 16
            ? 6 + (int)length : 0;
    }

    private static bool IsCorner(ReadOnlySpan<byte> point, double x, double y) =>
        BinaryPrimitives.ReadDoubleLittleEndian(point) == x && BinaryPrimitives.ReadDoubleLittleEndian(point[8..]) == y;
}
