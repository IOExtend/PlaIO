# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html); versions before 1.0 may change the
API between minor releases.

## [Unreleased]

### Added

- `PlaDocument.ReadEmbeddedLibraryCatalog()` returns verified archived GSM library parts with their original
  bytes, saved names, main and revision GUIDs, and the offsets of unreadable asset blocks.
- Embedded library reading follows the decoded asset pool across QuickLZ frame boundaries, including asset
  records whose headers are stored inside compressed frames.
- Doors with verified library descriptors expose the main and revision GUIDs recorded on the placed element.
- Doors on supported straight walls expose their reference line position and nominal sill elevations in project
  and host-story coordinates in the core reader, CLI and IOScene.
- Doors expose the wall-hole width and height from verified placed GDL parameter tables.

- `PlaSceneReader.Read()` maps Archicad elements, levels, supported properties and verified slab meshes into IOScene.
  Straight-edged slabs can have saved holes, supported through cuts from rectangular columns or separate Opening
  elements, and multiple connected parts.
- `PlaElement.WallExtrusion` and the scene adapter expose supported straight vertical wall solids and their net
  volumes, including walls with straight-edged complex profiles and verified zone-area references.
- Rectangular walls with verified simple Door openings expose closed solids with the through cuts, including
  supported walls saved without a cached plan outline.
- Straight walls with an empty plan cache can expose element boxes from their verified inline contour or placed
  profile. Verified inline contours and reciprocal annotations also support elevations at structural junctions.
- Supported straight composite walls expose schedule elevations after composite slabs cut through all wall skins,
  including independently verified cuts at opposite ends. Reciprocal slab labels preserve eligible cutting bands.
- Supported walls expose schedule elevations after a combination of straight-edged slab cuts, including
  reciprocal slab subtraction links with vertical extrusion upwards or downwards.
- Wall end coverage now accounts for verified oblique wall joins, collinear butt joins with different face
  offsets, and acyclic slab subtractions where each operator cuts the wall in the same direction.
- Wall elevation checks account for reciprocal Openings located between the wall ends and verify whether
  openings in a cutting slab reach the cutting band, including supported centered circular openings.
  Standalone slab labels and typed directory label references can also preserve that band.
- Walls with verified rectangular door openings can retain their schedule elevations where an intact section
  reaches both end planes. This includes supported swing doors with and without transoms whose placed GDL parameters
  confirm the rectangular cut. At supported slab junctions the reader checks the slab contour within the wall strip.
- Wall elevation checks account for the axial reach of verified beam joints and circular column contacts
  outside the wall's height range. A verified full cap cut can be combined with a separate slab cut at the opposite end.
- For supported basic walls with slab cuts at opposite ends, verified rectangular beams can preserve slab
  coverage where they replace a cutting skin. The reader checks for a remaining wall section beyond the cut.
- `PlaElement.ColumnExtrusion` and the scene adapter expose supported vertical column solids and their net
  volumes, including rectangular sections with planar caps and straight-edged profiles split into spans by
  verified full-section beam cuts.
- `PlaElement.ColumnCutMesh` and the scene adapter expose supported partial cuts of vertical profiled columns
  by horizontal or inclined beams, including evaluated elevations and net volume.
- Column geometry accounts for verified right-angle beam T-junctions, including matching cutters and repeated
  references to the same end trim.
- Column cutters can retain their sweep through verified interior beam collisions, angled T-junctions and
  symmetric miters that clear the column.
- Columns can retain a verified solid at contacts with weaker composite walls whose skins cannot wrap them.
- Rotated rectangular columns can retain their solid when validated joined beam caps clear the actual footprint.
- Rectangular columns support partial cuts by verified coextensive beams where the stronger rectangular section contains the other cutters.
- Column cuts include the joined caps of verified equal-priority rectangular beams that meet at an angled miter.
- Columns preserve supported geometry with reciprocal labels, angular dimension anchors or verified beam crossings
  away from the cutter's caps. Beams also support verified angular dimension anchors.
- Profiled columns expose element boxes from their saved cap-axis elevations and profile outline, including
  the plan extension of a centered crosshair symbol.
- The core reader and scene adapter reconstruct circular columns with horizontal caps. They expose analytic
  and Archicad listing quantities. The mesh preserves the analytic bounds with a bounded sagitta.
