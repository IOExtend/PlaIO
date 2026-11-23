using System.Buffers.Binary;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private static readonly Guid ColumnBeamRelation = new("6346a816-1d7c-4e46-9609-67624e99d0fb");
    private static readonly Guid ColumnSlabRelation = new("87811dc4-1686-45f4-b1a6-dfaa81da44e4");
    private static readonly Guid ColumnWallRelation = new("27c70d06-a928-499c-a005-5307c9f10da0");
    private static readonly Guid BeamColumnJunctionClass = new("f7f60005-35d0-4346-87ab-29e087ac0bbf");
    private static readonly Guid SlabColumnJunctionClass = new("d5381783-6a19-4de8-9e52-2e04a3978db0");
    private static readonly Guid WallColumnJunctionClass = new("a9e0daf5-0932-4054-b976-d8f207bb865a");
    private static readonly Guid BeamColumnRelation = new("cd0b0837-0b34-4e96-8d66-9fe7d926478c");
    private static readonly Guid WallBeamJunctionClass = new("9eb0036e-2b99-4db1-b043-051fb1817f47");
    private static readonly Guid WallBeamRelation = new("b7a142a1-2e5f-4b17-9b52-40604fbc3828");
    private static readonly Guid BeamWallRelation = new("b6d5f795-c750-4a2f-9bb0-e659fb07513a");
    private static readonly Guid SlabColumnRelation = new("f6ee0d9c-27ad-44d0-824f-682969bb171e");
    private static readonly Guid WallColumnRelation = new("199f13d8-5fa0-4894-8adf-761a1442a7f7");
    private static readonly Guid SlabClass = new("945c1275-7948-41d4-8744-9d22d72ca486");
    private static readonly Guid WallClass = new("4f654582-33af-4e52-abfc-c35d46765ec4");
    private static readonly Guid WallProfileRelation = new("e63e7632-0efd-412d-8077-d3f3906b638a");
    private static readonly Guid WallProfileClass = new("462b6598-57f0-41a5-ae81-739c8f487e04");
    private static readonly Guid WallPlanContourClass = new("5f7c9d0d-a84b-4240-a67d-f1458abafd0e");
    private static readonly Guid FirstWallEndRelation = new("0e7a0206-f853-4c86-9f9e-43266b0ab58a");
    private static readonly Guid SecondWallEndRelation = new("df61b698-e5ca-45bf-99fd-0e574a5e54d5");
    private static readonly Guid WallJunctionPointRelation = new("90b557ed-5803-411b-af47-ef97d9d3e31e");
    private static readonly Guid WallJunctionPointClass = new("3367fbd4-7f06-4cef-bd01-09c83d399855");
    private static readonly Guid WallWallJunctionClass = new("68122649-d152-4046-8c2d-8880dfd880bf");
    private static readonly Guid WallSlabJunctionClass = new("324477ff-7a26-4d53-a38e-8f39f678c2b8");
    private static readonly Guid WallSlabRelation = new("88f6f041-4ce9-43cd-b0c1-056bf842bf5a");
    private static readonly Guid SlabWallRelation = new("ae1816f7-dc76-4857-b0e9-fbb3378a3800");
    private static readonly Guid BeamMemoClass = new("cbab7bc6-e03a-4133-9161-c28ed3368672");
    private static readonly Guid BeamClass = new("a5c2d285-5a7c-4a68-9554-9eda9d4556ca");
    private static readonly Guid ColumnClass = new("13e8eb85-5a33-47a3-a4d4-1e05296aec7e");
    private static readonly Guid ComplexSectionClass = new("8825ae40-f827-4a57-a1c5-49d04c687c46");
    private static readonly Guid ProfileShapeClass = new("f4cd2bd7-d4b1-4abf-b13c-59001907bbbd");
    private static readonly Guid ProfileShapeRelation = new("ca073b44-0655-407d-9715-1a019e628139");
    private static readonly Guid BuildingMaterialClass = new("2b0c5e9b-605e-4b8f-aaae-cc166f03bee5");
    private static readonly Guid CompositeClass = new("c7ecb629-3cf3-4c9c-a222-11082672e04b");

    private static bool IsJunctionGeometryClass(Guid id) =>
        id == ColumnMemoClass || id == RectangularSectionClass || id == CircularColumnSectionClass || id == BeamColumnJunctionClass ||
        id == SlabColumnJunctionClass || id == WallColumnJunctionClass || id == BeamClass || id == BeamMemoClass || id == ColumnClass ||
        id == ComplexSectionClass || id == ProfileShapeClass || id == SlabClass || id == WallClass || id == WallProfileClass ||
        id == WallWallJunctionClass || id == WallSlabJunctionClass || id == WallBeamJunctionClass || id == WallPlanContourClass ||
        id == WallJunctionPointClass || id == BeamBeamJunctionClass || id == BeamSlabJunctionClass ||
        id == SlabLevelDimensionClass || id == OpeningClass || id == OpeningCutClass || id == DoorClass || id == DoorLibraryClass ||
        id == ElementAssemblyClass || id == ElementAssemblyMemberClass || id == AttachedAnnotationClass || id == AngularDimensionClass ||
        id == ZoneClass || id == ZoneBoundaryCalculationClass || id == PropertyOverrideClass;

    private readonly struct ModelGeometry(byte[] pool, ILookup<Guid, ObjectDirectoryRecord>? records,
        Dictionary<ushort, Guid> classes, Lazy<JunctionAttributes> attributes, Lazy<Dictionary<int, Story>> stories,
        Lazy<Dictionary<Guid, int?>> partitions, Lazy<SavedLabels> labelOwners,
        Dictionary<Guid, DoorParameterTable?> doorParameters)
    {
        public JunctionAttributes Attributes => attributes.Value;
        public double? StoryElevation(int? index) => index is { } story ? stories.Value.GetValueOrDefault(story).Elevation : null;
        public double? LinkedElevation(Guid partition) => StoryElevation(partitions.Value.GetValueOrDefault(partition));
        public DoorParameterTable? DoorParameters(DoorParameterLink link) => ResolveDoorParameterTable(doorParameters, link);
        public bool OwnsLabel(Guid owner, Guid label, Guid role) =>
            owner != Guid.Empty && labelOwners.Value.Owners.GetValueOrDefault((label, role)) == owner;
        public bool SlabLabelIsMetadata(Guid owner, Guid label, Guid role) =>
            labelOwners.Value.Owners.TryGetValue((label, role), out Guid saved)
                ? owner != Guid.Empty && saved == owner
                : labelOwners.Value.Indexed.Contains(label);

        public byte[]? Read(Guid id, Guid expectedClass)
        {
            if (records is null || id == Guid.Empty) return null;
            byte[]? result = null;
            foreach (var record in records[id])
            {
                if (classes[record.ClassNumber] != expectedClass) continue;
                var payload = ReadIndexedPayload(pool, record);
                if (payload is not { Length: >= 16 }) continue;
                if (expectedClass == SlabClass || expectedClass == WallClass || expectedClass == BeamClass ||
                    expectedClass == ColumnClass || expectedClass == OpeningClass || expectedClass == DoorClass
                    ? FindClassFrame(payload, expectedClass) < 0 :
                    new Guid(payload.AsSpan(0, 16)) != expectedClass) continue;
                if (result is not null && !result.AsSpan().SequenceEqual(payload)) return null;
                result = payload;
            }
            return result;
        }
    }

    private readonly record struct SlabSkin(double Thickness, uint Priority, bool? IsCore = null);

    private sealed record JunctionAttributes(Dictionary<int, uint?> Material, Dictionary<int, SlabSkin[]?> Composite,
        Dictionary<int, uint?> LayerGroup);

    private static bool SameIntersectionGroup(ModelGeometry geometry, int? first, int? second) =>
        first is { } a && second is { } b &&
        geometry.Attributes.LayerGroup.GetValueOrDefault(a) is > 0 and { } group &&
        geometry.Attributes.LayerGroup.GetValueOrDefault(b) == group;

    private static JunctionAttributes ReadJunctionAttributes(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var classNumbers = classes.Where(c => c.Guid == BuildingMaterialClass || c.Guid == CompositeClass || c.Guid == LayerClass)
            .ToDictionary(c => c.Number, c => c.Guid);
        var materials = new Dictionary<int, uint?>();
        var composites = new Dictionary<int, ReadOnlyMemory<byte>>();
        var layers = new Dictionary<int, uint?>();
        foreach (var block in document.Blocks)
        {
            if (block.Tag != "BL" || block.Version != 1 || !classNumbers.TryGetValue(block.Kind, out Guid kind) ||
                !document.PayloadStartsWithClass(block, kind)) continue;
            var payload = document.ReadPayload(block);
            var data = ReadNamedAttribute(payload, out _, out int index);
            if (data.IsEmpty) continue;

            if (kind == BuildingMaterialClass)
            {
                if (ReadBuildingMaterialPriority(data, out _) is { } priority) AddPriority(materials, index, priority);
            }
            else if (kind == LayerClass)
            {
                if (ReadLayerGroup(data) is { } group) AddPriority(layers, index, group);
            }
            else if (!composites.TryAdd(index, payload.AsMemory(payload.Length - data.Length)) &&
                !composites[index].Span.SequenceEqual(data))
                composites[index] = default;
        }

        return new JunctionAttributes(materials,
            composites.ToDictionary(c => c.Key, c => ReadCompositeSkins(c.Value.Span, materials)), layers);
    }

    private static void AddPriority(Dictionary<int, uint?> values, int index, uint value)
    {
        if (!values.TryAdd(index, value) && values[index] != value) values[index] = null;
    }

    private static SlabSkin[]? ReadCompositeSkins(ReadOnlySpan<byte> data, Dictionary<int, uint?> materials)
    {
        var rows = ReadCompositeSkinRows(data);
        if (rows.IsEmpty) return null;
        var skins = new SlabSkin[rows.Length / CompositeSkinSize];
        for (int i = 0; i < skins.Length; i++)
        {
            var skin = rows[(CompositeSkinSize * i)..];
            if (materials.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(skin[8..])) is not { } priority)
                return null;
            var function = ReadCompositeSkinFunction(skin);
            skins[i] = new SlabSkin(ReadDouble(skin, 15), priority, function.HasValue ? function == 1 : null);
        }
        return skins;
    }

    private static Elevations? ResolveColumnJunctions(ModelGeometry geometry, Guid memo, List<(Guid Key, Guid Id)> junctions,
        ElementLocation location, int material, Elevations bounds, (double X0, double Y0, double X1, double Y1)? footprint,
        double lowerMax, double upperMin, bool profiled, bool checkSolid, out bool keepsNominalBox, out bool solid,
        RectangularColumnPlan? columnPlan = null)
    {
        keepsNominalBox = false;
        solid = false;
        if (geometry.Attributes.Material.GetValueOrDefault(material) is not { } priority || priority == 0)
            return null;
        bool cut = false;
        bool beamContactsMissEnds = true;
        List<(Guid Memo, Guid Junction)>? beams = null;
        List<byte[]>? otherSlabs = null;
        List<byte[]>? cuttingSlabs = null;
        foreach (var (key, id) in junctions)
        {
            var (junctionClass, ownerKey) = key == ColumnBeamRelation ? (BeamColumnJunctionClass, BeamColumnRelation) :
                key == ColumnWallRelation ? (WallColumnJunctionClass, WallColumnRelation) : (SlabColumnJunctionClass, SlabColumnRelation);
            var contact = geometry.Read(id, junctionClass);
            if (contact is null || ReadSingleRelation(contact, key, requireSection: true) != memo) return null;
            Guid neighbor = ReadSingleRelation(contact, ownerKey, requireSection: true);
            if (key == ColumnBeamRelation)
            {
                if (ReadBeamPriority(geometry, neighbor, id) is not { } beam) return null;
                if (beam > priority) (beams ??= []).Add((neighbor, id));
                continue;
            }
            if (key == ColumnWallRelation)
            {
                if (geometry.Read(neighbor, WallClass) is not { } wallRecord ||
                    !HasGeometryRelation(wallRecord, WallColumnRelation, id) ||
                    ReadWallPriority(geometry, neighbor, wallRecord) is not { } wall || wall > priority) return null;
                checkSolid &= profiled || !WallMayWrapColumn(geometry.Attributes, ReadWallSettings(wallRecord));
                continue;
            }

            var slab = geometry.Read(neighbor, SlabClass);
            if (slab is null || !HasGeometryRelation(slab, SlabColumnRelation, id)) return null;
            var record = slab.AsSpan(FindClassFrame(slab, SlabClass));
            var settings = AfterCountedString(record, ClassElementIdOffset("Slab"));
            if (ReadSlabPriority(settings, geometry.Attributes) is not { } slabPriority) return null;
            // At equal material priority an automatic column junction takes precedence over a beam or slab.
            if (slabPriority <= priority) continue;
            if (checkSolid) (cuttingSlabs ??= []).Add(slab);
            if (SlabKeepsPlanes(geometry, record, settings, location, priority, lowerMax, upperMin, bounds, checkSkins: true))
            {
                (otherSlabs ??= []).Add(slab);
                continue;
            }
            if (cut || footprint is not { } xy ||
                !HasSupportedSlabContacts(record, SlabColumnRelation, id) ||
                ClipBySlab(geometry, location, record, settings, xy, bounds, lowerMax, upperMin, out _) is not { } clipped)
                return null;
            cut = true;
            bounds = clipped;
        }
        if (beams is not null)
        {
            if (geometry.StoryElevation(location.HomeStoryIndex) is not { } home)
                return null;
            if (!cut && footprint is { } plan &&
                geometry.Attributes.LayerGroup.GetValueOrDefault(location.LayerIndex.GetValueOrDefault()) is > 0 &&
                ClipByBeamUnion(geometry, beams, BeamColumnRelation, location, home, plan, bounds, lowerMax, upperMin) is { } union)
            {
                cut = union != bounds;
                beamContactsMissEnds = beams.All(b => BeamCannotCutColumnEnds(geometry, b.Memo, home, bounds, lowerMax, upperMin));
                bounds = union;
            }
            else
            {
                foreach (var (beam, junction) in beams)
                {
                    if (BeamCannotCutColumnEnds(geometry, beam, home, bounds, lowerMax, upperMin)) continue;
                    if (cut || footprint is not { } xy ||
                        geometry.Attributes.LayerGroup.GetValueOrDefault(location.LayerIndex.GetValueOrDefault()) is not > 0 ||
                        ClipByBeam(geometry, beam, junction, BeamColumnRelation, location, home, xy, bounds, lowerMax, upperMin) is not { } clipped)
                        return null;
                    if (clipped == bounds) continue;
                    double low = clipped.Bottom == bounds.Bottom ? lowerMax : clipped.Bottom;
                    double high = clipped.Top == bounds.Top ? upperMin : clipped.Top;
                    if (beams.Any(b => b.Junction != junction && !BeamCannotCutColumnEnds(geometry, b.Memo, home, clipped, low, high)))
                        return null;
                    cut = true;
                    bounds = clipped;
                }
            }
        }
        if (cut && otherSlabs is not null)
        {
            foreach (var slab in otherSlabs)
            {
                var record = slab.AsSpan(FindClassFrame(slab, SlabClass));
                var settings = AfterCountedString(record, ClassElementIdOffset("Slab"));
                if (!SlabKeepsPlanes(geometry, record, settings, location, priority, Math.Max(lowerMax, bounds.Bottom),
                    Math.Min(upperMin, bounds.Top), bounds, checkSkins: true)) return null;
            }
        }
        // shortcut: combined cuts involving skew faces or overlapping slab cuts need their evaluated solids.
        keepsNominalBox = !cut && beamContactsMissEnds && junctions.All(c => c.Key != ColumnWallRelation);
        solid = checkSolid && (!cut || footprint.HasValue) &&
            ColumnCuttersClearBody(geometry, location, bounds, priority, beams, cuttingSlabs, columnPlan);
        return bounds;
    }

    private static bool ColumnCuttersClearBody(ModelGeometry geometry, ElementLocation location, Elevations body,
        uint priority, List<(Guid Memo, Guid Junction)>? beams, List<byte[]>? slabs, RectangularColumnPlan? columnPlan)
    {
        // End cuts prove removal outside the surviving interval. A mesh also needs the whole interval intact.
        if (beams is not null && (geometry.StoryElevation(location.HomeStoryIndex) is not { } home ||
            beams.Any(beam => !BeamCannotCutColumnEnds(geometry, beam.Memo, home, body, body.Top, body.Bottom) &&
                (columnPlan is not { } plan || !JoinedBeamClearsColumn(geometry, beam.Memo, beam.Junction, location, home, plan)))))
            return false;
        if (slabs is null) return true;
        foreach (var slab in slabs)
        {
            var record = slab.AsSpan(FindClassFrame(slab, SlabClass));
            var settings = AfterCountedString(record, ClassElementIdOffset("Slab"));
            if (!SlabKeepsPlanes(geometry, record, settings, location, priority, body.Top, body.Bottom, body, checkSkins: true))
                return false;
        }
        return true;
    }

    private static ReadOnlySpan<byte> ReadWallSettings(byte[] wall) =>
        AfterCountedString(wall.AsSpan(FindClassFrame(wall, WallClass)), 197);

    private static bool WallMayWrapColumn(JunctionAttributes attributes, ReadOnlySpan<byte> settings)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) != 2) return false;
        var skins = attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[229..]));
        // Wrapping requires both a core and a non-core skin.
        return skins is null || (!skins.All(skin => skin.IsCore == true) && !skins.All(skin => skin.IsCore == false));
    }

    private static uint? ReadWallPriority(ModelGeometry geometry, Guid wallId, byte[] wall)
    {
        var settings = ReadWallSettings(wall);
        if (settings.Length < 231 || BinaryPrimitives.ReadUInt16LittleEndian(settings[227..]) != 1) return null;
        switch (BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]))
        {
            case 1:
                return geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(settings[215..]));
            case 2:
                return geometry.Attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[229..]))
                    ?.Max(skin => skin.Priority);
            case 3:
                return ReadWallProfilePriority(ReadWallProfileVector(geometry, wallId, wall), geometry.Attributes.Material, lowest: false);
            default:
                return null;
        }
    }

    private static ReadOnlySpan<byte> ReadWallProfileVector(ModelGeometry geometry, Guid wallId, ReadOnlySpan<byte> wall)
        => ReadWallProfileRecord(geometry, wallId, wall) is { } profile ? ReadSectionVector(profile, 394) : default;

    private static byte[]? ReadWallProfileRecord(ModelGeometry geometry, Guid wallId, ReadOnlySpan<byte> wall)
    {
        Guid profileId = ReadSingleRelation(wall, WallProfileRelation, 0x11, requireSection: true);
        return geometry.Read(profileId, WallProfileClass) is { } profile &&
            ReadSingleRelation(profile, WallProfileRelation, requireSection: true) == wallId
            ? profile : null;
    }

    private static uint? ReadWallProfilePriority(ReadOnlySpan<byte> vector, Dictionary<int, uint?> materials, bool lowest)
    {
        var items = ReadFirstWallHatch(vector, out uint count);
        if (items.IsEmpty) return null;
        uint limit = lowest ? uint.MaxValue : 0;
        for (int i = 0; i < count; i++)
        {
            int size = ReadHatchSize(items, 2);
            if (size == 0) return null;
            if (materials.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(items[18..])) is not { } priority)
                return null;
            limit = lowest ? Math.Min(limit, priority) : Math.Max(limit, priority);
            items = items[size..];
            if (i + 1 < count)
            {
                if (items.Length < 16) return null;
                items = items[16..];
            }
        }
        return items.IsEmpty ? limit : null;
    }

    private static ReadOnlySpan<byte> ReadFirstWallHatch(ReadOnlySpan<byte> vector, out uint count)
    {
        count = 0;
        if (vector.IsEmpty) return default;
        count = BinaryPrimitives.ReadUInt32LittleEndian(vector[69..]);
        if (count == 0 || count > vector.Length / 65) return default;
        for (int start = 128; start <= vector.Length - 59; start++)
            if (ReadHatchSize(vector[start..], 2) != 0) return vector[start..];
        return default;
    }

    private static uint? ReadBeamPriority(ModelGeometry geometry, Guid beamId, Guid junction)
    {
        var beam = geometry.Read(beamId, BeamMemoClass);
        if (beam is null || !HasGeometryRelation(beam, BeamColumnRelation, junction)) return null;
        var hatch = ReadBeamHatch(geometry, beamId, beam);
        return hatch.IsEmpty ? null : geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..]));
    }

    private static ReadOnlySpan<byte> ReadBeamHatch(ModelGeometry geometry, Guid beamId, ReadOnlySpan<byte> beam)
    {
        Guid sectionId = ReadSingleRelation(beam, SectionProfileRelation, 0x11, requireSection: true);
        if (geometry.Read(sectionId, RectangularSectionClass) is { } basic &&
            ReadSingleRelation(basic, SectionProfileRelation, requireSection: true) == beamId)
        {
            var hatch = ReadSectionHatch(basic, 141, 482);
            return hatch.IsEmpty ? ReadSectionHatch(basic, 141, 482, hatchKind: 14) : hatch;
        }
        return ReadComplexSectionHatch(geometry, beamId, sectionId);
    }

    private static ReadOnlySpan<byte> ReadComplexSectionHatch(ModelGeometry geometry, Guid owner, Guid sectionId)
        => ReadComplexSectionProfile(geometry, owner, sectionId) is { } profile
            ? ReadSectionHatch(profile, 392, 128) : default;

    private static byte[]? ReadComplexSectionProfile(ModelGeometry geometry, Guid owner, Guid sectionId)
    {
        if (geometry.Read(sectionId, ComplexSectionClass) is { } complex &&
            ReadSingleRelation(complex, SectionProfileRelation, requireSection: true) == owner)
        {
            Guid shapeId = ReadSingleRelation(complex, ProfileShapeRelation, 0x11, requireSection: true);
            if (geometry.Read(shapeId, ProfileShapeClass) is { } profile &&
                ReadSingleRelation(profile, ProfileShapeRelation, requireSection: true) == sectionId)
                return profile;
        }
        return null;
    }

    private static bool HasGeometryRelation(ReadOnlySpan<byte> record, Guid relation, Guid id)
    {
        int start = FindRelationshipSection(record, record.Length);
        if (start < 0) return false;
        Span<byte> target = stackalloc byte[16];
        id.TryWriteBytes(target);
        var data = record[start..];
        bool found = false;
        while (data.Length >= 17)
        {
            var entry = data;
            if (!TryReadGeometryRelation(ref data, out byte tag, out Guid key, out Guid single)) return false;
            if (key != relation) continue;
            if (found) return false;
            if (tag == 0x11)
            {
                found = single == id;
                continue;
            }
            if (tag != 1) return false;
            var ids = entry.Slice(21, entry.Length - data.Length - 21);
            for (int i = 0; i < ids.Length; i += 16)
                if (ids.Slice(i, 16).SequenceEqual(target)) found = true;
        }
        return found && data is [0];
    }

    private static uint? ReadSlabPriority(ReadOnlySpan<byte> settings, JunctionAttributes attributes)
    {
        if (settings.Length < 89 || BinaryPrimitives.ReadUInt16LittleEndian(settings[85..]) != 1) return null;
        return BinaryPrimitives.ReadUInt16LittleEndian(settings[83..]) switch
        {
            1 => attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(settings[73..])),
            2 => attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[87..]))?.Max(skin => skin.Priority),
            _ => null,
        };
    }

    private static bool SlabKeepsPlanes(ModelGeometry geometry, ReadOnlySpan<byte> slab, ReadOnlySpan<byte> settings,
        ElementLocation target, uint priority, double lowerMax, double upperMin, Elevations bounds, bool checkSkins)
    {
        if (geometry.StoryElevation(target.HomeStoryIndex) is not { } home ||
            geometry.StoryElevation(ReadElementLocation(slab, "Slab", 0).HomeStoryIndex) is not { } slabStory ||
            ReadElevations(slab, "Slab") is not { } slabBounds || settings.Length < 89)
            return false;
        double bottom = slabBounds.Bottom + slabStory - home, top = slabBounds.Top + slabStory - home;
        if (MissesEndFaces(bottom, top, bounds, lowerMax, upperMin)) return true;
        // A wall may be cut below all the slab skins once an adjoining skin has sufficient priority.
        if (!checkSkins || settings[8] != 0 || BinaryPrimitives.ReadUInt16LittleEndian(settings[83..]) != 2 ||
            geometry.Attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[87..])) is not { } skins)
            return false;
        foreach (var skin in skins)
        {
            if (!(skin.Thickness > 0 && double.IsFinite(skin.Thickness))) return false;
            double skinBottom = top - skin.Thickness;
            if (skin.Priority > priority && !MissesEndFaces(skinBottom, top, bounds, lowerMax, upperMin))
                return false;
            top = skinBottom;
        }
        return Math.Abs(top - bottom) <= JunctionTolerance;
    }

    private static bool MissesEndFaces(double low, double high, Elevations bounds, double lowerMax, double upperMin) =>
        high <= bounds.Bottom + JunctionTolerance || low >= bounds.Top - JunctionTolerance ||
        low > lowerMax + JunctionTolerance && high < upperMin - JunctionTolerance;

    private static bool HasSupportedSlabContacts(ReadOnlySpan<byte> slab, Guid reciprocal, Guid junction,
        Func<Guid, Guid, bool>? acceptsOther = null, Func<Guid, Guid, bool>? acceptsMember = null,
        Func<Guid, Guid, bool>? acceptsOperatorTarget = null, Func<Guid, Guid, bool>? acceptsTargetOperand = null)
    {
        int start = FindRelationshipSection(slab, slab.Length);
        if (start < 0) return false;
        var data = slab[start..];
        bool hasTarget = false, hasMembership = false, hasTargetOperands = false;
        while (data.Length >= 17)
        {
            var entry = data;
            if (!TryReadGeometryRelation(ref data, out byte tag, out Guid key, out _)) return false;
            if ((tag is 1 or 0x11) && key == DrawingRelation ||
                tag == 0x11 && (key == DefaultElementRelation || key == ElementComponentRelation ||
                    key == ElementSettingsRelation || key == LinkedElementSettingsRelation) ||
                tag == 2 && key == AttributeOwnerRelation ||
                reciprocal == Guid.Empty && tag == 1 && key == ElementPropertyOverrideLink ||
                tag == 0x12 && (key == ElementPartitionRelation || key == ElementCollectionRelation ||
                    key == DatabaseElementList || key == AttributeOwnerRelation))
                continue;
            if (tag == 2 && key == ElementAssemblyRelation && acceptsMember is not null && !hasMembership &&
                ReadRelationIds(entry, data, tag) is { Length: 32 } membership &&
                acceptsMember(new Guid(membership[..16]), new Guid(membership[16..])))
            {
                hasMembership = true;
                continue;
            }
            if (tag == 2 && key == ElementAssemblyRelation && acceptsOperatorTarget is not null && !hasMembership)
            {
                var pairs = ReadRelationIds(entry, data, tag);
                if (pairs.IsEmpty || pairs.Length % 32 != 0) return false;
                for (int i = 0; i < pairs.Length; i += 32)
                    if (!acceptsOperatorTarget(new Guid(pairs.Slice(i, 16)), new Guid(pairs.Slice(i + 16, 16)))) return false;
                hasMembership = true;
                continue;
            }
            if (tag == 1 && key == ElementAssemblyRelation && acceptsTargetOperand is not null && !hasTargetOperands)
            {
                var pairs = ReadRelationIds(entry, data, tag);
                if (pairs.IsEmpty || pairs.Length % 32 != 0) return false;
                for (int i = 0; i < pairs.Length; i += 32)
                    if (!acceptsTargetOperand(new Guid(pairs.Slice(i, 16)), new Guid(pairs.Slice(i + 16, 16)))) return false;
                hasTargetOperands = true;
                continue;
            }
            if (tag is not (1 or 0x11)) return false;
            var ids = ReadRelationIds(entry, data, tag);
            if (ids.IsEmpty) return false;
            for (int i = 0; i < ids.Length; i += 16)
            {
                var id = new Guid(ids.Slice(i, 16));
                if (id == Guid.Empty) return false;
                if (key == reciprocal && id == junction && !hasTarget)
                    hasTarget = true;
                else if (id == junction || acceptsOther is null || !acceptsOther(key, id))
                    return false;
            }
        }
        return (hasTarget || reciprocal == Guid.Empty) && data is [0];
    }

    private static Elevations? ClipBySlab(ModelGeometry geometry, ElementLocation element,
        ReadOnlySpan<byte> slab, ReadOnlySpan<byte> settings, (double X0, double Y0, double X1, double Y1) footprint,
        Elevations bounds, double lowerMax, double upperMin, out Guid[]? beamJunctions, uint? wallPriority = null,
        (Guid Wall, Guid Slab, Guid Junction)? cutter = null, bool requireContiguousEnd = false,
        bool allowSharedBeamCoverage = false)
    {
        beamJunctions = null;
        var location = ReadElementLocation(slab, "Slab", 0);
        if (!SameIntersectionGroup(geometry, element.LayerIndex, location.LayerIndex) ||
            geometry.StoryElevation(element.HomeStoryIndex) is not { } home ||
            geometry.StoryElevation(location.HomeStoryIndex) is not { } slabStory ||
            settings.Length < 431 || ReadElevations(slab, "Slab") is not { } slabBounds)
            return null;
        int structure = BinaryPrimitives.ReadUInt16LittleEndian(settings[83..]);
        if (structure != 1 && (structure != 2 || wallPriority is null || settings[8] is not (0 or 3))) return null;

        var polygon = settings[270..];
        if (requireContiguousEnd && (ReadOutlineBounds(ReadSlabOutline(settings)) is null ||
            !HasVerticalSlabEdges(polygon[(int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[4..])..],
                (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[43..]) -
                (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[51..])))) return null;
        ReadOnlySpan<byte> rectangle = [2, 0, 2, 1, 247, 0, 0, 0, 1, 31, 0];
        var slabPlan = polygon.StartsWith(rectangle) ? ReadRectangularPlan(polygon) : null;
        if (slabPlan is null)
        {
            if (ReadSlabPlanEnvelope(settings) is not { } envelope ||
                !SlabPolygonContainsFootprint(polygon, footprint, allowBoundary: structure == 2))
                return null;
            slabPlan = envelope;
        }
        var slabFootprint = slabPlan.Value;
        var (x0, y0, x1, y1) = slabFootprint;

        double low = slabBounds.Bottom + slabStory - home, high = slabBounds.Top + slabStory - home;
        if (!(high > low)) return null;
        if (high <= bounds.Bottom || low >= bounds.Top ||
            footprint.X0 >= x1 || footprint.X1 <= x0 || footprint.Y0 >= y1 || footprint.Y1 <= y0)
            return bounds;
        bool covers = x0 < footprint.X0 && y0 < footprint.Y0 && x1 > footprint.X1 && y1 > footprint.Y1;
        bool leavesCorner = x0 > footprint.X0 || y0 > footprint.Y0 || x1 < footprint.X1 || y1 < footprint.Y1;
        if (structure == 2 && wallPriority is { } priority)
        {
            if (geometry.Attributes.Composite.GetValueOrDefault(BinaryPrimitives.ReadUInt16LittleEndian(settings[87..])) is not { } skins)
                return null;
            double skinTop = high, cutBottom = double.PositiveInfinity, cutTop = double.NegativeInfinity;
            foreach (var skin in skins)
            {
                if (!(skin.Thickness > 0 && double.IsFinite(skin.Thickness))) return null;
                double skinBottom = skinTop - skin.Thickness;
                if (skin.Priority >= priority && skinTop > bounds.Bottom + JunctionTolerance &&
                    skinBottom < bounds.Top - JunctionTolerance)
                {
                    // shortcut: disjoint cutting bands may leave more than one wall piece.
                    if (double.IsFinite(cutBottom) && cutBottom > skinTop + JunctionTolerance)
                        return null;
                    cutBottom = skinBottom;
                    cutTop = Math.Max(cutTop, skinTop);
                }
                skinTop = skinBottom;
            }
            if (Math.Abs(skinTop - low) > JunctionTolerance) return null;
            if (cutBottom > cutTop) return bounds;
            if (footprint.X0 < x0 - JunctionTolerance || footprint.Y0 < y0 - JunctionTolerance ||
                footprint.X1 > x1 + JunctionTolerance || footprint.Y1 > y1 + JunctionTolerance)
                return leavesCorner ? bounds : null;
            bool cutsBottom = low <= bounds.Bottom + JunctionTolerance && high < bounds.Top - JunctionTolerance;
            bool cutsTop = high >= bounds.Top - JunctionTolerance && low > bounds.Bottom + JunctionTolerance;
            if (!cutsBottom && !cutsTop)
                return low > bounds.Bottom + JunctionTolerance && high < bounds.Top - JunctionTolerance ? bounds : null;
            if (cutter is not { } joined) return null;
            if (requireContiguousEnd && (cutsBottom ? cutBottom > bounds.Bottom + JunctionTolerance : cutTop < bounds.Top - JunctionTolerance))
                return null;
            var band = new Elevations(cutBottom, cutTop);
            List<(Guid Memo, Guid Junction)>? beams = null;
            if (!HasSupportedSlabContacts(slab, SlabWallRelation, joined.Junction,
                (key, id) => key == SlabWallRelation && IsSameWallSlabPair(geometry, joined.Wall, joined.Slab, id) ||
                    SlabContactKeepsCuttingBand(geometry, joined.Slab, key, id, priority, home, footprint, band) ||
                    key == SlabBeamRelation &&
                    (allowSharedBeamCoverage && SharedBeamPreservesSlabCoverage(geometry, joined.Wall, joined.Slab, id,
                        element, home, priority, footprint) ||
                     !requireContiguousEnd && CollectBeamForSlabCut(geometry, joined.Wall, joined.Slab, id,
                        element, home, band, bounds, cutsBottom, priority, beams ??= [])),
                acceptsOperatorTarget: (target, operation) => IsSlabOperatorTarget(geometry, target, joined.Slab, operation)))
                return null;
            var cut = cutsBottom ? CheckedElevations(cutTop, bounds.Top) : CheckedElevations(bounds.Bottom, cutBottom);
            if (beams is null || cut is not { } remainder) return cut;
            if (ClipByBeamUnion(geometry, beams, BeamWallRelation, element, home, footprint, remainder,
                remainder.Bottom, remainder.Top, joinedSlabWall: true) is not { } combined ||
                (cutsBottom ? combined.Bottom <= remainder.Bottom || combined.Top != remainder.Top :
                    combined.Top >= remainder.Top || combined.Bottom != remainder.Bottom))
                return null;
            beamJunctions = beams.Select(b => b.Junction).ToArray();
            return combined;
        }
        if (high < upperMin && low <= bounds.Bottom)
        {
            if (leavesCorner && lowerMax == bounds.Bottom) return bounds;
            if (covers && high >= lowerMax) return CheckedElevations(high, bounds.Top);
        }
        if (low > lowerMax && high >= bounds.Top)
        {
            if (leavesCorner && upperMin == bounds.Top) return bounds;
            if (covers && low <= upperMin) return CheckedElevations(bounds.Bottom, low);
        }
        return low > lowerMax && high < upperMin ? bounds : null;
    }

    private static bool IsSameWallSlabPair(ModelGeometry geometry, Guid wall, Guid slab, Guid id) =>
        geometry.Read(id, WallSlabJunctionClass) is { } contact &&
        ReadSingleRelation(contact, WallSlabRelation, requireSection: true) == wall &&
        ReadSingleRelation(contact, SlabWallRelation, requireSection: true) == slab &&
        geometry.Read(wall, WallClass) is { } body && HasGeometryRelation(body, WallSlabRelation, id);

    private static byte[]? ReadWallSlabContact(ModelGeometry geometry, Guid wall, Guid junction, out Guid slab)
    {
        slab = Guid.Empty;
        if (geometry.Read(junction, WallSlabJunctionClass) is not { } contact ||
            ReadSingleRelation(contact, WallSlabRelation, requireSection: true) != wall)
            return null;
        slab = ReadSingleRelation(contact, SlabWallRelation, requireSection: true);
        return geometry.Read(slab, SlabClass) is { } body && HasGeometryRelation(body, SlabWallRelation, junction) ? body : null;
    }

    private static bool WallKeepsItsPlanes(ModelGeometry geometry, Guid wallId, Guid junctionId, Guid key, uint priority,
        ElementLocation location, Elevations bounds, WallStrip? wall, bool basic, uint? slabCutPriority,
        List<WallCapObstacle> blocked, out Elevations newBounds, out WallCut cut, bool trackPartialBeamCuts = false)
    {
        newBounds = bounds;
        cut = new WallCut(junctionId);
        if (key == WallColumnRelation)
            return ColumnLeavesWallSection(geometry, wallId, junctionId, priority, wall, blocked);
        if (key == WallBeamRelation)
        {
            var contact = geometry.Read(junctionId, WallBeamJunctionClass);
            if (contact is null || ReadSingleRelation(contact, WallBeamRelation, requireSection: true) != wallId) return false;
            Guid beamId = ReadSingleRelation(contact, BeamWallRelation, requireSection: true);
            if (geometry.Read(beamId, BeamMemoClass) is not { } beam || !HasGeometryRelation(beam, BeamWallRelation, junctionId)) return false;
            var hatch = ReadBeamHatch(geometry, beamId, beam);
            if (hatch.IsEmpty ||
                geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..])) is not { } beamPriority)
                return false;
            if (beamPriority < priority) return true;
            if (!trackPartialBeamCuts && wall is { } target &&
                geometry.StoryElevation(location.HomeStoryIndex) is { } home &&
                geometry.Attributes.LayerGroup.GetValueOrDefault(location.LayerIndex.GetValueOrDefault()) is > 0 &&
                basic && (ClipByBeamUnion(geometry, [(beamId, junctionId)], BeamWallRelation, location, home, target.Footprint,
                    bounds, bounds.Bottom, bounds.Top) ??
                 ClipByBeam(geometry, beamId, junctionId, BeamWallRelation, location, home, target.Footprint,
                    bounds, bounds.Bottom, bounds.Top)) is { } clipped)
            {
                newBounds = clipped;
                return true;
            }
            return BeamLeavesWallSection(geometry, beamId, beam, hatch, location, bounds, wall, blocked);
        }
        if (key == WallSlabRelation)
        {
            if (ReadWallSlabContact(geometry, wallId, junctionId, out Guid slabId) is not { } slab) return false;
            var record = slab.AsSpan(FindClassFrame(slab, SlabClass));
            var settings = AfterCountedString(record, ClassElementIdOffset("Slab"));
            if (ReadSlabPriority(settings, geometry.Attributes) is not { } slabPriority) return false;
            if (slabPriority < priority ||
                SlabKeepsPlanes(geometry, record, settings, location, priority, bounds.Bottom, bounds.Top, bounds, checkSkins: false))
                return true;
            if (wall is not { } strip) return false;
            bool composite = BinaryPrimitives.ReadUInt16LittleEndian(settings[83..]) == 2;
            if (composite && (basic ? priority : slabCutPriority) is { } threshold && ReadJoinedWallStrip(geometry, wallId, strip, location) is { } joined &&
                ClipBySlab(geometry, location, record, settings, joined.Footprint, bounds, bounds.Bottom, bounds.Top,
                    out Guid[]? beamJunctions, threshold, (wallId, slabId, junctionId),
                    requireContiguousEnd: !basic || settings[8] == 3) is { } compositeCut &&
                (joined == strip || compositeCut != bounds))
            {
                newBounds = compositeCut;
                cut = new WallCut(junctionId, CoversWallJoins: true, beamJunctions);
                return true;
            }
            if (!composite && basic && HasSupportedSlabContacts(record, SlabWallRelation, junctionId) &&
                ClipBySlab(geometry, location, record, settings, strip.Footprint, bounds, bounds.Bottom, bounds.Top, out _) is { } clipped)
            {
                newBounds = clipped;
                return true;
            }
            if (ReadSlabPlanEnvelope(settings) is not { } plan) return false;
            if (ReadWallSlabPlan(geometry, record, settings, out var world) is { } rings)
            {
                var height = geometry.StoryElevation(location.HomeStoryIndex) is { } home
                    ? CheckedElevations(world.Bottom - home, world.Top - home) : null;
                AddSlabBlockedInterval(strip, rings, height, blocked);
            }
            else
                AddWallBlockedInterval(strip, plan, blocked, hasVerifiedExtent: false);
            return true;
        }

        if (geometry.Read(junctionId, WallWallJunctionClass) is not { } joint ||
            ReadWallJunctionPeers(joint, wallId, key) is not { } peers) return false;
        foreach (var (relation, owner) in peers)
        {
            if (geometry.Read(owner, WallClass) is not { } neighbor ||
                !HasGeometryRelation(neighbor, relation, junctionId) ||
                ReadWallPriority(geometry, owner, neighbor) is not { } neighborPriority)
                return false;
            if (neighborPriority < priority) continue;
            var height = ReadNeighborWallHeight(geometry, neighbor, location);
            if (height is { } range && MissesEndFaces(range.Bottom, range.Top, bounds, bounds.Bottom, bounds.Top)) continue;
            var record = neighbor.AsSpan(FindClassFrame(neighbor, WallClass));
            var settings = AfterCountedString(record, 197);
            var contour = ReadWallContour(geometry, record, owner);
            (double X0, double Y0, double X1, double Y1)? inline = null;
            if (contour is null && HasEmptyWallPlanCache(settings)) ReadWallReferencePlan(settings, out inline);
            if (wall is not { } ownStrip) return false;
            if (ReadWallStrip(settings, contour, inline) is not { } otherStrip)
            {
                if (ReadWallReferencePlan(settings, out _) is not { } plan ||
                    ReadWallJunctionStation(geometry, joint, ownStrip) is not { } station ||
                    CrossingWallInterval(ownStrip, plan, station) is not { } interval)
                    return false;
                blocked.Add(new(interval, height));
                continue;
            }
            if (ownStrip.AlongX != otherStrip.AlongX)
            {
                blocked.Add(new((otherStrip.AcrossMin, otherStrip.AcrossMax), height));
            }
            else if (ownStrip.AcrossMin < otherStrip.AcrossMax && ownStrip.AcrossMax > otherStrip.AcrossMin &&
                !AreCollinearWallEnds(ownStrip, otherStrip))
            {
                if (ReadWallJunctionStation(geometry, joint, ownStrip, WallCapPlanClearance) is { } station &&
                    WallButtAtStation(ownStrip, otherStrip, station)) continue;
                if (!ownStrip.HasContour || !otherStrip.HasContour || ownStrip.Line != otherStrip.Line ||
                    !HasOnlyCollinearWallContacts(geometry, record, wallId, owner, ownStrip, otherStrip))
                {
                    // The verified vertical range can exclude a cut at one end even when the plan is unresolved.
                    if (height is null || ReadSavedWallBox(settings) is null || settings[184] != 0) return false;
                    blocked.Add(new((ownStrip.Start, ownStrip.End), height));
                }
                else
                {
                    blocked.Add(new((otherStrip.Start, otherStrip.End), height));
                }
            }
        }
        return true;
    }

    private static bool ColumnLeavesWallSection(ModelGeometry geometry, Guid wall, Guid junction, uint priority, WallStrip? strip,
        List<WallCapObstacle> blocked)
    {
        if (geometry.Read(junction, WallColumnJunctionClass) is not { } contact ||
            ReadSingleRelation(contact, WallColumnRelation, requireSection: true) != wall)
            return false;
        Guid memoId = ReadSingleRelation(contact, ColumnWallRelation, requireSection: true);
        var hatch = ReadColumnContactSection(geometry, memoId, ColumnWallRelation, junction, out var column);
        if (hatch.IsEmpty ||
            geometry.Attributes.Material.GetValueOrDefault(BinaryPrimitives.ReadInt32LittleEndian(hatch[18..])) is not { } columnPriority)
            return false;
        if (columnPriority < priority) return true;
        if (strip is not { } s || ReadColumnFootprint(column, hatch) is not { } plan) return false;
        AddWallBlockedInterval(s, plan, blocked);
        return true;
    }

    private static ReadOnlySpan<byte> ReadColumnContactSection(ModelGeometry geometry, Guid memoId, Guid relation,
        Guid junction, out ReadOnlySpan<byte> column, bool includeCircular = false)
    {
        column = default;
        if (geometry.Read(memoId, ColumnMemoClass) is not { } memo ||
            !HasGeometryRelation(memo, relation, junction)) return default;
        Guid owner = ReadSingleRelation(memo, ColumnMemoRelation, requireSection: true);
        if (ReadColumnSection(memo, owner, out _) is not { } sectionId ||
            geometry.Read(owner, ColumnClass) is not { } body) return default;
        column = body.AsSpan(FindClassFrame(body, ColumnClass));
        if (column.Length < 735 || ReadColumnPrismMemo(column, owner, geometry) != memoId) return default;
        var section = geometry.Read(sectionId, RectangularSectionClass);
        if (section is null && includeCircular && geometry.Read(sectionId, CircularColumnSectionClass) is { } circular &&
            ReadSingleRelation(circular, SectionProfileRelation, requireSection: true) == memoId &&
            ReadCircularColumnSection(circular, out _) is not null)
            return ReadSectionHatch(circular, 141, 482);
        return section is null ? ReadComplexSectionHatch(geometry, memoId, sectionId) :
            ReadSingleRelation(section, SectionProfileRelation, requireSection: true) == memoId ? ReadSectionHatch(section, 141, 482) : default;
    }

    private static (double X0, double Y0, double X1, double Y1)? ReadColumnFootprint(
        ReadOnlySpan<byte> column, ReadOnlySpan<byte> hatch, double? planAngle = null)
    {
        if (!column.Slice(304, ColumnEndPlaneFrame.Length).SequenceEqual(ColumnEndPlaneFrame) ||
            BinaryPrimitives.ReadUInt16LittleEndian(column[368..]) != 1 ||
            !IsCorner(column[370..], ReadDouble(column, 312), ReadDouble(column, 320)))
            return null;
        var vertices = StraightSectionVertices(hatch);
        var settings = AfterCountedString(column, 735);
        if (vertices.IsEmpty || settings.Length < 36) return null;
        double angle = planAngle ?? ReadDouble(settings, 28), sin = Math.Sin(angle), cos = Math.Cos(angle);
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        for (int i = 0; i < vertices.Length; i += 16)
        {
            double u = ReadDouble(vertices, i), v = ReadDouble(vertices, i + 8);
            double x = ReadDouble(column, 312) + cos * u - sin * v;
            double y = ReadDouble(column, 320) + sin * u + cos * v;
            x0 = Math.Min(x0, x);
            y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x);
            y1 = Math.Max(y1, y);
        }
        return double.IsFinite(x0) && double.IsFinite(y0) && double.IsFinite(x1) && double.IsFinite(y1) ? (x0, y0, x1, y1) : null;
    }

    private static void AddWallBlockedInterval(WallStrip strip, (double X0, double Y0, double X1, double Y1) plan,
        List<WallCapObstacle> blocked, bool hasVerifiedExtent = true)
    {
        double min = strip.AlongX ? plan.Y0 : plan.X0, max = strip.AlongX ? plan.Y1 : plan.X1;
        if (min < strip.AcrossMax && max > strip.AcrossMin)
            blocked.Add(new(strip.AlongX ? (plan.X0, plan.X1) : (plan.Y0, plan.Y1), HasVerifiedExtent: hasVerifiedExtent));
    }

    private static void AddSlabBlockedInterval(WallStrip strip, IReadOnlyList<IReadOnlyList<PlaPlanPoint>> rings,
        Elevations? height, List<WallCapObstacle> blocked)
    {
        const double clearance = 1e-7;
        var outline = rings[0];
        double start = double.PositiveInfinity, end = double.NegativeInfinity;
        // The axial extrema inside the wall's transverse band lie on clipped contour edges.
        var previous = outline[^1];
        foreach (var point in outline)
        {
            double across0 = strip.AlongX ? previous.Y : previous.X, across1 = strip.AlongX ? point.Y : point.X;
            double along0 = strip.AlongX ? previous.X : previous.Y, along1 = strip.AlongX ? point.X : point.Y;
            double low = 0, high = 1;
            if (ClipSegment(across0, across1, strip.AcrossMin - clearance, strip.AcrossMax + clearance, ref low, ref high))
            {
                double a = along0 + (along1 - along0) * low, b = along0 + (along1 - along0) * high;
                start = Math.Min(start, Math.Min(a, b));
                end = Math.Max(end, Math.Max(a, b));
            }
            previous = point;
        }
        // Prove clearance at the same coordinate precision used to validate the model geometry.
        if (start <= end) blocked.Add(new((start - clearance, end + clearance), height, rings));
    }

    private static bool AreCollinearWallEnds(WallStrip a, WallStrip b) =>
        Math.Abs(a.Line - b.Line) <= JunctionTolerance &&
        (Math.Abs(a.Start - b.End) <= JunctionTolerance || Math.Abs(a.End - b.Start) <= JunctionTolerance);

    private static bool HasOnlyCollinearWallContacts(ModelGeometry geometry, ReadOnlySpan<byte> record, Guid wall, Guid neighbor,
        WallStrip own, WallStrip other)
    {
        int start = FindRelationshipSection(record, record.Length);
        if (start < 0) return false;
        var data = record[start..];
        while (data.Length >= 17)
        {
            var entry = data;
            if (!TryReadGeometryRelation(ref data, out byte tag, out Guid key, out Guid target)) return false;
            if ((tag is 1 or 0x11) && key == DrawingRelation ||
                tag == 0x11 && (UnjoinedWallChildRelations.Contains(key) || key == LinkedElementSettingsRelation || key == PlanContourRelation) ||
                tag == 0x12 && (key == DatabaseElementList || UnjoinedWallOwnerRelations.Contains(key) ||
                    key == WallTopStoryRelation || key == AttributeOwnerRelation))
                continue;
            if (tag is not (1 or 0x11) || (key != FirstWallEndRelation && key != SecondWallEndRelation)) return false;
            var ids = tag == 1 ? entry.Slice(21, entry.Length - data.Length - 21) : entry.Slice(17, 16);
            for (int i = 0; i < ids.Length; i += 16)
            {
                var jointId = new Guid(ids.Slice(i, 16));
                if (geometry.Read(jointId, WallWallJunctionClass) is not { } joint ||
                    ReadSingleRelation(joint, key, requireSection: true) != neighbor ||
                    ReadSingleRelation(joint, key == FirstWallEndRelation ? SecondWallEndRelation : FirstWallEndRelation,
                        requireSection: true) != wall)
                    return false;
                Guid pointId = ReadSingleRelation(joint, WallJunctionPointRelation, requireSection: true);
                if (geometry.Read(pointId, WallJunctionPointClass) is not { Length: >= 62 } point) return false;
                double along = ReadDouble(point, own.AlongX ? 46 : 54), across = ReadDouble(point, own.AlongX ? 54 : 46);
                if (across != own.Line || !(along >= Math.Max(own.Start, other.Start) && along <= Math.Min(own.End, other.End)))
                    return false;
            }
        }
        return data is [0];
    }

    private readonly record struct WallStrip(bool AlongX, double Start, double End, double AcrossMin, double AcrossMax,
        double Line, bool HasContour)
    {
        public (double X0, double Y0, double X1, double Y1) Footprint =>
            AlongX ? (Start, AcrossMin, End, AcrossMax) : (AcrossMin, Start, AcrossMax, End);
    }

    private static (double X0, double Y0, double X1, double Y1)? ReadSlabPlanEnvelope(ReadOnlySpan<byte> settings)
    {
        if (settings.Length < 341) return null;
        var polygon = settings[270..];
        if (!polygon.StartsWith(SlabOutlineFrame) || !polygon[8..].StartsWith(new byte[] { 1, 31, 0 }) ||
            BinaryPrimitives.ReadUInt32LittleEndian(polygon[47..]) != 0) return null;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(polygon[4..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(polygon[43..]);
        uint contours = BinaryPrimitives.ReadUInt32LittleEndian(polygon[51..]);
        if (size > polygon.Length || size < 71 || count < 4 || count > (size - 71) / 16 ||
            contours == 0 || contours > (size - 71 - count * 16) / 4) return null;
        double x0 = ReadDouble(polygon, 11), y0 = ReadDouble(polygon, 19);
        double x1 = ReadDouble(polygon, 27), y1 = ReadDouble(polygon, 35);
        if (!(double.IsFinite(x0) && double.IsFinite(y0) && double.IsFinite(x1) && double.IsFinite(y1) && x0 < x1 && y0 < y1))
            return null;
        for (int i = 0; i < count; i++)
        {
            double x = ReadDouble(polygon, 71 + i * 16), y = ReadDouble(polygon, 79 + i * 16);
            if (!(x >= x0 && x <= x1 && y >= y0 && y <= y1)) return null;
        }
        return (x0, y0, x1, y1);
    }

    private static bool SlabPolygonContainsFootprint(ReadOnlySpan<byte> polygon,
        (double X0, double Y0, double X1, double Y1) footprint, bool allowBoundary)
    {
        if (allowBoundary)
        {
            footprint = (footprint.X0 + JunctionTolerance, footprint.Y0 + JunctionTolerance,
                footprint.X1 - JunctionTolerance, footprint.Y1 - JunctionTolerance);
            if (footprint.X0 >= footprint.X1 || footprint.Y0 >= footprint.Y1) return false;
        }
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[43..]);
        int contours = (int)BinaryPrimitives.ReadUInt32LittleEndian(polygon[51..]);
        int offset = 71 + count * 16;
        if (polygon.Length - offset < 4 + contours * 4 ||
            BinaryPrimitives.ReadUInt32LittleEndian(polygon[offset..]) != 0)
            return false;
        double x = footprint.X0 + (footprint.X1 - footprint.X0) / 2;
        double y = footprint.Y0 + (footprint.Y1 - footprint.Y0) / 2;
        bool inside = false;
        int start = 0;
        for (int c = 1; c <= contours; c++)
        {
            uint end = BinaryPrimitives.ReadUInt32LittleEndian(polygon[(offset + c * 4)..]);
            if (end > count || end < start + 4 ||
                !polygon.Slice(71 + start * 16, 16).SequenceEqual(polygon.Slice(71 + ((int)end - 1) * 16, 16)))
                return false;
            for (int i = start + 1; i < end; i++)
            {
                var edge = polygon[(71 + (i - 1) * 16)..];
                double ax = ReadDouble(edge, 0), ay = ReadDouble(edge, 8), bx = ReadDouble(edge, 16), by = ReadDouble(edge, 24);
                double lo = 0, hi = 1;
                if (ClipSegment(ax, bx, footprint.X0, footprint.X1, ref lo, ref hi) &&
                    ClipSegment(ay, by, footprint.Y0, footprint.Y1, ref lo, ref hi))
                    return false;
                if ((ay > y) != (by > y) && x < ax + (y - ay) / (by - ay) * (bx - ax))
                    inside = !inside;
            }
            start = (int)end;
        }
        return start == count && inside;
    }

    private static bool ClipSegment(double a, double b, double min, double max, ref double lo, ref double hi)
    {
        if (a == b) return a >= min && a <= max;
        double t0 = (min - a) / (b - a), t1 = (max - a) / (b - a);
        lo = Math.Max(lo, Math.Min(t0, t1));
        hi = Math.Min(hi, Math.Max(t0, t1));
        return lo <= hi;
    }

    private static WallStrip? ReadWallStrip(ReadOnlySpan<byte> settings, (double X0, double Y0, double X1, double Y1)? footprint,
        (double X0, double Y0, double X1, double Y1)? inline = null)
    {
        if (settings.Length < 229 || settings.Length < 563 && footprint is null && inline is null || settings[184] != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) is not (1 or 2))
            return null;
        double x = ReadDouble(settings, 0), y = ReadDouble(settings, 8);
        double x1 = ReadDouble(settings, 16), y1 = ReadDouble(settings, 24);
        bool alongX = Math.Abs(y - y1) <= JunctionTolerance;
        if (!double.IsFinite(x) || !double.IsFinite(x1) || !double.IsFinite(y) || !double.IsFinite(y1) ||
            (Math.Abs(x - x1) <= JunctionTolerance) == alongX) return null;
        double thickness = ReadDouble(settings, 77);
        if (!double.IsFinite(thickness) || !(thickness > 0) || thickness != ReadDouble(settings, 85)) return null;

        double a = alongX ? x : y, b = alongX ? x1 : y1, line = alongX ? y : x;
        double start = Math.Min(a, b), end = Math.Max(a, b);
        var (x0, y0, xMax, yMax) = footprint ?? inline ??
            (ReadDouble(settings, 515), ReadDouble(settings, 523), ReadDouble(settings, 531), ReadDouble(settings, 539));
        double min = alongX ? y0 : x0, max = alongX ? yMax : xMax;
        if (!(Math.Abs((alongX ? x0 : y0) - start) <= JunctionTolerance &&
              Math.Abs((alongX ? xMax : yMax) - end) <= JunctionTolerance &&
              Math.Abs(max - min - thickness) <= JunctionTolerance &&
              line >= min - JunctionTolerance && line <= max + JunctionTolerance))
            return null;
        return new WallStrip(alongX, start, end, min, max, line, footprint.HasValue);
    }

    private static (double X0, double Y0, double X1, double Y1)? ReadWallContour(ModelGeometry geometry,
        ReadOnlySpan<byte> wall, Guid owner)
    {
        Guid id = ReadSingleRelation(wall, PlanContourRelation, 0x11, requireSection: true);
        if (geometry.Read(id, WallPlanContourClass) is not { Length: >= 206 } contour ||
            ReadSingleRelation(contour, PlanContourRelation, requireSection: true) != owner) return null;
        var polygon = contour.AsSpan(44);
        ReadOnlySpan<byte> rectangle = [0, 0, 2, 1, 247, 0, 0, 0, 0, 31, 0];
        return polygon.StartsWith(rectangle) ? ReadRectangularPlan(polygon) : null;
    }

    private static (double X0, double Y0, double X1, double Y1)? ReadRectangularPlan(ReadOnlySpan<byte> polygon)
    {
        if (polygon.Length < 159 || BinaryPrimitives.ReadUInt32LittleEndian(polygon[43..]) != 5 ||
            BinaryPrimitives.ReadUInt32LittleEndian(polygon[47..]) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(polygon[51..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(polygon[155..]) != 5 || !polygon.Slice(71, 16).SequenceEqual(polygon.Slice(135, 16)))
            return null;
        double x0 = ReadDouble(polygon, 11), y0 = ReadDouble(polygon, 19), x1 = ReadDouble(polygon, 27), y1 = ReadDouble(polygon, 35);
        if (!(double.IsFinite(x0) && double.IsFinite(y0) && double.IsFinite(x1) && double.IsFinite(y1) && x0 < x1 && y0 < y1))
            return null;
        uint corners = 0;
        for (int i = 71; i < 135; i += 16)
        {
            double x = ReadDouble(polygon, i), y = ReadDouble(polygon, i + 8);
            if ((x != x0 && x != x1) || (y != y0 && y != y1) ||
                ReadDouble(polygon, i + 16) != x && ReadDouble(polygon, i + 24) != y) return null;
            corners |= 1u << ((x == x0 ? 0 : 1) + (y == y0 ? 0 : 2));
        }
        return corners == 15 ? (x0, y0, x1, y1) : null;
    }

    private static Elevations? ReadNeighborWallHeight(ModelGeometry geometry, byte[] neighbor, ElementLocation target)
    {
        var record = neighbor.AsSpan(FindClassFrame(neighbor, WallClass));
        var location = ReadElementLocation(record, "Wall", 0);
        var settings = AfterCountedString(record, 197);
        if (settings.Length < 563 || BinaryPrimitives.ReadUInt16LittleEndian(settings[225..]) != 1 ||
            geometry.StoryElevation(location.HomeStoryIndex) is not { } story ||
            geometry.StoryElevation(target.HomeStoryIndex) is not { } home ||
            ReadWallReferencePlanes(settings, location, geometry) is not { } planes ||
            !(Math.Abs(ReadDouble(settings, 547) - (planes.Bottom + story)) <= JunctionTolerance &&
              Math.Abs(ReadDouble(settings, 555) - (planes.Top + story)) <= JunctionTolerance))
            return null;
        return CheckedElevations(planes.Bottom + story - home, planes.Top + story - home);
    }
}
