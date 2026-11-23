# Command line and exit codes

The `plaio` tool reports the container structure of a file. It prints plain text to standard output, writes messages to
standard error and tells success from failure by its exit code. It uses only the public API of the library.

## Commands

| Command | Works on | Output |
| --- | --- | --- |
| `plaio info <file>` | `.pla`, `.pln`, `.bimx` | `key: value` lines: kind, length, header fields and block counts, or the cover bounds of a `.bimx` file. |
| `plaio blocks <file>` | `.pla`, `.pln` | A tab-separated header line, then one line per indexed block. |
| `plaio directory <file> [--out <file.json>]` | `.pla`, `.pln` | A tab-separated header line, then one line per object directory found. With `--out`, also every record as JSON. |
| `plaio elements <file>` | `.pla`, `.pln` | JSON with the recognized elements and their available parameters. |
| `plaio stories <file>` | `.pla`, `.pln` | JSON with the saved project stories. |
| `plaio layers <file>` | `.pla`, `.pln` | JSON with the saved project layer attributes. |
| `plaio materials <file>` | `.pla`, `.pln` | JSON with building materials and composite definitions. |
| `plaio profiles <file>` | `.pla`, `.pln` | JSON with complex profile definitions. |
| `plaio classifications <file>` | `.pla`, `.pln` | JSON with classification systems and item trees. |
| `plaio properties <file>` | `.pla`, `.pln` | JSON with user-defined property groups and definitions. |
| `plaio layouts <file>` | `.pla`, `.pln` | JSON with the layout book and placed drawings. |
| `plaio --help`, `plaio -h` | | The usage text, on standard output. |
| `plaio --version` | | `plaio <version>`, for example `plaio 0.1.0`. |

The command comes first. `--out` is the only option and is accepted by `directory` only. The kind of the file is detected
from its first bytes, not from its extension.

## Exit codes

| Code | Name | Meaning |
| --- | --- | --- |
| 0 | done | The command finished and its output was written. |
| 1 | usage | The command line is wrong: no command, an unknown command or option, no file, an empty file name, an extra argument, or `--out` given twice or without a value. Two lines on standard error: the problem and `Run 'plaio --help' for usage.` |
| 2 | input or output error | The file cannot be used: it does not exist or cannot be opened, is not a recognised `.pla`, `.pln` or `.bimx` file, is cut short or malformed, is a `.bimx` file given to `blocks` or `directory`, or the `--out` file cannot be written. One line on standard error, prefixed with `plaio: `, no stack trace. |

Any other failure is a defect in the tool and ends the process with the runtime's unhandled exception code.

## Limits of the reader

The limits are fixed; the tool has no option to change them.

| Limit | Effect |
| --- | --- |
| At most 1048576 (2^20) blocks in one index | A file with more blocks is rejected with exit code 2. |
| Unpacked size of a block at most 2^28 bytes and at most 300 times its packed size plus 65536 | A block header outside these bounds is not taken as a block; the scan moves on. |
| Mode 2 (LZ) payloads | Listed by `blocks`. The CLI `directory` command reads raw blocks only. |
| Object directories | Searched in raw (mode 0) blocks of at least 1920 bytes, at most one per block. A directory needs at least 64 records whose extents mostly abut. The run that is reported is an estimate and can be shorter than the real directory. |

The `directory` command reports `pos`, `count` and class numbers as stored. The library exposes extent reads
through `PlaDocument.OpenStream()`.

## Output format

### elements