- `PlaDocument.ReadStories()` and `plaio stories` expose project story indices, saved names and elevations.
- `PlaDocument.ReadLayers()` and `plaio layers` expose layer GUIDs, indices, saved names and intersection groups.
- `PlaDocument.ReadMaterialCatalog()` and `plaio materials` expose building materials, connection priorities and ordered
  composite skins. Supported walls and slabs expose the corresponding material or composite index.
- `PlaDocument.ReadProfileCatalog()` and `plaio profiles` expose complex profile definitions, enabled element types,
  section dimensions, stretch limits, named offset modifiers with their default offsets and saved material fills.
- `PlaDocument.ReadClassificationSystems()` and `plaio classifications` expose the saved classification systems and
  item hierarchy. `PlaElement.Classifications` and `plaio elements` expose each recognized element's saved assignments.
- `PlaDocument.ReadPropertyCatalog()` and `plaio properties` expose user-defined property groups, definition types,
  classification availability, enumeration choices and supported defaults.
  `PlaElement.PropertyOverrides` exposes supported overrides linked to recognized model elements.
- `PlaDocument.ReadLayoutBook()` and `plaio layouts` expose the saved layout book, layouts in navigator order,
  placed drawing entries, source GUIDs and paths, layout databases, assigned masters and paper sizes.
- Layout entries expose issued revisions in saved order, transmittal date and issuer, and verified open transmittal links.
- Open layout revisions expose saved IDs and supported automatic numbering, including transmittal overrides.
- Object membership follows the declared length of the database record, including the longer record in older archives.
  The supported older Object format also exposes its Element ID, layer index and home story index.
- Standalone QuickLZ record decoding reads consecutive frames with short or long size headers.
- `PlaElement.BoundingBox` and `plaio elements` expose project-coordinate 3D extents for supported walls, slabs,
  zones, vertical columns and horizontal beams, including supported beam junctions and column contacts.
- Supported horizontal beams expose their evaluated top and bottom elevations in both reference systems,
  including complex profiles and verified junctions that preserve a transverse section.
  Straight beams with supported inclined axes or oblique end planes can also expose 3D bounds and elevations.
  Horizontally curved beams with supported profiles expose 3D bounds and elevations at verified end connections
  and slab cuts that preserve their extrema.
  `PlaElement.BeamNetVolume` gives the volume of supported horizontal rectangular beams and inclined prisms.
  It reports zero when the verified intact portions of higher-priority rectangular beams cover the whole
  horizontal profiled beam, including supported end extensions.
- `PlaElement.BeamReferenceAxis` exposes the saved endpoints, inclination and length of straight and horizontally curved beams.
- `PlaElement.WallReferencePlan` exposes the directed reference line and face offsets for supported straight walls.
- `PlaElement.WallProfile` exposes the profile attribute index and saved material sections, including holes and circular
  edges, for supported complex-profile walls.
- `PlaElement.MemberProfile` exposes the saved material cross-sections and profile attribute index of supported
  complex-profile beams and columns.
  A verified inline rectangle also supplies extents and end elevations for supported walls without junctions or a saved 3D box.
- `PlaElement.SlabGeometry` exposes slab outlines with holes, circular edges, analytic areas and perimeters, and the volume
  of verified straight-edged extrusions.
- Slab geometry exposes both gross face areas after verified Opening cuts and deducts their volume from
  supported slab prisms.
- Slab volume accounts for verified intersections with vertical rectangular columns and the priorities
  of the affected slab skins.
- Slab volume also accounts for verified cuts by straight profiled walls, including cuts that apply to
  only some slab skins.
- `PlaElement` exposes Opening shapes, profile dimensions, associated walls and slabs, and verified cut targets.
  Supported vertical wall openings also have elevations relative to project zero and the home story.
- `PlaElement` exposes nominal Door dimensions, sill height relative to its saved anchor, and the host wall GUID when both records confirm the relationship.
- Supported slab openings expose elevations in both reference systems after through and one-sided cuts, including
  overlapping limited-depth cuts that fully cover another opening's profile.
- `PlaElement.OpeningGeometry` exposes project XY opening profiles, verified cut intervals by slab and cut volume.

