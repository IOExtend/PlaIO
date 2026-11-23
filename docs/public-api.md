# Public API

The `PlaIO` package is the supported surface listed here. The entry points (`PlaDocument`, `BimxDocument`, `PlaFile`,
`PlaReaderOptions`) are in the namespace `PlaIO`; the types that describe the opened document (`PlaBlock`,
`ObjectDirectory`, `CompressedPool`, `PoolSubgroup`, `PlaStream`, `FdbHeader`) are in `PlaIO.Model`. Everything else
(the block scanner, the header parser, the payload decoders, the directory search, the PNG chunk walker) is `internal`
in `PlaIO.Internal`; it can change in any release and is visible only to the test project through `InternalsVisibleTo`.

## File kind

| Member | Use |
|---|---|
| `PlaFileKind` | `Unknown` (0), `Fdb` (1): bytes 0..7 are `ROF FDB `, the container that `.pla` and `.pln` files share (the two are not told apart), `Bimx` (2): bytes 0..3 are `BIMx`. |
| `PlaFile.SignatureLength` | `8`, the number of leading bytes needed to recognise every kind. |
| `PlaFile.DetectKind(ReadOnlySpan<byte> head)` | The kind of a file whose first bytes are `head`. |
| `PlaFile.DetectKind(string path)` | Reads at most 8 bytes of the file; I/O errors propagate. |

## Reader options

| Member | Use |
|---|---|
| `PlaReaderOptions.Default` | The options a reader uses when the caller passes none. |
| `PlaReaderOptions.MaxGlobalStreamBytes` | Cap on the decoded global stream (`OpenStream`) and on the window a mode-2 `ReadPayload` builds (default: the most one in-memory stream can hold). A block whose declared size grows the window past the cap throws `InvalidDataException` before it is decoded. |
| `PlaReaderOptions.MaxDecodedPoolBytes` | Cap on the decoded size of one compressed pool (default: 512 MiB). A pool that declares more makes `Decode` throw `InvalidDataException`. |
| `PlaReaderOptions.MaxCoverBytes` | Cap on the `.bimx` cover image read into memory (default: 64 MiB). A larger cover makes `ReadCover` throw `InvalidDataException`. |

The caps are reader limits, not facts of the format. The options are immutable — build a new instance to change a
limit; `Default` cannot be mutated, so one reader never changes the limits of another. The entry-point signatures of
the 0.1.0 surface are unchanged: the options come through added overloads (`Open(string, PlaReaderOptions?)` and
`Open(Stream, bool, PlaReaderOptions?)`; the same for `BimxDocument`).

## FDB container (`.pla`, `.pln`)

| Member | Use |
|---|---|
| `PlaDocument.Open(string path)`, `PlaDocument.Open(Stream stream, bool leaveOpen = false)` | Opens a container and builds its block index. The stream must be readable and seekable (`ArgumentException` otherwise); data that is not an FDB container or is shorter than 32 bytes throws `InvalidDataException`. The document owns the stream unless `leaveOpen` is true; when `Open` throws, the stream stays with the caller. |
| `PlaDocument.Open(string path, PlaReaderOptions? options)`, `PlaDocument.Open(Stream stream, bool leaveOpen, PlaReaderOptions? options)` | The same opens with explicit reader options. |
| `PlaDocument.Length` | Length of the file in bytes. |
| `PlaDocument.Header` | The `FdbHeader`. |
| `PlaDocument.Blocks` | The block index in file order, found by scanning from offset 32. No on-disk block map is known, so completeness is not claimed. The scan can take a byte run for a block header, so the index may contain false entries. In real files a small share of mode 1 blocks does not decode to its declared size, and `ReadPayload` throws `InvalidDataException` for them. |
| `PlaDocument.ReadPayload(PlaBlock block)` | The decoded payload of a block of this document. Decodes all three storage modes: raw (mode 0), zero masks (mode 1) and the LZ token stream (mode 2). A mode 2 payload may start with a 9-byte stream header with a 0x46 or 0x47 marker and matching packed and unpacked sizes. The token stream produces the block's declared unpacked size — some blocks declare a buffer larger than the stream produces, the tail of the buffer stays zero — and may reference the output of any preceding block, so the payload is produced by decoding every preceding block of the file in order into one global window; the cost of the call grows with the size of the file. Preceding blocks that do not decode (false entries of the scan) are skipped and leave the window unchanged. Throws `InvalidDataException` when a back-reference reaches beyond the decoded stream or the file is cut short, and `ArgumentException` when the block is not from this document. |
| `PlaDocument.FindDirectories()` | Searches every raw (mode 0) block large enough for 64 records of 30 bytes for an object directory, at most one per block. Recomputed on each call; mode 1 payloads are not searched. |
| `PlaDocument.ReadEmbeddedLibraryCatalog()` | Reads supported embedded GSM library parts in file order from the archive asset class. |
| `PlaEmbeddedLibraryCatalog`, `PlaEmbeddedLibraryPart` | Each part exposes the archive asset GUID, saved file name, main GUID, revision GUID and original GSM bytes. Physical offsets of asset blocks or pools with unreadable data are reported separately. |
| `PlaDocument.OpenStream()` | The document's decoded global stream (`PlaStream`): every block's payload decoded in block order. Blocks that fail to decode are skipped and mark the remaining offsets as unreliable. The stream holds the decoded bytes in memory and can outlive the document. |
| `PlaDocument.Dispose()` | Releases the stream unless the document was opened with `leaveOpen`. |
| `FdbHeader` | `Size` (64), `LayoutVersion` (u16 at offset 8), `FormatVersion` (u16 at offset 10), `Field12` (u32 at offset 12; meaning not documented), `Raw` (the first min(64, file length) bytes; bytes 32..63 overlap the first block). |
| `PlaBlockMode` | `Raw` (0), `ZeroMask` (1, flag 0x100), `Lz` (2, flag 0x200): an LZ token stream decoded against the global window. |
| `PlaBlock` | One indexed block: `Offset`, `Tag` (`BL` or `DY`), `Id` and `Kind` (raw), `Version` (0 or 1), `Flags` (raw), `SizeClass` (`Flags & 0xF`), `Mode`, `Guid` (present when flag 0x20 is set), `PackedSize`, `UnpackedSize`, `Extra` (present when flag 0x400 is set; raw), `PayloadOffset`, `HeaderLength`, `End`. |
| `ObjectDirectory` | A run of directory records inside a raw block: `Block`, `RecordOffset` (payload offset of the first record), `Phase` (`RecordOffset % 30`), `Records`, `AbuttingPairs`, `OverlappingPairs`, `DuplicatePositions`. The run is an estimate and can be shorter than the real directory: it stops after more than 8 consecutive records without an abutting neighbour. |
| `ObjectDirectoryRecord` | The 30-byte record `[u16 class][u64 pos][u32 count][GUID16]` as `ClassNumber`, `Position`, `Count`, `ObjectId`. `PlaStream.ReadExtent` interprets the extent as bytes in the decoded global stream. The framed FDEA tables used by `ReadElements` store extents in 16-byte units in the decoded pool. |
| `PlaStream.Length` | Length of the decoded stream in bytes, including the bytes beyond the unreliable boundary. |
| `PlaStream.UnreliableFrom` | The offset of the first indexed block that failed to decode, or null when every indexed block decoded. |
| `PlaStream.SkippedBlocks` | The indexed blocks that failed to decode to their declared size (false entries of the scan), in file order. |
| `PlaStream.ReadExtent(long position, long count)` | The bytes of one extent. Throws `ArgumentOutOfRangeException` for negative or out-of-bounds extents and `InvalidDataException` for extents that reach the unreliable range. |
| `PlaStream.ReadExtent(ObjectDirectoryRecord record)` | The bytes addressed by a directory record. Throws `InvalidDataException` if the extent exceeds the stream or reaches the unreliable range. |