The JSON object has an `elements` array. Each entry contains `guid`, `type`, `elementId`, `layerIndex`, `layerName`,
`homeStoryIndex`, `homeStoryName`, `topLinkedStoryIndex`, `wallThickness`, `wallReferenceLineChordLength`, `bottomElevationToProjectZero`,
`topElevationToProjectZero`, `bottomElevationToHomeStory`, `topElevationToHomeStory`, `boundingBox`, `wallReferencePlan`,
`wallProfile`, `memberProfile`, `slabGeometry`, `beamReferenceAxis`, `beamNetVolume`,
`buildingMaterialIndex`, `compositeIndex`, `classifications`, `propertyOverrides`, `hostGuid`, `doorDimensions`, `openingCutElementGuids`, `openingProfile`
and `openingGeometry`. The fields after
`type` can be null. A box contains `xMin`, `yMin`, `zMin`, `xMax`, `yMax` and `zMax` in project coordinates and metres.
`beamNetVolume` is the verified net volume in cubic metres for a supported rectangular beam.
The wall reference plan has project XY points `start` and `end`, and signed face offsets `outsideOffset` and
`insideOffset` in metres to the right of the directed line. It describes the wall before junction cleanup.
`wallProfile` and `memberProfile` have a project-local `profileIndex` and `components` in saved drawing order. Each component has
`buildingMaterialIndex`, nullable `function`, `crossSectionArea` in square metres, local `rings` with `u` and `z`
coordinates in metres, and nullable `arcs`.
Slab geometry contains `rings` of XY points, `bottom`, `top` and the analytic `outlineArea`.
For profiles and slabs, the nullable `arcs` array contains `ringIndex`, `startVertexIndex` and `sweepRadians`. Indices are zero-based.
An arc ends at the next vertex of the ring, wrapping to the first vertex when needed. A positive sweep turns counterclockwise.
`floorPlanPerimeter` measures all saved boundaries and `floorPlanHolesPerimeter` measures the saved hole boundaries.
Both use metres.
Nullable `topAreaWithOpenings`
and `bottomAreaWithOpenings` give the horizontal face areas after verified Opening cuts. The nullable `volume`
gives the verified solid volume. Units are metres, square metres and cubic metres.
The Opening profile contains `shape`, `width` and `height` in metres. `hostGuid` and `openingCutElementGuids` report
the associated element and verified cut targets for supported Openings. For Doors, `hostGuid` gives the verified
wall and `doorDimensions` contains the saved nominal `width`, `height` and optional `sillHeightFromAnchor` in metres.
For supported straight hosts, `doorPlacement` gives the reference line point in project XY, the distance
from the wall's directed start, and the nominal sill elevation in project and host-story coordinates.
When the saved Door and its library descriptor confirm the link, `libraryPartReference` gives the main and revision GUIDs.
`doorWallHoleDimensions` contains the saved GDL wall-hole `width` and `height` in metres when the linked
instance parameter table can be verified.
For supported horizontal Openings, `openingGeometry` gives the project XY `origin` and nullable `rings` of the profile.
It also gives nullable `cuts` with a `slabGuid`, `bottom` and `top` in project coordinates, and nullable `cutVolume` in cubic metres.
`classifications` contains `systemGuid` and `itemGuid` pairs. An empty list means the element has no assignments
in the readable systems. Null means the classification catalog is unavailable.
`propertyOverrides` contains explicit saved overrides, each with a `definitionGuid` and a nullable `value` object.
The value uses the same `status` and `value` fields as the property default. The list is null when no override
is recovered for the element.
See the [supported types and parameters](public-api.md#model-elements).

### stories

The JSON object has a `stories` array, sorted by ascending `index`. Each story contains the signed `index`, saved
`name` and nullable `elevationToProjectZero` in metres. An empty saved name is written as an empty string.

### layers

The JSON object has a `layers` array sorted by `index`. Each entry contains `guid`, `index`, nullable `name` and
nullable `intersectionGroup`. A stored intersection group of zero is preserved.

### materials

The JSON object contains `buildingMaterials` and `composites` arrays sorted by their project-local `index`.
Materials have `guid`, `index`, `name`, `id` and `connectionPriority`. Composites have `guid`, `index`, `name`,
`totalThickness`, `useWith` and an ordered `skins` array. Each skin has `buildingMaterialIndex`, `thickness`,
`isCore` and `isFinish`. Thicknesses are in metres.

### profiles

The JSON object has a `profiles` array sorted by `index`. Each definition has `guid`, `index`, `name`, `useWith`,
`width`, `height`, `hasCoreSkin` and `components`. Width and height measure the saved local section, in metres.
The optional `widthStretch` and `heightStretch` objects carry `enabled`, `start`, `end` and `minimumSize`.
`modifiers` supplies the saved name and the instance and definition GUIDs for each verified named offset modifier.
Its nullable `defaultValue` gives the signed offset in metres on the undeformed catalog section.
The section uses the same component fields as placed profiles in `elements`. A `buildingMaterialIndex` of zero
denotes a fill without an assigned building material. Unsupported usage flags or section layouts yield null values
for the affected fields.

### classifications

The JSON object has a `classificationSystems` array sorted by GUID. Each system contains `guid`, `name`,
`description`, `source`, `version`, nullable ISO date `date` and `items`. Items appear in depth-first order
with siblings in saved order. Each item contains `guid`, `parentGuid`, `id`, `name` and `description`.
The `parentGuid` is null for top-level items.

### properties

The JSON object has `groups` and `definitions` arrays sorted by GUID. Each group has a `guid` and `name`.
A definition has a `guid`, `groupGuid`, `name`, `description`, `dataType` and `availableClassificationItemGuids`.
An enumeration's `choices` give each saved choice's `guid` and `displayValue` in display order.

The nullable `defaultValue` has a `status` of `normal`, `userUndefined` or `expression`. A verified normal default
has a typed JSON `value`. Numeric measurements are in SI units. A single enumeration default references the
choice GUID. See [User-defined properties](public-api.md#user-defined-properties) for available fields.

### layouts

The JSON object contains a nullable `layoutBook` with the root `guid` and an ordered `items` array. Each item
has a `guid`, `parentGuid`, `type`, nullable `name`, `id`, `sourceGuid`, `drawingSourcePath`, `databaseGuid`,
`masterLayoutGuid` and `paperSize`. An available `paperSize` has `width` and `height` in metres.
The nullable `revision` contains the current revision IDs, status flags, `issuedRevisions` in saved order and
the linked `openTransmittal` when available. A transmittal's `overridesRevisionIds` reports whether it imposes its
own ID on every included layout. Its `issuedAtUtc` uses ISO 8601.
See the [layout book API](public-api.md#layout-book).

### info

For a `.pla` or `.pln` file:

| Key | Value |
| --- | --- |
| `kind` | `FDB` (the container shared by `.pla` and `.pln`). |
| `length` | File length in bytes. |
| `layoutVersion`, `formatVersion` | The u16 header fields at offsets 8 and 10, as `0x` and four hex digits. |
| `field12` | The u32 header field at offset 12, as `0x` and eight hex digits; its meaning is not documented. |
| `headerBytes32to63` | The raw header bytes 32 to 63 in upper-case hex, or `-` when the file has none. |
| `blocks` | Number of indexed blocks. |
| `mode Raw`, `mode ZeroMask`, `mode Lz` | Number of blocks per payload mode (0, 1 and 2). |
| `tag BL`, `tag DY` | Number of blocks per tag. |
| `flag 0x10` | Number of blocks with flag `0x10` set; its meaning is not documented. |

For a `.bimx` file:

| Key | Value |
| --- | --- |
| `kind` | `BIMx`. |
| `length` | File length in bytes. |
| `coverOffset` | File offset of the PNG cover image, in hex. |
| `coverLength` | Length of the cover image in bytes, from the PNG signature through the `IEND` chunk. |
| `contentOffset` | File offset of the content after the cover, in hex. The content is not interpreted. |

### blocks

Columns, separated by tabs:

| Column | Value |
| --- | --- |
| `offset` | File offset of the block header, hex. |
| `tag` | `BL` or `DY`. |
| `id` | The raw u32 at +2, `0x` and eight hex digits. |
| `kind` | The raw u16 at +6, `0x` and four hex digits. |
| `ver` | The byte at +8, decimal. |
| `flags` | The raw u16 at +9, `0x` and four hex digits. |
| `class` | The size class, `flags & 0xF`, decimal. |
| `mode` | `Raw`, `ZeroMask` or `Lz`. |
| `packed`, `unpacked` | Stored and decoded payload size in bytes, decimal. |
| `extra` | The raw u32 stored when flag `0x400` is set, `0x` and eight hex digits, or `-`. |
| `payloadOffset` | File offset of the payload, hex. |
| `guid` | The GUID stored when flag `0x20` is set, or `-`. |

### directory

Columns, separated by tabs:

| Column | Value |
| --- | --- |
| `blockOffset` | File offset of the raw block that holds the directory, hex. |
| `recordOffset` | Offset of the first reported record within the payload, decimal. |
| `phase` | `recordOffset % 30`, the record alignment within the payload. |
| `records` | Number of records in the run. |
| `abutting` | Adjacent pairs of the records sorted by position where one extent ends where the next starts. |
| `overlapping` | Adjacent sorted pairs whose extents overlap. |
| `duplicates` | Adjacent sorted pairs with the same position. |

The header line is printed even when no directory is found.

### The `--out` JSON file

One indented UTF-8 JSON object. All numbers are decimal; the input file name is not written.

```json
{
  "length": 2550,
  "directories": [
    {
      "blockOffset": 135,
      "payloadOffset": 148,
      "recordOffset": 2,
      "phase": 2,
      "recordCount": 80,
      "abuttingPairs": 79,
      "overlappingPairs": 0,
      "duplicatePositions": 0,
      "records": [
        { "class": 152, "pos": 5043, "count": 168, "guid": "d0d18fb0-81da-fbbb-2bd4-afc18e1e5540" }
      ]
    }
  ]
}
```

| Property | Meaning |
| --- | --- |
| `length` | File length in bytes. |
| `directories[]` | One object per directory, in the order of the summary lines. |
| `blockOffset`, `payloadOffset` | File offsets of the block header and of its payload. |
| `recordOffset`, `phase`, `recordCount` | As `recordOffset`, `phase` and `records` in the summary line. |
| `abuttingPairs`, `overlappingPairs`, `duplicatePositions` | As `abutting`, `overlapping` and `duplicates` in the summary line. |
| `records[]` | Every record of the run in file order: `class` (the class number, not mapped to a name), `pos` and `count` (raw, units not known) and `guid` (the object GUID). |

The example comes from the synthetic container of the README and is shortened to one record.

The summary lines are printed before the JSON is written. The JSON is first written to a new file next to the target,
named after it with a random suffix and `.tmp`, and then moved over the target. When the write fails the temporary file is
deleted, the target is left as it was and the exit code is 2. When the input cannot be read, no output file is created.

## Scripting

```sh
plaio directory project.pln --out directories.json
case $? in
  0) ;;                                    # done; directories.json holds the records
  1) echo "check the command line" ;;
  2) echo "the file cannot be read, or directories.json cannot be written" ;;
esac

# count the mode 2 blocks
plaio blocks project.pln | awk -F '\t' 'NR > 1 && $8 == "Lz"' | wc -l
```