- `PlaDocument.ReadObjectClasses()` resolves file-local class numbers to stored GUIDs and recognizes API type names
  by GUID. `PlaDocument.ReadElements()` and `plaio elements` expose recognized model elements, their GUIDs and basic
  parameters such as Element ID and constant-width wall thickness. Objects are selected by the saved database element
  and grid GUID lists. Element IDs are read from Raw, ZeroMask and standalone QuickLZ records. Zones get their IDs
  from the linked zone stamp. Elements also carry the effective layer and home story, with names from the saved
  layer and story tables. Slabs and zones expose bottom and top elevations relative to the home story and project zero.
  Walls and columns expose the index of the story to which their top is linked. Supported walls and vertical columns
  expose bottom and top elevations using saved story links, profiles, cut planes and material priorities. Supported
  rectangular slab junctions can change wall and column end elevations. Isolated higher-priority rectangular beams
  use their saved section and rotation to cut column ends. Supported perpendicular wall junctions preserve elevations
  when a remaining section reaches both end planes.
  Supported sets of horizontal rectangular beams can cut a column together. Verified collinear wall junctions and
  bounded contacts with beams, columns and slabs can preserve elevations when some wall section reaches both ends.
  Supported perpendicular beam joins preserve known cutter coverage beyond the junction and at verified T-junctions.
  A supported horizontal beam or a straight-edged slab outline can define a basic wall's cut end.
  Verified T-junctions can cut a column with one beam. A composite slab can cut a basic wall at the boundary
  of its cutting skins.
  Verified adjacent walls and beams can participate in composite-slab wall cuts when the joined footprint stays covered.
  Several supported rectangular beams, including verified right-angle beam joins, can extend the same composite-slab cut.
  See `docs/public-api.md` for the supported layouts.

- The `PlaIO.Scene` adapter project (namespace `PlaIO.Scene`): `PlaSceneReader.Read(path)` turns a `.pla` or `.pln`
  container into an `IOScene` `SceneDocument` — one model per file with the frozen `Key` derivation (the
  `PlaSceneOptions.ModelKey`, else the file name), model metadata, and the `container-only` diagnostic.
  The project is `IsPackable=false` and is not published; it ships on a
  `PlaIO` release tag when the reader decodes elements and geometry. See `docs/scene.md`.

- `PlaDocument.ReadPayload` decodes mode 2 (LZ) payloads. A payload may start with a 9-byte stream header with a
  0x46 or 0x47 marker and matching packed and unpacked sizes. The token stream produces the block's declared unpacked size and may reference the
  decompressed output of any preceding block of the file, so `ReadPayload` for a mode 2 block decodes the file from the
  start into one global window; the cost of the call grows with the size of the file. Preceding blocks that do not
  decode (false entries of the scan) are skipped and leave the window unchanged. A final match may omit the length byte
  (the match length is the remainder of the output). Some blocks declare an output buffer larger than the stream
  produces: the stream simply ends with the body, the produced bytes fill the head of the buffer and the tail stays
  zero — the global window holds the buffer space, so directory positions keep addressing it. A back-reference beyond
  the decoded stream throws `InvalidDataException`.

- `PlaDocument.OpenStream` and `PlaStream`: the decoded global stream of the document (every block's payload decoded in
  block order) and the extents of it. A directory record's position and count name a byte extent of the stream — the
  object's data — so `PlaStream.ReadExtent(ObjectDirectoryRecord)` returns an object's bytes; extents can also be read
  by position and count. The stream is a snapshot that holds the whole decoded file in memory and the document may be
  disposed afterwards.
- The position and count of `ObjectDirectoryRecord` are now documented: a byte extent of the decoded global stream,
  confirmed against the object directories of real files.

- `PlaDocument.FindCompressedPools()` finds pools outside indexed block payloads. `CompressedPool.Decode()` reads
  stored and QuickLZ level 1 frames using the public format description and saved-file verification.

- `CompressedPool.ReadSubgroups()` reads BL-tagged records from decoded pools and reports incomplete payloads.

- `PlaReaderOptions` (with `Default`) caps what the readers decode into memory: `MaxGlobalStreamBytes` bounds the
  decoded global stream and the window a mode-2 `ReadPayload` builds (default: the most one in-memory stream can
  hold), `MaxDecodedPoolBytes` bounds one decoded pool (default: 512 MiB) and `MaxCoverBytes` bounds the `.bimx`
  cover image (default: 64 MiB, previously fixed). The options are immutable — build a new instance to change a
  limit — and come through added overloads (`Open(string, PlaReaderOptions?)`, `Open(Stream, bool,
  PlaReaderOptions?)`; the 0.1.0 entry-point signatures are unchanged). A read past a cap throws
  `InvalidDataException` naming it. The caps are reader limits, not facts of the format.