The embedded library catalog reads Windows GSM version 46 from verified archive records. It recognizes raw, zero-mask
and supported standalone QuickLZ payloads. For a verified QuickLZ asset pool, it reassembles the pool before reading
its logical blocks. This includes asset headers and payloads that cross frame boundaries. Pool decoding respects
`MaxDecodedPoolBytes`.
Readable assets with other media types are excluded. The GSM bytes belong to the archived revision, which may have a
different GUID from the revision saved in an element's library reference. Library files linked outside the archive
and script evaluation require separate readers.

## Model elements

| Member | Use |
|---|---|
| `PlaDocument.ReadObjectClasses()` | Reads the file-local class numbers and stored class GUIDs from the version 1 `FDE9` block. The class number can differ between projects. |
| `PlaDocument.HasObjectClassTable` | Reports presence of an indexed object class table before reading or validating its contents. |
| `PlaObjectClass` | `Number`, `Guid`, and `Name`. Names are Archicad API labels for known GUIDs. The table stores GUIDs, and a class with an unrecognized GUID has a null name. |
| `PlaDocument.ReadElements()` | Reads recognized elements from framed `FDEA` directories and verifies the class and GUID of pool records used for parameters. Returns a read-only snapshot with one record per GUID. An unusable class table, conflicting types, a truncated element or grid list, or a pool decode failure raises `InvalidDataException`. |
| `PlaElement` | `Guid`, `Type`, nullable basic parameters, `BoundingBox` and the elevation properties described below. The wall values are in metres. The chord is the distance between the stored endpoints. Thickness is returned when the stored endpoint thicknesses agree. |
| `PlaDocument.ReadClassificationSystems()` | Reads the saved systems and item hierarchy as described under [Classifications](#classifications). |
| `PlaElement.Classifications` | A list of `PlaClassificationAssignment` values with `SystemGuid` and `ItemGuid`. Empty means the element has no assignment in the readable systems. Null means the classification catalog is unavailable. |
| `PlaDocument.ReadPropertyCatalog()` | Reads the saved user-defined groups and definitions described under [User-defined properties](#user-defined-properties). |
| `PlaElement.PropertyOverrides` | Saved overrides linked to the element, sorted by definition GUID. Null means the reader found no verified override record for the element. |
| `PlaDocument.ReadLayoutBook()` | Reads the [layout book](#layout-book), including layouts, master layouts and drawing entries. |
| `PlaBoundingBox` | `XMin`, `YMin`, `ZMin`, `XMax`, `YMax`, `ZMax`. These are axis-aligned limits in project coordinates, in metres, with Z relative to project zero. |
| `PlaElement.BeamNetVolume` | The net volume in cubic metres for a supported beam. A verified box can be available when the contact geometry leaves the volume unresolved. |
| `PlaBeamReferenceAxis` | The saved directed axis of a supported straight or horizontally curved beam, exposed through `PlaElement.BeamReferenceAxis`. |
| `PlaWallReferencePlan` | The directed reference line of a supported straight basic or composite wall, and the signed offsets to its outside and inside faces. Exposed through `PlaElement.WallReferencePlan`. |
| `PlaWallProfile` | The saved local cross-section of a supported complex-profile wall, exposed through `PlaElement.WallProfile`. |
| `PlaMemberProfile` | The saved local cross-section of a supported single-segment complex-profile beam or column, exposed through `PlaElement.MemberProfile`. |
| `PlaSlabGeometry`, `PlaSlabArc`, `PlaPlanPoint`, `PlaSlabPrism` | The saved slab outline and elevations, analytic outline area, optional face areas and volume, and evaluated vertical slab parts when their geometry is available. Exposed through `PlaElement.SlabGeometry`. |
| `PlaWallExtrusion`, `PlaWallExtrusionSection` | The verified solid of a supported straight wall, with the sweep endpoints, section origin elevation, connected section contours and net volume. Available through `PlaElement.WallExtrusion` in .NET. |
| `PlaColumnExtrusion`, `PlaColumnExtrusionSection`, `PlaColumnEndPlane` | A supported vertical column solid with its axis, section contours, cap planes, tight bounds and net volume. Available through `PlaElement.ColumnExtrusion` in .NET. |
| `PlaColumnCutMesh`, `PlaColumnMeshPoint` | The triangulated boundary, tight bounds and net volume of a supported column cut by beams. Available through `PlaElement.ColumnCutMesh` in .NET. |
| `PlaOpeningProfile` | An Opening's base profile shape, width and height in metres, exposed through `PlaElement.OpeningProfile`. |
| `PlaDoorDimensions` | A Door's saved nominal width and height in metres and optional sill height relative to its saved anchor, exposed through `PlaElement.DoorDimensions`. |
| `PlaDoorPlacement` | The position on a supported straight host wall, the distance from the directed start of its reference line, and optional nominal sill elevations to project zero and to the wall's home story. Exposed through `PlaElement.DoorPlacement`. |
| `PlaDoorWallHoleDimensions` | The saved GDL wall-hole width and height in metres, exposed through `PlaElement.DoorWallHoleDimensions`. |
| `PlaLibraryPartReference` | A Door's saved main and revision GUIDs when the element and library descriptor confirm their link, exposed through `PlaElement.LibraryPartReference`. |
| `PlaOpeningGeometry`, `PlaOpeningCut` | The project XY profile of a supported horizontal Opening, its optional per-slab cut intervals and cut volume through `PlaElement.OpeningGeometry`. |
| `PlaDocument.ReadStories()` | Lists saved project stories in ascending index order, including those without elements. Returns an empty list when the class table is usable and no supported story table is present. Invalid class tables, invalid recognized rows and conflicting names raise `InvalidDataException`. |
| `PlaStory` | The signed project-relative `Index`, saved `Name` and `ElevationToProjectZero` in metres. A saved empty name is preserved. The elevation is null if the stored elevation is nonfinite or multiple tables disagree. |
| `PlaDocument.ReadLayers()` | Lists identifiable layer attributes in ascending layer index order, including layers without elements. Conflicting GUIDs or index records raise `InvalidDataException`. |
| `PlaLayer` | The stored attribute `Guid`, positive project-local `Index`, saved `Name` and stored `IntersectionGroup`. The name can be null for the built-in layer's localization token. The group can be null when the record ends before the field. |
| `PlaDocument.ReadMaterialCatalog()` | Reads the saved building materials and composite definitions in ascending project-local index order, including unused attributes. Conflicting indices and GUIDs raise `InvalidDataException`. |
| `PlaMaterialCatalog`, `PlaBuildingMaterial` | The catalog's materials expose their GUID, index, name, user-defined ID and normalized connection priority. The priority is null for unsupported encodings. |
| `PlaComposite`, `PlaCompositeSkin` | A composite exposes its GUID, index, name, total thickness, supported element types and ordered skins. Each skin identifies the referenced building material by index and gives its thickness and core and finish flags. |
| `PlaDocument.ReadProfileCatalog()`, `PlaProfileAttribute` | Lists complex profile definitions in ascending project-local index order, including unused attributes. Conflicting indices and GUIDs raise `InvalidDataException`. |

Recognized model types are Beam, Column, CurtainWall, Door, Lamp, Mesh, Morph, Object, Opening, Railing, Roof, Slab,
Stair, Wall and Zone. An Object must appear in the element GUID list of its owning model database. Grid GUIDs are
excluded using the saved grid list. PlaIO uses the declared record lengths to locate both lists. Older supported
Object records also expose Element ID, layer index and home story index. If the membership records use an
unsupported layout, Objects are omitted.
Unknown classes remain visible through `ReadObjectClasses()`.

For supported basic walls and slabs, `BuildingMaterialIndex` stores the material attribute index. For supported composite
walls and slabs, `CompositeIndex` stores the index of the composite definition. Join these indices to the corresponding
entries from `ReadMaterialCatalog()`. A composite wall's skins are ordered from outside to inside and a slab's from
top to bottom. Thicknesses are in metres. Profile components and geometry altered by joins have separate representations.

Element ID decoding covers all the recognized types in the supported record layouts. For zones, the reader follows the
saved relationship to the zone stamp in the same pool and reads the ID from the stamp. A missing, incomplete, ambiguous,
or unsupported parameter is null.

The effective layer is given as a project-local attribute index and its saved name. Doors and openings take the effective
layer from their host. Openings also take the home story from their host. The home story number is signed and is relative
to the project's story zero. `HomeStoryName` preserves the saved name, including an empty name when Archicad displays a
localized default. A built-in layer stored as a localization token has a layer index and a null name.

`TopLinkedStoryIndex` is the signed project-relative index of the story to which a wall or column top is linked.
It is resolved from the stored story partition relationship. An unlinked element or an unresolved partition has a null index.

For Doors, `HostGuid` identifies the wall when both saved records confirm the relationship.
`DoorDimensions` gives the saved width and height independently of host resolution. `SillHeightFromAnchor`
uses the Door's saved vertical anchor and is null if the placement frame is unsupported.
`DoorPlacement` resolves the point on the directed reference line from the saved distance along that line.
It combines the wall's saved base offset and the Door's sill offset for the host-story elevation, then
adds the wall's story elevation for the project-zero value. The placement applies to supported vertical
doors on straight walls. The nominal sill describes the placement anchor. Evaluated door and cut geometry
has its own limits.
`DoorWallHoleDimensions` reads the instance's saved `ac_wallhole_width` and `ac_wallhole_height`. It requires
reciprocal links from the Door to the library descriptor and from the descriptor to its parameter table.
Several Door instances may share a table. The evaluated aperture can extend beyond these dimensions
when the library part adds cut geometry.

For Openings, `HostGuid` identifies the associated wall or slab. `OpeningCutElementGuids` lists the host and any
additional cut walls or slabs in GUID order. Both fields require reciprocal references in the same decoded pool.
An unresolved host or an incomplete cut list gives null for the respective field. `OpeningProfile` gives the shape
and dimensions measured in the extrusion plane. For custom profiles, width and height are the profile's planar dimensions.

`BottomElevationToHomeStory` and `TopElevationToHomeStory` are measured in metres above the home story.
`BottomElevationToProjectZero` and `TopElevationToProjectZero` add the home story's elevation from the saved story table.
For supported straight horizontal beams, the reader projects the saved cross-section, including the placed
components and circular edges of a complex profile. It can report both elevations when the verified beam,
column and slab contacts leave a complete transverse section outside the combined cuts. Opposite collinear
beam ends meet at their common section. A transverse junction limits the affected part of the axis to the
peer section's projected strip. An unresolved plan extent can leave the 3D box null while both elevations
are available. Overlapping collinear beams and unverified cuts can leave the elevations null.
For a supported inclined straight beam, the saved normals and reference axis determine the two end faces.
An inclined butt joint can make the schedule elevations depend on the adjoining beam. In those cases the
reader can report verified 3D bounds and leave the schedule elevations null.
Slab elevations use the saved horizontal planes derived from the reference plane and thickness. Zone elevations use
the saved base offset, floor adjustment and height.
Supported center-anchored vertical openings that cut one wall use their saved anchor elevation and profile height.
Openings with a supported horizontal base frame can derive elevations across the slabs they cut. This covers
the center anchor and the verified downward through-opening frame anchored at the top center of its profile.
The reader uses each slab's saved planes and vertical edge layout and applies the Opening's through or one-sided depth limit.
Additional cut slabs contribute only if the projected profile is inside their saved outline. A cut target whose
plan bounds are disjoint from the Opening is skipped.
For two Openings with the same associated slab, a limited-depth Opening can remove a vertical interval when its
profile fully covers the other Opening's plan bounds. If limited-depth overlap remains ambiguous, the elevations
are null.

`OpeningGeometry` gives the saved profile in project XY. Rectangular and supported custom polygonal profiles have
straight `Rings` with implicit closure, ordered as exterior then holes. For circular and elliptical profiles, use
`Origin` as the center and `OpeningProfile.Width` and `Height` as the diameters along the project axes.
`Cuts` is a list of slab GUIDs and project-zero elevation intervals. Each interval cuts the whole reported profile.
The cut volume remains unavailable for the top-center circular frame, whose native subdivision can vary with diameter.
Disjoint intervals are kept separate. `CutVolume` sums their depths and multiplies by the section area in square
metres. For the supported circle layout, the volume uses the area of a circumscribed 36-gon, which matches the
measured Archicad quantity. Elliptical cut volume, partial plan clipping, ambiguous overlap and effects of other
solid operations remain unsupported. A known placement may still be returned when the cut geometry is unavailable.

For supported basic, composite and profiled walls, PlaIO resolves linked tops from the saved story elevation and offset.
It checks the reference planes against the world-space envelope when present. At verified junctions with walls,
beams and slabs it checks material priorities, vertical overlap and whether the joined walls leave a section that
still reaches both end planes. This includes supported collinear wall overlaps with reciprocal junctions and saved
plan contours. A supported horizontal rectangular beam or a slab can move a basic wall's end plane when it covers
the wall footprint. For non-rectangular slabs with straight-edged contours, the reader verifies that the footprint lies
inside the filled polygon and clear of its boundaries and holes.
A supported composite slab meeting a basic wall's end uses the boundary of its contiguous cutting skins. PlaIO
reconstructs the skin elevations from the saved top plane and thicknesses and checks each material's priority.
It bounds changes in plan at verified wall junctions and checks whether the other slab contacts can alter the
cutting band at that footprint. At an oblique junction with a verified straight neighboring wall, it projects
the intersection of the two wall strips onto the target axis. Collinear butt joints can have different offsets
from their shared reference line. Compatible horizontal rectangular beams can extend the cut when their
vertical ranges overlap the slab's cutting band. PlaIO checks the combined beam footprints between successive
beam faces, including verified equal-priority right-angle beam joins where the wall stays clear of the outer
corner. The cut extends only through the bands that cover the entire joined wall footprint.
For a beam connected to the cutting slab, PlaIO can verify clearance after beam-to-beam joins. It checks
reciprocal joins and projects the intersection of horizontal rectangular beam strips onto the beam axis.
Verified collinear butt joints also preserve clearance. Slab contacts are checked in the part of each skin
inside the wall's elevation range, including contacts with supported circular columns below the wall.
For a supported straight composite wall and a vertical-edged composite slab, a continuous cutting band can
move an end plane if its priority reaches that of every wall skin. The reader verifies both cutting bands
and the remaining contacts when separate slabs trim opposite ends. Reciprocal labels on the slabs preserve
the cutting band. The schedule elevations are reported relative to both the home story and project zero.
Several vertical-edged slabs can jointly cover a wall end. PlaIO checks the union of their filled outlines,
including holes, between successive cutting planes. For automatic junctions, this calculation supports basic
walls and straight composite walls with verified transverse skin placement. At wall junctions it checks that
the cuts cover any extension of the joined wall and that a section of the authored wall survives at each
reported plane. Multiple junction records connecting the same wall and slab contribute one cutting body
after the reader verifies the reciprocal links. The reader also evaluates supported reciprocal slab subtraction
links, including vertical extrusion in either direction. When one slab subtracts from another, both slabs must
independently cut the same wall with the same operation and direction. The reader checks that these dependencies
are acyclic and checks the layer intersection groups independently of the subtraction. A wall used as an
operator retains its own geometry after the reader verifies the operation and its reciprocal target link.
A verified subtractive operation keeps a neighboring wall within its reference body. If a verified wall contact
may remove material from a slab, PlaIO evaluates the cuts with the full saved slab body and with that uncertain
contribution omitted. It reports elevations only if both reproduce the same limits and the full-cut case leaves a
verified patch at each plane.
A verified slab opening that misses the cutting band in plan or height can preserve coverage. For a supported
centered circular opening, the clearance check uses the circumscribed 36-gon. If a reciprocal opening may reduce
a slab cutting band, the reader compares the wall limits with that full band and with the band omitted. It can
report an end when both limits agree there and the remaining material is verified. A verified separate opening
in the wall can preserve the reported elevations
when it lies between those planes. Additional operations that can alter the end sections may leave the schedule
elevations unavailable.

For a basic wall, a single slab can establish a complete cut through the cap beyond its cutting skins.
Two or more verified composite slabs can also establish a complete cut when their single cutting skins share
both elevations and their combined outlines cover the joined wall footprint all the way across.
PlaIO can combine that verified cap with a separate cut at the opposite end.
In that case, a horizontal rectangular beam can replace part of the slab's cutting layer when its saved
connections and material priority confirm that it also cuts the wall. The reader checks the beam's joints
and other structural contacts over the wall footprint. It requires a surviving section of the wall outside
the combined beam contacts at the resulting elevations.

For a supported straight wall with a rectangular door opening, PlaIO checks the reciprocal host link, the saved
library part and revision, and the placement along the wall. For supported swing doors and swing doors with a transom,
the saved GDL parameters must confirm the rectangular passage and exclude additional cuts. It reports the wall elevations when a section outside
the opening and the other cutters reaches both end planes. At a junction with a straight-edged, vertical-sided
slab, the reader projects the slab contour along the wall with a `1e-7` metre clearance margin. If that
projection leaves no gap, it checks the saved slab outlines in plan, including holes with verified vertical
edges. It can confirm each plane using a different square of wall material with a `2e-6` metre side.
Supported slabs, horizontal beams, basic neighboring walls and rectangular door apertures use their verified
height ranges to determine which planes they can reach. A contact without verified height is included at both
planes. The reader reports an independently verified wall elevation even when the opposite plane is unresolved.
This includes retained original planes and evaluated slab cuts where the expanded joined wall and the verified
remaining material in the authored section reach the same elevation. A wall solid requires both planes
and a verified section. Door parts with unverified cut geometry leave the wall schedule elevations unavailable.

At a reciprocal end-to-end junction, the saved junction point can confirm a common longitudinal end plane for
parallel rectangular walls whose transverse coordinates differ by up to `1e-6` metres. For an overlapping basic
neighbor with a corroborated saved height range, PlaIO can retain a wall elevation outside that range while
treating the neighbor's plan coverage as unresolved.

For a supported sliding pocket door revision, PlaIO checks the saved aperture dimensions, centered leaf and frame
settings, and the three skins of the symmetric composite host. On an axis-aligned vertical wall it looks for
material outside the through opening and both possible pocket envelopes bounded by the leaf width and frame depth.
This can establish wall schedule elevations even when the placement direction of the recess is unresolved. It leaves
the cut solid unavailable in that case.

For supported vertical columns, PlaIO projects a rectangular section onto its saved end planes, including angled
cuts on an unrotated section. A supported single-material custom profile can supply bounds when its end planes
are horizontal. Automatic junctions with walls, beams and slabs are checked against the saved materials and
composite skins. An isolated horizontal beam with a rectangular section or a supported rectangular slab can change
a column endpoint when it covers the full end face. PlaIO projects the beam section with its saved rotation about the axis.
For supported horizontal rectangular beams parallel to the project axes, PlaIO combines their footprints between changes
in elevation. A beam collision within the group is supported when the two parallel axes are distinct and the axial ends
and vertical ranges agree. Rectangular beams in a perpendicular junction are supported where the junction stays clear
of the element footprint. For a verified T-junction with both beams in the cutter group over the same vertical band,
PlaIO can use their combined footprint where the through beam covers the joining end. If only the stronger branch
can cut a column in that junction, its cutter footprint reaches the far face of the through beam. A stronger
through beam retains its footprint at a junction with a weaker branch.

Unresolved geometry and operations leave the affected elevation fields null. Beam joins that may change coverage
at the element, coupled cutters outside the supported group and wall junctions without a verified remaining section
still require additional geometry.
A missing story elevation
leaves the project-zero fields null.

### 3D bounding boxes

`PlaElement.BoundingBox` returns a box for supported wall, slab, zone, column and beam records. A wall may carry a saved
3D box. For a supported straight wall without one, PlaIO can combine a reciprocal rectangular plan contour with
verified end planes when its only junction contacts are with other walls. A verified inline rectangle can supply the plan
extents of a straight wall with an empty plan cache. Supported placed wall profiles use the full cross-section,
including when an inline slice confirms its orientation. The element box can preserve the authored height at a cut.
For wall elevations, a verified inline rectangle can also establish the wall's strip in a reciprocal junction.
The reader checks associated dimension and label records before using that proof.
For slabs and zones, PlaIO uses the
saved contour extents and horizontal planes, including slab contours with arcs. For supported isolated vertical
columns, PlaIO projects the saved section into plan and uses the end planes for Z. Joined rectangular columns
can also retain their box when the saved relationships, priorities and cutter bands leave the end faces intact.
An evaluated rectangular `ColumnExtrusion` can provide a box after a verified full-width end cut.
For a supported single-segment complex-profile column, the element box uses the placed profile and saved
cap-axis elevations. This also applies to verified cuts, including columns with oblique end planes.
For a centered profile with a crosshair symbol the plan bounds include the four ends of its saved extension,
rotated with the element. The extrusion or cut mesh supplies the bounds of the evaluated physical solid.

`ColumnExtrusion` describes a supported vertical single-segment column with a rectangular or straight-edged
single-fill profile section. Its contours use project X and Y directions with the column axis as the origin.
For a circular column with horizontal faces, `CircularRadius` supplies the analytic radius and `Sections` is empty.
The analytic volume and bounding box of the circular solid use that radius. `ScheduledCapArea`,
`ScheduledLateralArea` and `ScheduledVolume` use the Archicad listing polygon with equal subdivisions of
each quadrant and a maximum sagitta of one millimetre.
Each end plane gives an elevation relative to project zero at that axis and two slopes for other points on the
plane. Supported full-width end cuts can replace the saved cap. When verified beam cuts remove the entire
section at intermediate heights, `Spans` lists the remaining project-zero intervals in ascending order.
For a verified T-junction with matching vertical section limits, the reader can use the combined
sweep of two rectangular cutters with equal priority when the through beam still covers the join. It also
supports a rectangular branch ending against the near face of a weaker through beam with a straight-edged
concave section. Repeated contacts must agree on that face. Coincident horizontal cutter faces within
`1e-8` metres use the same mesh boundary. Interior beam collisions and
symmetric miters preserve the cutter where the saved contact and section geometry establish clearance or
priority. Possible wall wrapping can leave the geometry unavailable. A composite wall with verified skins all
in the core or all outside the core cannot wrap the column. Its contact can preserve the column solid when
the material priority and the other contacts permit it.
A rectangular column with horizontal caps can retain its solid when its rotated footprint misses a joined beam.
PlaIO tests the footprint inside the beam's section strip against the axial reach of all verified beam joins.
For a crossing recorded without a point, the reader checks that the overlap of the two section strips
stays strictly between the cutter beam's saved caps.
A reciprocal annotation link can coexist with the column geometry. PlaIO verifies the back-reference of an associated
column label or angular dimension before evaluating the column. A label's wall and column references are verified
independently. PlaIO also verifies angular dimension references on
connected beams. The extrusion box encloses the solid tightly.
An Archicad element box can preserve authored extents or be wider in plan than the evaluated solid.

`ColumnCutMesh` supplies the closed boundary of a supported rectangular or profiled vertical column after a partial beam cut.
With horizontal caps and horizontal rectangular cutters, it uses the saved straight-edged section, including
concave outlines with a single boundary. Two verified cutters of equal priority with matching rectangular sections can
share a miter plane through their common endpoint. PlaIO clips both masks at that plane. A weaker peer at the same
endpoint is accepted when it repeats the verified cap geometry and its full section stays inside the matching envelope.
At a rectangular column, a weaker beam sharing the same saved axis
and caps can share the rectangular cutter's mask when its entire section fits inside the stronger beam and
the verified contacts leave the cut unchanged. PlaIO also exposes the element's box when the mesh retains all
six limits of the saved rectangular column. For oblique caps and inclined cutters, it supports axis-aligned
rectangular column profiles. The reader verifies the cap planes, cutter profiles and structural contacts.
Cuts can remove only part of the section or leave internal cavities.
The triangle vertices are relative to the mesh's project-coordinate origin. `BoundingBox` on this mesh
encloses the evaluated solid tightly.

`WallExtrusion` describes the solid of a verified straight vertical wall. A wall with a constant cross-section
sweeps a rectangle or a straight-edged complex section along its reference line. The complex section combines
the saved material regions, retaining holes and disconnected pieces. For an axis-aligned rectangular wall with
supported simple Door openings, the reader subtracts the rectangles from the wall elevation and sweeps the remaining section
across the wall thickness. The Door links and instance parameters must confirm the openings, with all four
oversize parameters set to zero. This path can also recover the wall box and elevations when the plan cache
is absent. The sweep endpoints describe the solid's direction in both cases. `WallReferencePlan` gives the
authored reference line. Wall junctions, other cut types, slants and profiles with curved edges remain outside
this solid reconstruction. A verified zone-area calculation can reference the wall without affecting mesh eligibility.

For supported horizontal beams, PlaIO sweeps the saved section along the beam axis, including the stored section
rotation. It resolves validated equal-priority butt joints, perpendicular T-junctions and axis-aligned L-junctions
with matching vertical profiles. At a supported T-junction the branch ends at the through beam's near face.
At a supported L-junction an outer corner of each beam reaches the far face of its peer. A beam that takes
precedence over a reciprocally linked slab, wall or supported rectangular column keeps its box at that contact.
Unjoined horizontal complex-profile beams can also supply a box from the saved placed section.
For a straight beam with inclined or oblique end planes, PlaIO intersects the section's sweep with the two
saved planes and projects the resulting end faces. An inclined beam can keep that box when material priorities
preserve the solid or the checked cutters miss both end faces. This includes supported walls whose placed profile
is clear of both faces and collinear vertical butt planes unaffected by a distant transverse junction.

For horizontal circular beams with supported unrolled complex profiles, PlaIO sweeps the section about the saved
circle. At verified end connections with matching placed profiles, it intersects equal transverse offsets of the
neighboring circular or straight axes. A slab cut within the beam's vertical span preserves the elevations when
the end connections leave a complete section. The 3D box also requires a surviving point at each plan extremum.

Saved wall boxes are read independently of schedule elevations and may differ after junction cleanup. For the boxes
reconstructed from profiles, contours or planes, a missing story elevation or unsupported geometry leaves the box
null. Unresolved wrapping, column cuts and beam end changes may require additional geometry.
General solid element operations are still outside this reconstruction.

For a horizontal `BeamNetVolume`, the reader multiplies the rectangular section area by the evaluated length. A supported square
butt cut shortens that length. At a supported symmetric miter, the added and removed wedges have equal volume.
For supported inclined beams with parallel cut planes, the reader uses the rectangular or single-component
straight-edged profile area and the 3D axis length when the known contacts preserve the solid.
A horizontal profiled beam has zero net volume when the intact portions of reciprocally linked rectangular beams
with higher material priority cover its complete section along the span. The reader includes supported end
extensions of the profile and excludes the portions of each covering beam that its verified contacts can remove.
This zero is available even when the covered beam has no evaluated box or elevations.
Net volumes for curved beams remain unresolved. Their saved circular axes and profile arcs suffice for supported
extents, while their volumes also depend on how the model facets the curves and resolves the end connections.

`BeamReferenceAxis` gives the saved start and end in project XY, their project-zero elevations, and the signed
plan sweep. A positive sweep turns counterclockwise from start to end. `Length` follows that straight line or arc
before junction cleanup. `InclinationRadians` is positive when the end is higher than the start. The axis remains
available when the evaluated beam geometry is unresolved. A circular axis requires agreement between the saved
sweep and both end-plane normals. A missing story or unresolved axis record leaves the field null.

`WallReferencePlan` describes the authored placement before junction cleanup. Its start and end are in project XY,
and both face offsets are positive toward the right of the directed reference line. PlaIO reports the plan for
validated inline rectangles of constant width, including flipped and core-referenced walls. The property is null
when the reference settings and saved rectangle disagree, or when no supported rectangle is present.

### Classifications

`ReadClassificationSystems()` returns the supported saved systems sorted by GUID. A system carries its saved name,
description, source, version label and optional publication date. Its flat `Items` list gives the saved item GUID,
displayed ID, name and description. Items appear in depth-first order with siblings in saved order. A null
`ParentGuid` identifies a top-level item.

The reader follows the saved links between each system, its internal root and its visible items. Assignments
come from item records and are limited to elements recognized by `ReadElements()`. An element can have one saved
assignment per system. The assignment list follows system GUID order. A file without a supported catalog returns
an empty system list. Truncated supported records, broken trees and conflicting assignments raise
`InvalidDataException` when reading the affected data.

### User-defined properties

`ReadPropertyCatalog()` reads user-defined groups and definitions directly from the file. Group and definition
lists are sorted by GUID and include unused records. Definitions expose the group GUID, name, description,
data type, classification availability and saved enumeration choices. A definition applies to an element when
one of the element's `Classifications` item GUIDs occurs in its `AvailableClassificationItemGuids`.

Recognized scalar types are integer, number, length, area, volume, string and boolean. Enumerations expose
their choices in display order. Supported literal defaults return `normal` and an `int`, `double`, `string`
or single choice `Guid` value according to the data type. Length, area and volume use metres, square metres
and cubic metres. The statuses `userUndefined` and `expression` carry no literal value. An unsupported default
encoding yields null.

`PropertyOverrides` exposes explicitly saved element-level values with the definition GUID. The reader verifies
both the definition's link to the override record and the override's link back to the definition. An override
with a recognized link and an unsupported value encoding has a null value. Values inherited from the definition
are available through `ReadPropertyCatalog()`. Built-in property definitions, formula evaluation and Boolean and
multiple-choice literal values need additional support.

### Layout book

`ReadLayoutBook()` returns the book root GUID and a flat list of its items in depth-first order. Each item
supplies the parent GUID and an Archicad navigator type label. The supported item types are `LayoutItem`,
`DrawingItem`, `SubsetItem`, `MasterFolderItem` and `MasterLayoutItem`. Top-level items refer to the book GUID.
The master folder is represented by its GUID and type.

Subsets, layouts, master layouts and drawings expose their saved name and navigator identifier. An identifier
can be empty. Layouts and master layouts expose the source GUID from the navigator link and the verified layout
database GUID. A layout exposes the navigator GUID of its assigned master and inherits that master's paper size.
Paper sizes use metres.

A placed drawing exposes its saved source path and the source GUID from a verified internal view link or supported
external source record. An empty source path is preserved. A missing or ambiguous internal view link yields a null
source GUID. Drawing content remains pending.

`PlaLayoutBookItem.Revision` exposes the revision status of layouts and master layouts with recognized revision
records. It includes the issued history in saved order, oldest first. Each entry has the revision GUID and ID
and the transmittal's GUID, ID, name, UTC issue time and issuer. A saved open revision link exposes its transmittal
with a null issue time and issuer. If there is no open revision, the last issued ID supplies both current ID
fields. Two empty IDs represent a layout with a readable state and no revisions. A null revision summary means
the record is unavailable or its format is unsupported.

For an open revision, `CurrentRevisionId` comes from the transmittal ID when `OverridesRevisionIds` is enabled.
Otherwise the saved layout numbering applies, including a retained transmittal override and the history of issued
IDs. Automatic numbering supports decimal suffixes and uppercase ASCII letter sequences. An unsupported sequence
yields a null current ID. `CurrentFinalRevisionId` remains null while the revision is open because the
application supplies the localized display suffix. Open revisions without a verified transmittal link and
changes associated with each revision need additional support.

The method returns null when no supported book is found. A recognized book with conflicting records or broken
links raises `InvalidDataException`.

### Complex profiles

`WallProfile` gives the project-local profile attribute index and the material components saved for a placed wall.
`MemberProfile` gives the corresponding values for a supported single-segment beam or column. Saved profile indices
can refer to attributes unavailable in the project catalog.
`ReadProfileCatalog()` resolves either index to its saved attribute when present and exposes the undeformed section.
The profile definition includes its enabled element types, width, height and core flag when those fields are supported.
The width and height include circular edge extrema. `WidthStretch` and `HeightStretch` expose the saved enabled flags,
local stretch intervals and minimum sizes in metres. An enabled interval gives the part of the section that can contract.
The reader computes the minimum size by removing the interval's overlap with the section extent. A disabled axis has
null interval bounds and keeps its full size. A missing or unsupported stretch record leaves the whole axis value null.

`Modifiers` lists the saved names and GUIDs of offset modifiers whose profile, instance and definition links agree.
An empty list represents a verified profile without named modifiers. A null list means the links or layout are unavailable.
Each modifier's `DefaultValue` is its signed value in metres on the undeformed catalog section. The reader resolves
its saved dimensional constraints and projects the distance between their anchors onto the stored direction.
The value is null when the anchors are unavailable or the modifier's dimensions disagree. Placement-specific
overrides remain unavailable.
Positive component `BuildingMaterialIndex` values resolve through `ReadMaterialCatalog()`. Zero denotes an unassigned
catalog fill. Components follow saved drawing
order. `Function` supports `Core` and `Other` and is null for unrecognized values.

The first ring is the exterior and any following rings are holes. Vertices are in saved order with the closing
vertex omitted. `U` and `Z` are the saved coordinates in the local profile plane, in metres. For a wall they
represent the horizontal and vertical coordinates of the section. A column's section is transverse to the column axis.
`Arcs` identifies circular edges by zero-based ring and starting vertex. Each extends to the next vertex, wrapping
at the end. Positive angles turn counterclockwise. `CrossSectionArea` integrates the saved exterior and subtracts
the holes, including the circular edges.

PlaIO requires reciprocal links from the wall or through the beam or column segment, and a valid section for every component. Each component
may include up to 4,096 stored vertices. The saved fills may overlap. The profile describes the local cross-section
before inter-component clipping, element junctions and other solid operations.

### Slab outlines and solids

`PlaElement.SlabGeometry` exposes a supported slab outline in project coordinates. The first
ring is the exterior in counterclockwise order. Subsequent rings are holes in clockwise order. The final edge
joins the last point back to the first. The reader validates simple rings and rejects holes that touch, cross,
nest or lie outside the exterior. The outline limit is 4,096 stored points.

`Arcs` supplies the circular edges. Each arc identifies its zero-based ring and starting vertex. Its end is the
next vertex, including the first vertex for a closing arc. `SweepRadians` is positive counterclockwise.
Edges with no arc entry are straight. `Arcs` is null when all edges are straight.
`FloorPlanPerimeter` is the total length of the saved exterior and hole boundaries. `FloorPlanHolesPerimeter` gives
the length of the hole boundaries alone, or zero when there are no holes. Both lengths use metres and include exact
arc lengths. Linked Opening cuts are tracked separately.

`Bottom` and `Top` are saved slab elevations relative to project zero in metres. The reader includes the saved
offset from the reference to the upper plane in supported composite slab layouts. `OutlineArea` is the area inside
the saved exterior minus holes in the outline, in square metres. For circular edges, it integrates the exact saved
arcs. Archicad's scheduled face areas can differ from this analytic area. `TopAreaWithOpenings` and `BottomAreaWithOpenings`
are gross horizontal face areas with both saved holes and verified linked Opening cuts subtracted. A one-sided
opening reduces the face it reaches. Unverified opening links, nonvertical edges or curved outlines yield null face areas.

`Volume` in cubic metres is available when the slab has vertical edges and the reader can verify all contacts.
Supported linked openings reduce the volume when other contacts preserve the slab prism. For slabs without
linked openings, the reader can also deduct verified cuts by vertical rectangular columns and straight
walls with a saved polygonal profile. It intersects a rotated column footprint with the slab rings at skins
with equal or lower material priority. For a profiled wall it checks the placement against the saved envelope
and integrates the profile overlap through strictly lower-priority slab skins. Other joins that can modify the
cutting element, unresolved overlaps between cuts or curved outlines withhold the volume.

`IsUncutVerticalPrism` confirms that the saved outline and elevations describe the whole solid.
`VerticalPrisms` supplies the evaluated solid as one or more connected parts from `Bottom` to `Top`.
Each part has one counterclockwise exterior and optional clockwise holes. Vertical cuts by supported rectangular
columns can add holes, change the exterior or split a slab into separate parts. Each cut must span the full slab,
cut every skin and produce a closed polygonal boundary. Verified separate Opening elements with straight-edged
profiles can also cut the whole extrusion when the other contacts preserve the slab. `VerticalPrisms` is null for
cuts into individual skins, profiled wall cuts, partial-depth or curved Opening cuts, and unresolved contacts.
Use `Rings` for the saved outline. Both prism properties are available in .NET. The JSON fields describe the
saved outline and its quantities.

## Compressed pools

Pool framing was verified using saved container bytes. A 16-byte marker is followed by the decoded total and frame count
as little-endian u64 values. Each frame has a u32 size followed by a standalone QuickLZ level 1 frame. The decoder
implements the [public QuickLZ 1.5.0 format](https://github.com/ReSpeak/quicklz/blob/d905415a88aed6dae685bae500025ccc81de3822/Format.md);
independent decoding of saved-file frames verified the correspondence. PlaIO has no runtime dependency on QuickLZ.

| Member | Use |
|---|---|
| `PlaDocument.FindCompressedPools()` | Finds pool markers outside indexed block payloads and validates frame headers and sizes. The first call scans the file; later calls return the cached list, so the pool objects stay identical. |
| `CompressedPool.Offset` and `End` | File offsets of the pool marker and of the end of the pool. |
| `CompressedPool.TotalDecompressed` and `BlockCount` | Decoded byte count and number of frames in the pool. |
| `CompressedPool.Decode()` | Decoded frame payloads in order. Throws `InvalidDataException` on invalid framing or data (a pool past `PlaReaderOptions.MaxDecodedPoolBytes` included) and `ObjectDisposedException` after disposing the document. |

### Pool subgroups

The subgroup header uses the FDB size field widths and may carry a GUID. It does not contain the file-level extra field.
Offsets are relative to the decoded pool. The scanner retries an unmatched BL candidate at the next byte. A mode-2
payload is an LZ token stream like a mode-2 block payload: it may start with a 13-byte pool header (a u32, the 0x47
marker, the packed payload length and the unpacked size) or with the file block's 9-byte stream header, and its
back-references reach into the pool stream — the reconstruction of every preceding pool of the document and of this
pool before the record — so a read decodes the pools before the named one. Reading the pools in file order advances
one shared window, so every pool decodes once. A read that names an earlier pool rebuilds the window. A skipped
pool or an undecodable mode-2 subgroup resets the history. A reference into missing history returns empty data
with `Partial` set. Subgroups can continue to reference bytes decoded after the gap. A back-reference distance is a
u16, so the decoder can discard older history between records.

| Member | Use |
|---|---|
| `CompressedPool.ReadSubgroups()` | Decodes the pool and returns the recognised BL-tagged records in pool order, with mode-2 payloads decoded against the pool stream. |
| `PoolSubgroup.Offset` | Start of the subgroup header in the decoded pool. |
| `PoolSubgroup.Kind`, `Version`, `Flags` and `ObjectId` | Raw header metadata and the optional GUID. |
| `PoolSubgroup.Mode` and `Data` | The payload bytes: raw as they are, zero-mask and mode-2 as their decoded output. A mode-2 payload that does not decode is reported with empty data and `Partial`. |
| `PoolSubgroup.Partial` | True for a mode-2 payload that does not decode (or produces less than declared) or an incomplete zero-mask literal. A zero-mask stream ending at a group boundary can leave valid zero padding. |

## BIMx file (`.bimx`)

| Member | Use |
|---|---|
| `BimxDocument.Open(string path)`, `BimxDocument.Open(Stream stream, bool leaveOpen = false)` | Opens a `.bimx` file and measures its cover image without reading it. Same exceptions as `PlaDocument.Open`; a cover cut short also throws `InvalidDataException`. |
| `BimxDocument.Open(string path, PlaReaderOptions? options)`, `BimxDocument.Open(Stream stream, bool leaveOpen, PlaReaderOptions? options)` | The same opens with explicit reader options. |
| `BimxDocument.Length` | Length of the file in bytes. |
| `BimxDocument.CoverOffset` | Offset of the PNG cover image: always 4, right after the signature. |
| `BimxDocument.CoverLength` | Length of the cover, from the PNG signature through the IEND chunk and its CRC. |
| `BimxDocument.ContentOffset` | `CoverOffset + CoverLength`. The content after the cover is not interpreted. |
| `BimxDocument.ReadCover()` | The PNG cover image. Throws `InvalidDataException` when the cover is larger than `PlaReaderOptions.MaxCoverBytes` (64 MiB by default). |
| `BimxDocument.Dispose()` | Releases the stream unless the document was opened with `leaveOpen`. |

## Resources and threads

`PlaDocument` and `BimxDocument` never load the whole file into memory: the block index is built by scanning through the
stream once, at `Open`, and payloads and the cover are read on request. The exception is `OpenStream`: its decoded global
stream holds the whole decoded file in memory by design, bounded by `PlaReaderOptions.MaxGlobalStreamBytes`. A document
is not thread-safe; use one per thread.
After `Dispose`, `ReadPayload`, `FindDirectories`, `OpenStream` and `ReadCover` throw `ObjectDisposedException`.

## Diagnostics

The library never writes to the console and has no diagnostic writer. Every failure is reported by an exception. The
`plaio` command line tool turns those exceptions into one-line messages on standard error and an exit code; see
[Command line and exit codes](cli.md).
