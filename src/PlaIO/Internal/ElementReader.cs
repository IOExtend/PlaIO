using System.Buffers.Binary;
using System.Text;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static partial class ElementReader
{
    private const ushort ClassTableKind = 0xFDE9;
    private const ushort DirectoryKind = 0xFDEA;
    private const int ExtentUnit = 16;
    private static readonly Guid DirectoryLayout = new("1f9b53da-bb9f-4f97-b5d9-532843923eac");
    private static readonly Guid ModelDatabaseClass = new("f6a0e521-443b-4eda-b20a-7160cbe09115");
    private static readonly Guid DatabaseElementList = new("bd9b270b-0347-4dcf-92cf-d2d872117ba1");
    private static readonly Guid GridStoreClass = new("e9969c7d-ebe2-42ff-a7dc-ed77e7e274c0");
    private static readonly Guid GridElementList = new("d940e8f4-4d66-4faa-86a1-6bf94c703228");
    private static readonly Guid ObjectClass = new("dbefcec3-1bbf-4867-b8b3-c4f7c859df61");
    private static readonly Guid DoorClass = new("d8f07689-9cfa-4fbe-aeb4-0a60b8e667ee");
    private static readonly Guid OpeningClass = new("6746aa79-7498-4bea-bad8-191cdad31d05");
    private static readonly Guid ZoneClass = new("3ad0332e-efe2-491a-92a6-5b14bfdd7229");
    private static readonly Guid ZoneStampRelation = new("5bbc9f04-754d-408b-8e1b-4f20c33cd868");
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);
    private static ReadOnlySpan<byte> ElementIdFrame => [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];
    private static ReadOnlySpan<byte> ClassFrame => [1, 0, 9, 0, 0x83, 0, 3, 0, 0, 1];

    private static readonly Dictionary<Guid, string> Names = new()
    {
        [new("a5c2d285-5a7c-4a68-9554-9eda9d4556ca")] = "Beam",
        [new("13e8eb85-5a33-47a3-a4d4-1e05296aec7e")] = "Column",
        [new("c653c817-a855-4446-aef4-24efb46271aa")] = "CurtainWall",
        [DoorClass] = "Door",
        [new("342a8b55-7348-486a-850b-1eb1bed031fc")] = "Lamp",
        [new("8f2fee46-6a3f-41ac-a32b-69e44dc6276d")] = "Mesh",
        [new("e6363c7b-940e-4deb-af95-8b3b39e86bd2")] = "Morph",
        [ObjectClass] = "Object",
        [OpeningClass] = "Opening",
        [new("4c5ae61a-af17-4b85-93e0-005c987d5f5b")] = "Railing",
        [new("9d4c7621-51e9-4523-a3ba-f2121777bcbe")] = "Roof",
        [new("945c1275-7948-41d4-8744-9d22d72ca486")] = "Slab",
        [new("fa0f7f15-29b4-4905-80fa-c6769109847b")] = "Stair",
        [new("4f654582-33af-4e52-abfc-c35d46765ec4")] = "Wall",
        [ZoneClass] = "Zone",
    };

    internal static bool HasClassTable(PlaDocument document) =>
        document.Blocks.Any(block => block.Tag == "BL" && block.Kind == ClassTableKind);

    internal static IReadOnlyList<PlaObjectClass> ReadClasses(PlaDocument document)
    {
        var tables = document.Blocks.Where(b => IsRawTable(b, ClassTableKind)).Take(2).ToArray();
        if (tables.Length != 1)
            throw new InvalidDataException("The file must contain one supported object class table.");

        ReadOnlySpan<byte> data = document.ReadPayload(tables[0]);
        if (data.Length < 18 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0)
            throw new InvalidDataException("The object class table has an invalid header.");

        int slots = (data.Length - 2) / 16;
        var classes = new List<PlaObjectClass>();
        for (int number = 0; number < slots; number++)
        {
            var guid = new Guid(data.Slice(2 + 16 * number, 16));
            if (guid == Guid.Empty) continue;
            if (number > ushort.MaxValue)
                throw new InvalidDataException("An object class number exceeds the directory field width.");
            classes.Add(new PlaObjectClass((ushort)number, guid, Names.GetValueOrDefault(guid)));
        }
        if (classes.Count == 0 || data[(2 + 16 * slots)..].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("The object class table has no class GUIDs or has an invalid tail.");
        return classes.AsReadOnly();
    }

    internal static IReadOnlyList<PlaElement> Read(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var types = classes.Where(c => c.Name is not null).ToDictionary(c => c.Number);
        var databaseClasses = classes.Where(c => c.Guid == ModelDatabaseClass).Select(c => c.Number).ToHashSet();
        var propertyOverrideClasses = classes.Where(c => c.Guid == PropertyOverrideClass).Select(c => c.Number).ToHashSet();
        var labelClasses = classes.Where(c => c.Guid == ElementLabelClass).Select(c => c.Number).ToHashSet();
        bool useObjectRecords = databaseClasses.Count != 0 || types.Values.Any(c => c.Name == "Zone");
        var elements = new Dictionary<Guid, PlaObjectClass>();
        var directories = new Dictionary<ulong, List<ObjectDirectoryRecord>>();
        var databaseRecords = new Dictionary<ulong, List<ObjectDirectoryRecord>>();
        var sectionRecords = new Dictionary<ulong, List<ObjectDirectoryRecord>>();
        var libraryParameterRecords = new Dictionary<ulong, List<ObjectDirectoryRecord>>();
        var propertyOverrideRecords = new Dictionary<ulong, List<ObjectDirectoryRecord>>();
        var labelRecords = new Dictionary<ulong, List<ObjectDirectoryRecord>>();
        var sectionClasses = classes.Where(c => IsJunctionGeometryClass(c.Guid))
            .ToDictionary(c => c.Number, c => c.Guid);
        var parameterClasses = classes.Where(c => c.Guid == LibraryParameterClass).Select(c => c.Number).ToHashSet();
        foreach (var block in document.Blocks)
        {
            if (!IsRawTable(block, DirectoryKind)) continue;
            ReadOnlySpan<byte> data = document.ReadPayload(block);
            int header = DirectoryHeaderSize(data, DirectoryLayout);
            bool parameterDirectory = header == 0;
            if (parameterDirectory) header = DirectoryHeaderSize(data, LibraryParameterDirectory);
            if (header == 0) continue;
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(data[(header - 40)..]);
            var owner = new Guid(data.Slice(header - 32, 16));
            for (int i = header; i <= data.Length - DirectoryFinder.RecordSize; i += DirectoryFinder.RecordSize)
            {
                var record = DirectoryFinder.ReadRecord(data[i..]);
                if (record.ObjectId == Guid.Empty || record.Count == 0 || record.Position > length || record.Count > length - record.Position)
                    continue;

                if (parameterClasses.Contains(record.ClassNumber))
                {
                    if (!libraryParameterRecords.TryGetValue(length, out var tables)) libraryParameterRecords.Add(length, tables = []);
                    tables.Add(record);
                }
                if (parameterDirectory) continue;

                if (record.ObjectId == owner && databaseClasses.Contains(record.ClassNumber))
                {
                    if (!databaseRecords.TryGetValue(length, out var roots)) databaseRecords.Add(length, roots = []);
                    roots.Add(record);
                }

                if (sectionClasses.ContainsKey(record.ClassNumber))
                {
                    if (!sectionRecords.TryGetValue(length, out var sections)) sectionRecords.Add(length, sections = []);
                    sections.Add(record);
                }

                if (propertyOverrideClasses.Contains(record.ClassNumber))
                {
                    if (!propertyOverrideRecords.TryGetValue(length, out var overrides))
                        propertyOverrideRecords.Add(length, overrides = []);
                    overrides.Add(record);
                }
                if (labelClasses.Contains(record.ClassNumber))
                {
                    if (!labelRecords.TryGetValue(length, out var labels)) labelRecords.Add(length, labels = []);
                    labels.Add(record);
                }

                if (!types.TryGetValue(record.ClassNumber, out var objectClass)) continue;
                if (objectClass.Name != "Object") AddElement(elements, record.ObjectId, objectClass);
                if (HasBasicParameters(objectClass.Name) && (objectClass.Name != "Object" || useObjectRecords))
                {
                    if (!directories.TryGetValue(length, out var records)) directories.Add(length, records = []);
                    records.Add(record);
                }
            }
        }

        var parameters = new Dictionary<Guid, BasicParameters>();
        var indexedParameters = new Dictionary<Guid, BasicParameters>();
        var materialReferences = new Dictionary<Guid, (int? Material, int? Composite)>();
        var locations = new Dictionary<Guid, ElementLocation>();
        var elevations = new Dictionary<Guid, Elevations?>();
        var retainedWallCaps = new Dictionary<Guid, (double? Bottom, double? Top)>();
        var boxes = new Dictionary<Guid, PlaBoundingBox?>();
        var beamVolumes = new Dictionary<Guid, double?>();
        var beamAxes = new Dictionary<Guid, PlaBeamReferenceAxis?>();
        var wallReferencePlans = new Dictionary<Guid, PlaWallReferencePlan?>();
        var wallProfiles = new Dictionary<Guid, PlaWallProfile?>();
        var wallExtrusions = new Dictionary<Guid, PlaWallExtrusion?>();
        var wallDoors = new Dictionary<Guid, List<Guid>?>();
        var wallsNeedingDoorSolids = new HashSet<Guid>();
        var columnExtrusions = new Dictionary<Guid, PlaColumnExtrusion?>();
        var columnCutMeshes = new Dictionary<Guid, PlaColumnCutMesh?>();
        var memberProfiles = new Dictionary<Guid, PlaMemberProfile?>();
        var slabGeometry = new Dictionary<Guid, PlaSlabGeometry?>();
        var slabOpeningSources = new Dictionary<Guid, SlabOpeningSources?>();
        var doorHosts = new Dictionary<Guid, Guid>();
        var doorPlacements = new Dictionary<Guid, PlaDoorPlacement?>();
        var doorLibraryParts = new Dictionary<Guid, PlaLibraryPartReference?>();
        var doorParameterLinks = new Dictionary<Guid, DoorParameterLink>();
        var pools = directories.Count != 0 || propertyOverrideRecords.Count != 0 ? document.FindCompressedPools() : [];
        var doorParameterTables = elements.Values.Any(element => element.Guid == DoorClass)
            ? ReadDoorParameterTables(pools, libraryParameterRecords) : new Dictionary<Guid, DoorParameterTable?>();
        var openingLinks = new Dictionary<Guid, OpeningLinks>();
        var openingGeometry = new Dictionary<Guid, PlaOpeningGeometry?>();
        var storyTable = new Lazy<Dictionary<int, Story>>(() => ReadStories(document, classes));
        var partitionTable = new Lazy<Dictionary<Guid, int?>>(() => ReadStoryPartitions(document, classes, storyTable.Value));
        var savedPropertyOverrides = new Dictionary<Guid, SavedPropertyOverride>();
        if (directories.Count != 0 || propertyOverrideRecords.Count != 0)
        {
            var grids = databaseRecords.Count == 0 ? null : ReadGridElements(document, classes);
            var junctionAttributes = new Lazy<JunctionAttributes>(() => ReadJunctionAttributes(document, classes));
            var labelOwners = new Lazy<SavedLabels>(() => ReadLabelOwners(document, labelRecords, labelClasses));
            var window = new List<byte>();
            foreach (var pool in pools)
            {
                byte[] data = pool.Decode();
                ulong units = (ulong)(data.Length / ExtentUnit);
                if (data.Length % ExtentUnit == 0 && propertyOverrideRecords.TryGetValue(units, out var overrides))
                    ReadPropertyOverrideRecords(data, overrides, savedPropertyOverrides);
                var sectionRows = sectionRecords.GetValueOrDefault(units);
                if (data.Length % ExtentUnit == 0 && directories.TryGetValue(units, out var records))
                {
                    var placedElements = new HashSet<Guid>();
                    if (grids is not null && databaseRecords.TryGetValue(units, out var roots))
                    {
                        foreach (var root in roots)
                        {
                            var database = ReadIndexedPayload(data, root);
                            if (database is not null)
                                ReadRelationshipElements(database, ModelDatabaseClass, DatabaseElementList, placedElements);
                        }
                    }

                    var stampRecords = records.Any(r => types[r.ClassNumber].Name == "Zone")
                        ? records.Where(r => types[r.ClassNumber].Guid == ObjectClass).ToLookup(r => r.ObjectId)
                        : null;
                    var sections = sectionRows?.ToLookup(r => r.ObjectId);
                    var geometry = new ModelGeometry(data, sections, sectionClasses, junctionAttributes, storyTable, partitionTable,
                        labelOwners, doorParameterTables);
                    foreach (var record in records)
                    {
                        var objectClass = types[record.ClassNumber];
                        bool isObject = objectClass.Name == "Object";
                        if (isObject)
                        {
                            if (grids is null || grids.Contains(record.ObjectId) || !placedElements.Contains(record.ObjectId)) continue;
                            AddElement(elements, record.ObjectId, objectClass);
                        }
                        var body = ReadIndexedPayload(data, record);
                        if (body is null) continue;
                        int frame = FindClassFrame(body, objectClass.Guid);
                        const int objectFrameOffset = 49;
                        bool olderObject = isObject && frame < 0 &&
                            FindClassFrame(body, ObjectClass, version: 0x82) == objectFrameOffset;
                        if (olderObject) frame = objectFrameOffset;
                        if (frame >= 0 && objectClass.Name is "Wall" or "Slab")
                        {
                            var reference = ReadMaterialReference(body.AsSpan(frame), objectClass.Name == "Wall");
                            if (!materialReferences.TryAdd(record.ObjectId, reference) && materialReferences[record.ObjectId] != reference)
                                materialReferences[record.ObjectId] = default;
                        }
                        var fields = olderObject ? new BasicParameters(ReadFramedElementId(body.AsSpan(frame), 163), null, null) :
                            objectClass.Name == "Zone" && stampRecords is not null ? ReadZoneParameters(body, data, stampRecords) :
                            ReadBasicParameters(body, objectClass);
                        var location = frame >= 0 ? ReadElementLocation(body, objectClass.Name!, frame, olderObject) : default;
                        if (location != default && !locations.TryAdd(record.ObjectId, location) && locations[record.ObjectId] != location)
                            locations[record.ObjectId] = default;
                        (double X0, double Y0, double X1, double Y1)? wallFootprint = null;
                        PlaWallReferencePlan? wallPlan = null;
                        PlaWallProfile? wallProfile = null;
                        if (objectClass.Name == "Wall" && frame >= 0)
                        {
                            var wall = body.AsSpan(frame);
                            var settings = AfterCountedString(wall, 197);
                            wallPlan = ReadWallReferencePlan(settings, out wallFootprint,
                                allowEmptyOutline: HasUncachedDoorWallOutline(wall, settings));
                            if (!wallReferencePlans.TryAdd(record.ObjectId, wallPlan) && wallReferencePlans[record.ObjectId] != wallPlan)
                                wallReferencePlans[record.ObjectId] = null;
                            wallProfile = ReadWallProfile(wall, settings, record.ObjectId, geometry);
                            if (!wallProfiles.TryAdd(record.ObjectId, wallProfile))
                            {
                                var previous = wallProfiles[record.ObjectId];
                                if (!SameProfile(previous?.ProfileIndex, wallProfile?.ProfileIndex, previous?.Components, wallProfile?.Components))
                                    wallProfiles[record.ObjectId] = null;
                            }
                        }
                        PlaMemberProfile? memberProfile = null;
                        if (frame >= 0 && objectClass.Name is ("Beam" or "Column"))
                        {
                            memberProfile = ReadMemberProfile(body.AsSpan(frame), record.ObjectId, objectClass.Name == "Beam", geometry);
                            if (!memberProfiles.TryAdd(record.ObjectId, memberProfile))
                            {
                                var previous = memberProfiles[record.ObjectId];
                                if (!SameProfile(previous?.ProfileIndex, memberProfile?.ProfileIndex,
                                    previous?.Components, memberProfile?.Components))
                                    memberProfiles[record.ObjectId] = null;
                            }
                        }
                        if (objectClass.Name == "Door" && frame >= 0)
                        {
                            var door = body.AsSpan(frame);
                            Guid host = ReadDoorHost(door, record.ObjectId, geometry);
                            if (!doorHosts.TryAdd(record.ObjectId, host) && doorHosts[record.ObjectId] != host)
                                doorHosts[record.ObjectId] = Guid.Empty;
                            var placement = ReadDoorPlacement(geometry, door, host);
                            if (!doorPlacements.TryAdd(record.ObjectId, placement) && doorPlacements[record.ObjectId] != placement)
                                doorPlacements[record.ObjectId] = null;
                            var library = ReadDoorLibraryPart(geometry, door, record.ObjectId, out var parameterLink);
                            if (!doorLibraryParts.TryAdd(record.ObjectId, library) && doorLibraryParts[record.ObjectId] != library)
                                doorLibraryParts[record.ObjectId] = null;
                            if (!doorParameterLinks.TryAdd(record.ObjectId, parameterLink) &&
                                doorParameterLinks[record.ObjectId] != parameterLink)
                                doorParameterLinks[record.ObjectId] = default;
                        }
                        if (objectClass.Name == "Opening" && frame >= 0)
                        {
                            var opening = body.AsSpan(frame);
                            var links = ReadOpeningLinks(opening, record.ObjectId, geometry, elements);
                            if (!openingLinks.TryAdd(record.ObjectId, links))
                            {
                                var previous = openingLinks[record.ObjectId];
                                if (previous.Host != links.Host)
                                    openingLinks[record.ObjectId] = default;
                                else if (previous.Targets is null || links.Targets is null ||
                                    !previous.Targets.SequenceEqual(links.Targets))
                                    openingLinks[record.ObjectId] = previous with { Targets = null };
                            }
                            var planes = ReadOpeningElevations(opening, record.ObjectId, fields.OpeningProfile, links, geometry,
                                elements, out var cutGeometry);
                            if (!openingGeometry.TryAdd(record.ObjectId, cutGeometry) &&
                                !SameOpeningGeometry(openingGeometry[record.ObjectId], cutGeometry))
                                openingGeometry[record.ObjectId] = null;
                            if (!elevations.TryAdd(record.ObjectId, planes) && elevations[record.ObjectId] != planes)
                                elevations[record.ObjectId] = null;
                        }
                        if (location.HomeStoryIndex.HasValue)
                        {
                            var framed = body.AsSpan(frame);
                            PlaBoundingBox? box = null;
                            PlaWallExtrusion? wallExtrusion = null;
                            List<Guid>? isolatedDoors = null;
                            bool needsDoorSolid = false;
                            (double? Bottom, double? Top) retainedCap = default;
                            PlaColumnExtrusion? columnExtrusion = null;
                            PlaColumnCutMesh? columnCutMesh = null;
                            var bounds = objectClass.Name switch
                            {
                                "Column" => ReadColumnElevations(framed, record.ObjectId, location, geometry, memberProfile,
                                    out box, out columnExtrusion, out columnCutMesh),
                                "Wall" => ReadWallElevations(framed, record.ObjectId, location, geometry, wallFootprint, wallPlan,
                                    wallProfile, out box, out wallExtrusion, out isolatedDoors, out needsDoorSolid, out retainedCap),
                                _ => ReadElevations(framed, objectClass.Name!),
                            };
                            if (bounds is { } planes && objectClass.Name is "Slab" or "Zone")
                                box = ReadFloorBox(framed, objectClass.Name, planes, geometry.StoryElevation(location.HomeStoryIndex));
                            if (objectClass.Name == "Beam" && geometry.StoryElevation(location.HomeStoryIndex) is { } home)
                            {
                                var axis = ReadBeamReferenceAxis(framed, record.ObjectId, geometry);
                                if (!beamAxes.TryAdd(record.ObjectId, axis) && beamAxes[record.ObjectId] != axis)
                                    beamAxes[record.ObjectId] = null;
                                bounds = ReadBeamGeometry(framed, record.ObjectId, geometry, memberProfile, axis, home, out box, out double? volume);
                                if (!beamVolumes.TryAdd(record.ObjectId, volume) && beamVolumes[record.ObjectId] != volume)
                                    beamVolumes[record.ObjectId] = null;
                            }
                            if (bounds is not null && !elevations.TryAdd(record.ObjectId, bounds) && elevations[record.ObjectId] != bounds)
                                elevations[record.ObjectId] = null;
                            if (objectClass.Name == "Wall" && !retainedWallCaps.TryAdd(record.ObjectId, retainedCap) &&
                                retainedWallCaps[record.ObjectId] != retainedCap)
                                retainedWallCaps[record.ObjectId] = default;
                            if (box is not null && !boxes.TryAdd(record.ObjectId, box) && boxes[record.ObjectId] != box)
                                boxes[record.ObjectId] = null;
                            if (objectClass.Name == "Wall" && !wallExtrusions.TryAdd(record.ObjectId, wallExtrusion) &&
                                !SameWallExtrusion(wallExtrusions[record.ObjectId], wallExtrusion))
                                wallExtrusions[record.ObjectId] = null;
                            if (objectClass.Name == "Wall" && !wallDoors.TryAdd(record.ObjectId, isolatedDoors) &&
                                (isolatedDoors is null || wallDoors[record.ObjectId] is not { } previousDoors ||
                                    !isolatedDoors.SequenceEqual(previousDoors)))
                                wallDoors[record.ObjectId] = null;
                            if (needsDoorSolid) wallsNeedingDoorSolids.Add(record.ObjectId);
                            if (objectClass.Name == "Column" && !columnExtrusions.TryAdd(record.ObjectId, columnExtrusion) &&
                                !SameColumnExtrusion(columnExtrusions[record.ObjectId], columnExtrusion))
                                columnExtrusions[record.ObjectId] = null;
                            if (objectClass.Name == "Column" && !columnCutMeshes.TryAdd(record.ObjectId, columnCutMesh) &&
                                !SameColumnCutMesh(columnCutMeshes[record.ObjectId], columnCutMesh))
                                columnCutMeshes[record.ObjectId] = null;
                            if (objectClass.Name == "Slab")
                            {
                                SlabOpeningSources? sources = null;
                                var slab = box is not null ? ReadSlabGeometry(framed, record.ObjectId, geometry, box, out sources) : null;
                                if (!slabOpeningSources.TryAdd(record.ObjectId, sources))
                                {
                                    var previous = slabOpeningSources[record.ObjectId];
                                    slabOpeningSources[record.ObjectId] = sources is { } found && previous is { } known &&
                                        known.Openings.SequenceEqual(found.Openings)
                                        ? known with { CanReconstructSolid = known.CanReconstructSolid && found.CanReconstructSolid }
                                        : null;
                                }
                                if (!slabGeometry.TryAdd(record.ObjectId, slab))
                                {
                                    if (slab is null || slabGeometry[record.ObjectId] is not { } previous ||
                                        (previous.Bottom, previous.Top) != (slab.Bottom, slab.Top) ||
                                        previous.Rings.Count != slab.Rings.Count ||
                                        previous.Rings.Where((ring, i) => !ring.SequenceEqual(slab.Rings[i])).Any() ||
                                        !(previous.Arcs ?? []).SequenceEqual(slab.Arcs ?? []))
                                        slabGeometry[record.ObjectId] = null;
                                    else if (previous.Volume != slab.Volume)
                                        slabGeometry[record.ObjectId] = slab with { Volume = null };
                                }
                            }
                        }
                        AddParameters(indexedParameters, record.ObjectId, fields);
                    }
                }

                foreach (var subgroup in PoolSubgroupReader.ReadPool(data, window))
                {
                    if (subgroup.Partial || subgroup.Version != 1 || subgroup.ObjectId is not { } id ||
                        !elements.TryGetValue(id, out var objectClass) || objectClass.Number != subgroup.Kind ||
                        objectClass.Name is "Object" or "Zone" || !HasBasicParameters(objectClass.Name) || indexedParameters.ContainsKey(id))
                        continue;

                    AddParameters(parameters, id, ReadBasicParameters(subgroup.Data, objectClass));
                }
            }
            foreach (var (id, fields) in indexedParameters) parameters[id] = fields;
        }

        var layerNames = locations.Count == 0 ? [] : ReadLayerNames(document, classes);
        var stories = locations.Count == 0 ? [] : storyTable.Value;
        var storyPartitions = stories.Count == 0 || !locations.Values.Any(l => l.TopStoryPartition != Guid.Empty)
            ? [] : partitionTable.Value;
        var classifications = ReadClassificationCatalog(document, classes, elements);
        var propertyOverrides = ReadPropertyOverrides(document, classes, elements, savedPropertyOverrides);
        var result = new List<PlaElement>(elements.Count);
        foreach (var (id, objectClass) in elements)
        {
            var fields = parameters.GetValueOrDefault(id);
            var location = locations.GetValueOrDefault(id);
            var material = materialReferences.GetValueOrDefault(id);
            var links = openingLinks.GetValueOrDefault(id);
            Guid hostId = objectClass.Name == "Door" ? doorHosts.GetValueOrDefault(id) : links.Host;
            var host = locations.GetValueOrDefault(location.Host);
            int? layer = objectClass.Name is "Door" or "Opening" ? host.LayerIndex : location.LayerIndex;
            int? story = objectClass.Name == "Opening" ? host.HomeStoryIndex : location.HomeStoryIndex;
            var homeStory = story is { } storyIndex ? stories.GetValueOrDefault(storyIndex) : default;
            double? beamVolume = beamVolumes.GetValueOrDefault(id);
            var bounds = story.HasValue && (objectClass.Name != "Opening" || fields.OpeningProfile is not null)
                ? elevations.GetValueOrDefault(id) : null;
            if (bounds is { StoredProjectBottom: { } boxBottom } wall &&
                homeStory.Elevation is { } storyElevation && !(Math.Abs(wall.Bottom + storyElevation - boxBottom) <= 1e-8))
                bounds = null;
            var box = story.HasValue ? boxes.GetValueOrDefault(id) : null;
            var extrusion = box is { } envelope ? wallExtrusions.GetValueOrDefault(id) ??
                ReadIsolatedDoorWall(id, bounds, homeStory.Elevation, envelope) : null;
            if (extrusion is null && wallsNeedingDoorSolids.Contains(id))
            {
                box = null;
                bounds = null;
            }
            var retainedCap = story.HasValue && !elevations.ContainsKey(id) ? retainedWallCaps.GetValueOrDefault(id) : default;
            double? bottom = bounds?.Bottom ?? retainedCap.Bottom;
            double? top = bounds?.Top ?? retainedCap.Top;
            result.Add(new PlaElement(id, objectClass.Name!, fields.ElementId, fields.WallThickness, fields.WallChordLength)
            {
                LayerIndex = layer,
                LayerName = layer is { } layerIndex ? layerNames.GetValueOrDefault(layerIndex) : null,
                HomeStoryIndex = story,
                HomeStoryName = homeStory.Name,
                TopLinkedStoryIndex = storyPartitions.GetValueOrDefault(location.TopStoryPartition),
                BuildingMaterialIndex = material.Material,
                CompositeIndex = material.Composite,
                HostGuid = hostId != Guid.Empty ? hostId : null,
                DoorDimensions = fields.DoorDimensions,
                DoorPlacement = hostId != Guid.Empty ? doorPlacements.GetValueOrDefault(id) : null,
                DoorWallHoleDimensions = FindDoorParameterTable(id)?.Dimensions,
                LibraryPartReference = doorLibraryParts.GetValueOrDefault(id),
                OpeningCutElementGuids = links.Targets,
                OpeningProfile = fields.OpeningProfile,
                OpeningGeometry = fields.OpeningProfile is not null && links.Host != Guid.Empty
                    ? openingGeometry.GetValueOrDefault(id) : null,
                BoundingBox = box,
                BeamReferenceAxis = story.HasValue ? beamAxes.GetValueOrDefault(id) : null,
                BeamNetVolume = story.HasValue && (beamVolume == 0 || boxes.GetValueOrDefault(id) is not null)
                    ? beamVolume : null,
                WallReferencePlan = wallReferencePlans.GetValueOrDefault(id),
                WallProfile = wallProfiles.GetValueOrDefault(id),
                WallExtrusion = extrusion,
                ColumnExtrusion = story.HasValue && elevations.GetValueOrDefault(id) is not null ? columnExtrusions.GetValueOrDefault(id) : null,
                ColumnCutMesh = story.HasValue && elevations.GetValueOrDefault(id) is not null ? columnCutMeshes.GetValueOrDefault(id) : null,
                MemberProfile = memberProfiles.GetValueOrDefault(id),
                Classifications = classifications.Systems.Count == 0 ? null :
                    classifications.Assignments.TryGetValue(id, out var assigned) ? assigned.AsReadOnly() : [],
                PropertyOverrides = propertyOverrides.GetValueOrDefault(id),
                SlabGeometry = story.HasValue && boxes.GetValueOrDefault(id) is not null && slabGeometry.GetValueOrDefault(id) is { } slab
                    ? ApplySlabOpenings(slab, id, slabOpeningSources.GetValueOrDefault(id), openingLinks, openingGeometry, parameters)
                    : null,
                BottomElevationToHomeStory = bottom,
                TopElevationToHomeStory = top,
                BottomElevationToProjectZero = ToProjectZero(bottom, homeStory.Elevation),
                TopElevationToProjectZero = ToProjectZero(top, homeStory.Elevation),
            });
        }
        return result.AsReadOnly();

        DoorParameterTable? FindDoorParameterTable(Guid door)
        {
            return ResolveDoorParameterTable(doorParameterTables, doorParameterLinks.GetValueOrDefault(door));
        }

        PlaWallExtrusion? ReadIsolatedDoorWall(Guid wall, Elevations? bounds, double? storyElevation, PlaBoundingBox box)
        {
            if (wallDoors.GetValueOrDefault(wall) is not { } doors ||
                wallReferencePlans.GetValueOrDefault(wall) is not { } plan ||
                bounds is not { } planes || storyElevation is not double home) return null;
            var apertures = new List<(double Station, double Width, double Bottom, double Top)>(doors.Count);
            foreach (Guid door in doors)
            {
                if (doorHosts.GetValueOrDefault(door) != wall ||
                    doorLibraryParts.GetValueOrDefault(door) != new PlaLibraryPartReference(SimpleDoorOpeningPart, SimpleDoorOpeningRevision) ||
                    doorPlacements.GetValueOrDefault(door) is not { SillElevationToHostStory: { } sill } placement ||
                    parameters.GetValueOrDefault(door).DoorDimensions is not { } nominal ||
                    FindDoorParameterTable(door) is not { NoOversize: true, Dimensions: var dimensions } ||
                    Math.Abs(nominal.Width - dimensions.Width) > JunctionTolerance ||
                    Math.Abs(nominal.Height - dimensions.Height) > JunctionTolerance)
                    return null;
                apertures.Add((placement.DistanceAlongWall, dimensions.Width, sill, sill + dimensions.Height));
            }
            return ReadDoorCutWallExtrusion(plan, planes, home, box, apertures);
        }
    }

    private static bool IsRawTable(PlaBlock block, ushort kind) =>
        block.Tag == "BL" && block.Kind == kind && block.Version == 1 && block.Mode == PlaBlockMode.Raw;

    private static void AddElement(Dictionary<Guid, PlaObjectClass> elements, Guid id, PlaObjectClass objectClass)
    {
        if (!elements.TryAdd(id, objectClass) && elements[id].Guid != objectClass.Guid)
            throw new InvalidDataException($"Object {id:D} has conflicting classes in the directories.");
    }

    private static HashSet<Guid>? ReadGridElements(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var classNumbers = classes.Where(c => c.Guid == GridStoreClass).Select(c => c.Number).ToHashSet();
        var grids = new HashSet<Guid>();
        foreach (var block in document.Blocks)
        {
            if (block.Tag != "BL" || !classNumbers.Contains(block.Kind)) continue;
            if (block.Version != 1 || block.Mode != PlaBlockMode.Raw ||
                !ReadRelationshipElements(document.ReadPayload(block), GridStoreClass, GridElementList, grids))
                return null;
        }
        return grids;
    }

    private static byte[]? ReadIndexedPayload(ReadOnlySpan<byte> pool, ObjectDirectoryRecord record)
    {
        int offset = (int)(record.Position * ExtentUnit);
        var extent = pool.Slice(offset, (int)(record.Count * ExtentUnit));
        if (!BlockHeaderParser.TryParse(extent, 0, extent.Length, out var header, poolSubgroup: true) ||
            header.Tag != "BL" || header.Version != 1 ||
            header.Kind != record.ClassNumber || header.Guid != record.ObjectId ||
            (header.Flags & FdbFormat.FlagExtra) == 0 || header.End > extent.Length - sizeof(uint))
            return null;

        // In this directory layout the four-byte field is outside the packed size.
        var packed = extent.Slice((int)header.PayloadOffset + sizeof(uint), (int)header.PackedSize);
        if (header.Mode == PlaBlockMode.Raw) return packed.ToArray();
        if (header.Mode == PlaBlockMode.Lz) return QuickLzLevelOne.ReadFrames(packed, (int)header.UnpackedSize);

        var body = new byte[header.UnpackedSize];
        PayloadDecoder.DecodeZeroMask(packed, body, out int written, out _);
        return written == body.Length ? body : null;
    }

    private static bool ReadRelationshipElements(ReadOnlySpan<byte> data, Guid recordClass,
        Guid listKind, HashSet<Guid> elements)
    {
        const int bodyOffset = 39;
        const int relationshipHeaderSize = sizeof(uint) + sizeof(ushort);
        if (data.Length < bodyOffset + relationshipHeaderSize || new Guid(data[..16]) != recordClass)
            return false;
        uint bodySize = BinaryPrimitives.ReadUInt32LittleEndian(data[(bodyOffset - sizeof(uint))..]);
        if (bodySize > data.Length - bodyOffset - relationshipHeaderSize) return false;
        int sizeOffset = bodyOffset + (int)bodySize;
        int relationshipOffset = sizeOffset + sizeof(uint);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[sizeOffset..]) != data.Length - relationshipOffset ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[relationshipOffset..]) != 1)
            return false;

        Span<byte> marker = stackalloc byte[17];
        marker[0] = 1;
        listKind.TryWriteBytes(marker[1..]);
        var relationships = data[(relationshipOffset + sizeof(ushort))..];
        int start = relationships.IndexOf(marker);
        if (start < 0 || relationships[(start + 1)..].IndexOf(marker) >= 0) return false;
        var list = relationships[(start + marker.Length)..];
        if (list.Length < sizeof(uint))
            throw new InvalidDataException($"The element list {listKind:D} is missing its count.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(list);
        var guids = list[sizeof(uint)..];
        if (count > guids.Length / 16)
            throw new InvalidDataException($"The element list {listKind:D} exceeds its relationship section.");
        for (int i = 0; i < count; i++) elements.Add(new Guid(guids.Slice(i * 16, 16)));
        return true;
    }

    private static void AddParameters(Dictionary<Guid, BasicParameters> parameters, Guid id, BasicParameters fields)
    {
        if (fields.ElementId is null || parameters.TryAdd(id, fields) || parameters[id] == fields) return;
        var basic = fields with { OpeningProfile = null, DoorDimensions = null };
        parameters[id] = (parameters[id] with { OpeningProfile = null, DoorDimensions = null }) == basic ? basic : default;
    }

    private static int DirectoryHeaderSize(ReadOnlySpan<byte> data, Guid layout)
    {
        if (data.Length >= 40 && new Guid(data.Slice(24, 16)) == layout) return 40;
        if (data.Length >= 42 && BinaryPrimitives.ReadUInt16LittleEndian(data) == 0 &&
            new Guid(data.Slice(26, 16)) == layout) return 42;
        return 0;
    }

    private static int ClassElementIdOffset(string? name) => name switch
    {
        "Slab" or "Morph" or "CurtainWall" or "Mesh" or "Opening" or "Railing" or "Stair" => 185,
        "Roof" => 197,
        "Door" => 286,
        "Lamp" or "Object" => 289,
        _ => 0,
    };

    private static bool HasBasicParameters(string? name) =>
        name is "Wall" or "Beam" or "Column" or "Zone" || ClassElementIdOffset(name) != 0;

    private static BasicParameters ReadZoneParameters(ReadOnlySpan<byte> zone, ReadOnlySpan<byte> pool,
        ILookup<Guid, ObjectDirectoryRecord> stampRecords)
    {
        Span<byte> marker = stackalloc byte[19];
        marker[0] = 1;
        marker[1] = 0;
        marker[2] = 0x11;
        ZoneStampRelation.TryWriteBytes(marker[3..]);
        int start = zone.IndexOf(marker);
        if (start < sizeof(uint) || zone.Length - start < marker.Length + 16 ||
            zone[(start + 1)..].IndexOf(marker) >= 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(zone[(start - sizeof(uint))..]) != zone.Length - start)
            return default;

        var stampId = new Guid(zone.Slice(start + marker.Length, 16));
        string? elementId = null;
        foreach (var record in stampRecords[stampId])
        {
            var stamp = ReadIndexedPayload(pool, record);
            if (stamp is null) continue;
            string? value = ReadClassElementId(stamp, ObjectClass, ClassElementIdOffset("Object"));
            if (value is null) continue;
            if (elementId is not null && value != elementId) return default;
            elementId = value;
        }
        return new BasicParameters(elementId, null, null);
    }

    private static BasicParameters ReadBasicParameters(ReadOnlySpan<byte> data, PlaObjectClass objectClass)
    {
        int relativeOffset = ClassElementIdOffset(objectClass.Name);
        if (objectClass.Name is "Opening" or "Door")
        {
            int frame = FindClassFrame(data, objectClass.Guid);
            if (frame < 0 || relativeOffset > data.Length - frame ||
                BinaryPrimitives.ReadUInt16LittleEndian(data[(frame + relativeOffset - 2)..]) != 1)
                return default;
            string? elementId = ReadElementId(data[(frame + relativeOffset)..], out int consumed);
            if (elementId is null) return default;
            var settings = data[(frame + relativeOffset + consumed)..];
            return objectClass.Name == "Door"
                ? new BasicParameters(elementId, null, null, DoorDimensions: ReadDoorDimensions(data[frame..], settings))
                : new BasicParameters(elementId, null, null, ReadOpeningProfile(settings));
        }
        if (relativeOffset != 0)
            return new BasicParameters(ReadClassElementId(data, objectClass.Guid, relativeOffset), null, null);

        BasicParameters result = default;
        while (data.IndexOf(ElementIdFrame) is var offset && offset >= 0)
        {
            var value = data[(offset + ElementIdFrame.Length)..];
            data = data[(offset + 1)..];
            string? elementId = ReadElementId(value, out int consumed);
            if (string.IsNullOrEmpty(elementId)) continue;
            if (result.ElementId is not null) return default;
            result = new BasicParameters(elementId, null, null);
            if (objectClass.Name == "Wall" && value[consumed..] is { Length: >= 93 } tail)
            {
                double dx = BinaryPrimitives.ReadDoubleLittleEndian(tail[16..]) - BinaryPrimitives.ReadDoubleLittleEndian(tail);
                double dy = BinaryPrimitives.ReadDoubleLittleEndian(tail[24..]) - BinaryPrimitives.ReadDoubleLittleEndian(tail[8..]);
                double chord = Math.Sqrt(dx * dx + dy * dy);
                double thickness = BinaryPrimitives.ReadDoubleLittleEndian(tail[85..]);
                result = result with
                {
                    WallChordLength = double.IsFinite(chord) && chord > 0 ? chord : null,
                    WallThickness = double.IsFinite(thickness) && thickness > 0 &&
                        thickness == BinaryPrimitives.ReadDoubleLittleEndian(tail[77..]) ? thickness : null,
                };
            }
        }
        return result;
    }

    private static string? ReadClassElementId(ReadOnlySpan<byte> data, Guid classGuid, int relativeOffset)
    {
        int start = FindClassFrame(data, classGuid);
        return start < 0 ? null : ReadFramedElementId(data[start..], relativeOffset);
    }

    private static string? ReadFramedElementId(ReadOnlySpan<byte> frame, int offset) =>
        offset >= 2 && offset <= frame.Length && BinaryPrimitives.ReadUInt16LittleEndian(frame[(offset - 2)..]) == 1
            ? ReadElementId(frame[offset..], out _)
            : null;

    private static int FindClassFrame(ReadOnlySpan<byte> data, Guid classGuid, byte version = 0x83)
    {
        Span<byte> frame = stackalloc byte[26];
        ClassFrame.CopyTo(frame);
        frame[4] = version;
        classGuid.TryWriteBytes(frame[ClassFrame.Length..]);
        int start = data.IndexOf(frame);
        return start < 0 || data[(start + 1)..].IndexOf(frame) >= 0 ? -1 : start;
    }

    private static string? ReadElementId(ReadOnlySpan<byte> value, out int consumed)
    {
        var elementId = ReadCountedUnicode(value, out consumed);
        if (elementId is null || !elementId.Any(char.IsControl)) return elementId;
        consumed = 0;
        return null;
    }

    private static string? ReadCountedUnicode(ReadOnlySpan<byte> value, out int consumed)
    {
        consumed = 0;
        if (value.Length < 4) return null;
        uint units = BinaryPrimitives.ReadUInt32LittleEndian(value);
        if (units > (value.Length - 4) / 2) return null;
        int size = (int)units * 2;
        try
        {
            string text = Utf16.GetString(value.Slice(4, size));
            consumed = 4 + size;
            return text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private readonly record struct BasicParameters(string? ElementId, double? WallThickness, double? WallChordLength,
        PlaOpeningProfile? OpeningProfile = null, PlaDoorDimensions? DoorDimensions = null);
}