- `PlaStream.SkippedBlocks`: the indexed blocks that failed to decode to their declared size (false entries of the
  scan), in file order — the blocks behind `UnreliableFrom`.

- `CompressedPool.ReadSubgroups()` decodes mode-2 pool subgroups. A mode-2 payload is an LZ token stream like a
  mode-2 block payload; it may start with a 13-byte pool header (a u32, the 0x47 marker, the packed payload length
  and the unpacked size) or with the file block's 9-byte stream header. Its back-references reach into the pool
  stream — the reconstruction of every preceding pool of the document and of this pool before the record — so a read
  decodes the pools before the named one; reading the pools in file order advances one shared window, so every pool
  decodes once. A skipped pool or an undecodable mode-2 subgroup resets the history. A mode-2 payload that cannot
  decode is reported with empty data and `Partial`. The decoder discards history beyond the maximum back-reference
  distance between records. `FindCompressedPools` caches its list after the first scan, so the pool objects stay
  identical between calls.

### Changed

- The core library follows the IOExtend reference layout (RvtIO, NwcIO): the entry points (`PlaDocument`,
  `BimxDocument`, `PlaFile`, `PlaReaderOptions`) stay in `PlaIO`, while the document types moved to the namespace
  `PlaIO.Model` (`FdbHeader`, `PlaBlock` + `PlaBlockMode`, `ObjectDirectory` + `ObjectDirectoryRecord`,
  `CompressedPool`, `PoolSubgroup`, `PlaStream`). The mode-2 global-window decode moved out of `PlaDocument` into
  internal machinery; the public surface and behavior are unchanged apart from the namespaces, and the entry-point
  signatures of the 0.1.0 surface keep their forms.

- The release workflow follows the IOExtend release order: the NuGet packages are pushed before the GitHub release is
  created (a refused first publication can be fixed and the job re-run, because `--skip-duplicate` makes the push
  repeatable), and a tag whose version contains a hyphen is published as a prerelease. The changelog section for the
  version is checked before anything is published.

- `PlaDocument.ReadPayload` no longer throws `NotSupportedException` for mode 2 blocks: all three storage modes
  (raw, zero mask, LZ) decode now.

## [0.1.0] - 2026-11-12

### Added

- `PlaFile.DetectKind` tells the container shared by `.pla` and `.pln` files (`ROF FDB `) and `.bimx` files (`BIMx`)
  apart by their first bytes.
- `PlaDocument` opens a `.pla` or `.pln` file or a seekable stream without loading the whole file. It exposes the length,
  the header (`FdbHeader`: layout version, format version, the raw field at offset 12 and the first 64 raw bytes) and the
  `BL` and `DY` blocks found by scanning from offset 32 (`PlaBlock`: offset, tag, id, kind, version, flags, size class,
  mode, GUID, packed and unpacked size, extra field, payload offset).
- `PlaDocument.ReadPayload` decodes payloads stored raw (mode 0) or with zero masks (mode 1). Mode 2 (LZ) is recognised
  and reported with `NotSupportedException`; a payload that does not decode to its declared size, or a cut file, is
  reported with `InvalidDataException`.
- `PlaDocument.FindDirectories` searches raw blocks for object directories of 30-byte records (`ObjectDirectoryRecord`:
  class number, position, count, object GUID) and reports how the records abut, overlap or repeat a position
  (`ObjectDirectory`). The meaning of position and count and the names of the classes are not known.
- `BimxDocument` opens a `.bimx` file or a seekable stream, measures the PNG cover image that follows the signature,
  reads it with `ReadCover` (up to 64 MiB) and reports where the rest of the content begins. That content is not
  interpreted.
- The `plaio` command line tool (package `PlaIO.Cli`) with the commands `info`, `blocks` and `directory`, JSON output of
  the directory records with `directory --out`, and the exit codes 0 (done), 1 (usage error) and 2 (input or output
  cannot be used).
- Targets .NET 8 and .NET 10, with no package dependencies. The library never writes to the console.
