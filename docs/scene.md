# The scene adapter

`PlaIO.Scene` converts a PLN or PLA file into the
[`SceneDocument`](https://github.com/IOExtend/IOScene/blob/main/docs/contract.md) used by
[IOViewer](https://github.com/IOExtend/IOViewer) and coordination tools. The adapter uses the public PlaIO API.

## Contents

- One model and one scene element for each recognized Archicad element. The native type is the category and the
  saved Element ID is the display name when present. Otherwise the category supplies the display name.
- Saved storeys and their elevations. Scene elements reference their home storey when it resolves to a saved level.
- A containment parent for a Door whose verified host is a Wall. Other host and cut-target GUIDs are occurrence
  properties.
- Supported element, location, classification and quantity properties. Dimensioned numeric values use SI units.
- Verified Door placement adds reference point X and Y in project coordinates, directed distance along the
  host wall, and the nominal sill relative to project zero and the wall's home story when available.
- Verified Door instance tables supply the saved GDL wall-hole width and height.
- Applicable user-defined literal values grouped by their saved property group. A linked override takes precedence
  over the default. Unavailable, undefined and expression values are omitted. Single-choice values use the saved
  choice label.
- Closed triangle meshes for verified vertical slab solids with straight edges. The mesh uses the saved contours and
  the footprint of supported through cuts from rectangular columns or separate straight-edged Opening elements.
  A slab can have holes and multiple connected parts, with one geometry instance per part. Geometry instances use
  project coordinates in metres with Z up.
- Closed triangle meshes for verified straight vertical walls with rectangular or straight-edged complex sections.
  Rectangular walls can include supported through cuts from simple Door openings.
  When a profile has several material fills, the exterior solid uses their combined section. Each disconnected
  section yields a geometry instance. The scene property `archicad:wall-net-volume` gives the solid volume.
- Closed triangle meshes for supported vertical columns, including rectangular sections with planar caps and
  straight-edged single-fill profiles. A circular section with horizontal caps is tessellated with a sagitta of
  at most `1e-7` metres, retaining the four axis extrema. A beam cut through the entire section can leave several
  mesh instances for one column. Supported cuts by horizontal or inclined beams can remove part of a profiled
  column section and produce a closed cut mesh.
  The scene property `archicad:column-net-volume` gives the listing volume. Circular columns also expose
  their analytic volume as `archicad:column-geometric-volume`.

Mesh support is pending for curved slab edges or Opening profiles, partial-depth slab cuts, combinations of Opening
and structural cuts, joined and cut walls, curved wall sections, wrapped columns and general partial column cuts.

## Frozen derivations

Consumers persist element identity using `SceneModel.Key` and `SceneElement.SourceId`.

- `Key` is `PlaSceneOptions.ModelKey` when given; otherwise the file name with its extension, without its directory.
- `SourceId` is the saved element GUID in lower-case D format.

The adapter uses `archicad:instance` for intrinsic properties. A saved user-defined group has the key
`archicad:group:<guid>` and its definitions use `archicad:property:<guid>`. A classification in
`archicad:classifications` uses `archicad:classification:<system-guid>` and carries the item GUID as text.

## Diagnostics

| Severity | Code | Meaning |
|---|---|---|
| Info | `container-only` | The container has no decoded model elements. |
| Info | `geometry-incomplete` | Reports how many decoded elements have no triangle mesh. |

A malformed or ambiguous indexed object class table raises an error.

## Publication

The adapter is available as a project reference. Its project currently has `IsPackable=false`.
The package configuration specifies an exact PlaIO dependency and an `IOScene [0.1.1, 0.2.0-0.0)` range.
