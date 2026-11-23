# Reader parity with RvtIO

The reference is the published RvtIO source at revision `2d65eefd39f3f53843d581753424c247e4b1addb`. The comparison
covers data read from files and exposed to consumers. Revit-specific identifiers and schema objects need an Archicad
counterpart only where they represent comparable model data. See the [RvtIO public API](https://github.com/IOExtend/RvtIO/blob/2d65eefd39f3f53843d581753424c247e4b1addb/docs/public-api.md).

| Reader capability | RvtIO code and tests | PlaIO status and evidence needed |
| --- | --- | --- |
| Offline input and container metadata | `RvtDocument`, `RvtFileReader`, `RvtReaderOptions` and container diagnostics tests | PLN and PLA open from file or stream. The reader exposes raw header fields, class GUIDs, directories and pools. Project metadata and a fixture matrix by format version remain to be covered. |
| Element census and selection | `RvtDocument`, `FilteredElementCollector`; class and category tests | `ReadElements()` and `plaio elements` report a GUID and type for recognized model elements. Coverage needs to account for each unsupported class and model database. |
| Storeys, axes and element placement | `GetBuildingStoreyCatalog()`, `GetGridAxes()`; storey checks in `RvtSceneReaderTests` and grid paths in `SceneGridPathTests` | `ReadStories()` provides the full saved catalog for supported story tables. `ReadGridAxes()` reads straight and circular reference axes, including segmented and hidden floor plan displays, and the saved axis height. The scene adapter preserves grid labels and places lines and tessellated circular paths at their elevation relative to project zero. Labels, endpoints and arc geometry can be compared with a native IFC export. Element records expose the home story and, for walls and columns, the linked top story. Straight walls with a verified saved rectangle have a project XY reference line and face offsets. Supported straight and horizontally curved beams expose their directed reference axes in project coordinates. Supported doors expose the station on the wall's reference line and nominal sill elevations. Other grid layouts and general element placement are pending. |
| Effective categories, materials and classifications | `Element`, `MaterialLayer` and `IfcExporterClassificationCurrentTests` | PlaIO exposes native element types, effective layer references, building materials, ordered composite skins, complex profile definitions with default modifier values and the material, composite or profile references for supported elements. It reads classification systems, ordered item trees and per-element assignments, verified against Archicad API with exact matches. Placed modifier overrides and materials on other element types remain pending. |
| Native and user properties | `Parameter`, `ParameterDefinition`, `StorageType`; `NativeFieldParametersTests`, `UserParameterCatalogTests` | PlaIO exposes Element ID and supported dimensional and placement values, including wall-hole dimensions from the placed Door's GDL parameters. It reads user-defined groups, definitions, data types, classification availability, enumeration choices, supported literal defaults and linked element-level overrides. Definition metadata, default status, element applicability and the saved text and single-choice overrides match Archicad API on the validation corpus. Built-in property definitions, remaining value encodings and formula evaluation are pending. |
| Hosts, openings and other model relationships | `Element.HostId`, `SerializedHostId`, `DerivedInstanceParamsTests`, `CurtainFamilyHostSerializationTests` | PlaIO exposes the Door wall, associated wall or slab for Openings, and Opening cut targets when both records confirm the links. It checks several structural junctions. Relationships still need a direct API comparison. The embedded library catalog exposes verified GSM parts and their identities. Assembly members, external library references and evaluation of door cuts from library parts are pending. |
| Extents and quantities | `Element.BoundingBox`, `Element.Geometry.BoundingBox`; wall and quantity tests | PlaIO exposes 3D boxes for supported walls, slabs, zones, vertical columns and beams with straight or circular axes. It reports evaluated wall elevations for supported junctions by checking which cutters reach each plane and which wall material survives inside slab holes or beyond the slab edges. It resolves supported circular end connections with matching profiles and slab cuts that preserve the extrema. Net volumes are available for supported horizontal rectangular beams, fully covered horizontal profiles and inclined prisms. Supported wall and slab Openings expose elevations, including one-sided slab cuts and overlaps that fully cover the Opening profile. Boxes for other layouts and types, general evaluated boxes, areas and volumes are pending. |
| Evaluated geometry, meshes and instances | `GeometryData`, `ElementMeshes`; `ElementMeshesTests`, `ElementMeshesSampleTests` | PlaIO exposes slab boundaries and saved complex cross-sections with straight and circular edges, including holes. It computes slab face areas after verified openings and volume after supported opening, column and profiled wall cuts on straight-edged slabs. It builds closed meshes for verified straight-edged slab parts, straight unjoined walls and supported vertical single-segment columns. Column meshes cover circular sections with horizontal caps, full-section end cuts, separated spans and verified partial cuts by horizontal or inclined beams. Circular columns expose analytic volume and the polygonal listing quantities. Supported Opening profiles and cut intervals are available in project coordinates. Multi-segment beam and column profiles, other curved solids, general partial-depth cuts and evaluated junction geometry are pending. |
| Sheets and view contents | `SheetCatalog`; `SheetCatalogTests` | PlaIO reads layout book membership and order, layout and drawing identifiers and names, layout databases, assigned master layouts, paper sizes, drawing source GUIDs and issued revision history with transmittal date and issuer. For revisions explicitly linked to an open transmittal it reads supported current IDs, including manually assigned IDs and transmittal overrides. Localized display IDs, drawing content and view-owned graphics remain pending. |
| Reader diagnostics and geometry coverage | `RvtReadDiagnostics`, `ExportReport`; `DiagnosticOutputTests` | PlaIO reports container limits and unusable data. Element reports still need to identify unsupported operations and distinguish absent, unsupported and verified values. |
| Consumer output | RvtIO element JSON, IFC and `RvtSceneReader`; `RvtSceneReaderTests` | PlaIO provides CLI JSON for elements, stories, layers and grid axes. The scene adapter maps recognized elements, saved levels, grid lines and paths, native properties, applicable literal user properties, classification assignments, host relationships and supported slab, wall and column meshes. Broader mesh coverage and downstream IFC export are pending. |

For each supported field, compare PlaIO against Archicad API for the exact saved files. Match elements by GUID and storeys
by their signed index. Record applicable API values, matching reader values, missing reader values and differences
separately for each type and field. Record the tolerances with the comparison. For project-zero and home-story elevations,
and for coordinates of 3D bounding boxes, the current absolute tolerance is `1e-7` metres. Preservation of other CLI output is checked according to
[CONTRIBUTING.md](https://github.com/IOExtend/PlaIO/blob/main/CONTRIBUTING.md).

For grid axes, the Archicad JSON interface identifies the placed object by GUID but returns unsupported-element
errors for its boxes. Compare saved labels, directed endpoints, project elevations and circular center, radius and sweep with the native
`IfcGridAxis` export after converting IFC units and removing the IFC site placement. Keep repeated labels and
coincident axes in the comparison. Floor plan display segment length is saved separately from the reference geometry.

Check the IOScene output by source GUID against the same API element census and classification assignments.
Compare level elevations by saved story index using `GetStoryNavigatorItems`, recording any API labels generated
for empty saved names. Compare typed scene properties with the core reader and applicable literal values with
`GetPropertyValuesOfElements`. Check closed, consistently oriented triangles for each mesh. Aggregate all instances
by element GUID before comparing world-space bounds, cap areas and signed volume. For evaluated slab solids check
`General_NetTopSurfaceArea`, `General_NetBottomSurfaceArea` and `General_NetVolume`. Apply the slab quantity
tolerances below using the evaluated boundary perimeter and report missing meshes by category.
Record any difference between the element extent reported by `Get3DBoundingBoxes` and the mesh's
evaluated extent. When a cut removes a slab extremum, an isolated 3D export can verify the extent separately,
using the coordinate precision of the export.
For wall meshes, compare `General_NetVolume` with a tolerance of `1e-7` metres times the total mesh surface area,
with a minimum of one square metre of surface area. For a rectangular wall, compare `General_NetTopSurfaceArea`
at a tolerance of `1e-7` metres times the upper face perimeter, with a minimum of one metre of perimeter.
For a wall with through Door cuts, compare the closed mesh in project coordinates. Check the net volume,
upper face area, box and four scheduled elevations independently against the native API.
Use the same volume and face-area tolerances for columns with a horizontal upper face. Check the column mesh
against its tight `ColumnExtrusion.BoundingBox`. For columns with separated spans, measure the area left at
the authored top face for `General_NetTopSurfaceArea`. Compare the tight box separately with `Get3DBoundingBoxes`
and record the per-axis difference. Compare `PlaElement.BoundingBox` directly with `Get3DBoundingBoxes`, including
the authored cap-axis heights and the plan extent of a supported crosshair symbol on a complex-profile column.
Compare the four scheduled elevations using their individual API properties.
For `ColumnCutMesh`, include only triangles on the saved upper cap when measuring the native upper area.
For circular `ColumnExtrusion` solids, compare `ScheduledCapArea` with `General_NetTopSurfaceArea`,
`ScheduledLateralArea` with `General_NetBottomSurfaceArea` and `ScheduledVolume` with `General_NetVolume`.
On the validated files, the API's bottom surface property returns the polygon's lateral area.
Use a tolerance of `1e-9` square or cubic metres. Compare extents and heights directly.
Verify the analytic cylinder and tessellated scene separately from the schedule. The scene uses a
`1e-7` metre sagitta limit, with at most 16,384 boundary segments per circle.

Compare the saved GDL wall-hole dimensions with `WindowDoor_WallHoleDimension`. Parse the two lengths
and allow half the unit of the last displayed digit. For the validation projects the API formats the values
to three decimal places in metres. A library-generated cut can extend outside the listed dimensions.

Compare classification systems by GUID, including the saved metadata and visible items in tree order. Compare
element assignments by system and item GUID using `GetClassificationsOfElements`. Count matching unassigned
elements, missing classifications and mismatched assignments separately.

Compare the user-defined property catalog by group and definition GUID, including type, description, enumeration
labels in display order and the set of available classification item GUIDs. Compare supported scalar defaults in
SI units and enumeration defaults by their displayed choice. Track undefined and expression defaults separately
from literal values. Join classification availability with saved element assignments and compare the resulting
pairs with `GetAllPropertyIdsOfElements`.
Compare explicit overrides against `GetPropertyValuesOfElements` by element and definition GUID. Count values that
match the definition default, unavailable properties and values that require an override separately.

Compare layout book GUIDs, parent links, type labels and depth-first order with `GetNavigatorItemTree`.
Compare the names and identifiers of subsets, layouts, master layouts and placed drawings. Compare paper
dimensions with `GetLayoutSettings` using an absolute tolerance of `1e-7` metres after converting the API's
millimetres. Check each layout's master assignment through reciprocal database links and compare the inherited
dimensions with those returned for the layout. Compare drawing source GUIDs directly with the navigator tree.
Saved drawing source paths have their own file field and are reported when available.
Compare revision IDs and the issued and open status flags with `GetLayoutSettings` by layout GUID. Record
known absence separately from null IDs and unavailable summaries. Check issued history order and transmittal metadata
against the native layout revision history. Check open IDs on saved copies with edited layout numbering, with and
without a transmittal override, and after a previous override has been issued.

Compare beam elevations against the four `General_*Elevation*` properties and the 3D box against
`Get3DBoundingBoxes`. Horizontal beams with unresolved plan extents can still be checked by their box Z limits.
At an inclined butt joint, check the box and schedule properties separately because the latter can include a
vertical limit from the adjoining beam.
Compare beam net volumes against `General_NetVolume` to `1e-8` cubic metres. Account for API values that are
missing separately from reader gaps.

Compare beam reference-axis inclination with `Beam_SlantAngle`. Check the endpoints and circular sweep against
the independently saved axis and end frames. Compare authored length to listing lengths in cases where cleanup
preserves that span. A listing length can change with intersection geometry.

For a supported wall reference plan, project the two signed face offsets from each reference line endpoint.
Compare the resulting XY limits with `Get3DBoundingBoxes` in cases where junction cleanup preserves those limits.
Validate the direction and individual face offsets against the independently saved wall rectangle.

Compare the material and composite catalogs by GUID and project-local index against
`GetBuildingMaterialAttributes`, `GetCompositeAttributes` and `GetAttributesIndices`. Check skin order, building
material references, role flags and thickness at `1e-7` metres. Compare wall and slab assignments by the
composite name and the component material properties where available.

Compare the complex profile catalog by GUID and project-local index through `GetAttributesIndices` and
`GetProfileAttributes`. Check names, enabled element types, core and stretch flags exactly, and the default and minimum
widths and heights to `1e-7` metres. Compare the named offset modifiers as a multiset and their default values to the
same tolerance. Verify the reciprocal links, the anchor and dimension records, and the undeformed contours,
arcs and stretch intervals against the saved definition.

Compare each placed complex wall, beam and column profile index through `GetAttributesIndices`, `GetProfileAttributes` and
`Construction_ProfileName`. Compare the component counts and the material names, IDs, roles and cross-section
areas against `GetComponentsOfElements` and `GetPropertyValuesOfElementComponents`. Match components as a multiset
because the saved drawing order can differ from API enumeration. The area tolerance is `1e-7` square metres.
Compare local vertices and arc endpoints against the saved section. Track unresolved catalog indices and API components
with role `None` and zero returned area separately from comparable materials, roles and areas.

Slab area comparisons propagate the coordinate tolerance by multiplying it by the relevant boundary perimeter, with
a minimum of one metre. Volume comparisons multiply the area tolerance by slab thickness and add the thickness
tolerance of `1e-7` metres times the area, with a minimum of one square metre. Compare supported slab volumes with
`General_NetVolume`. Compare `TopAreaWithOpenings` and `BottomAreaWithOpenings` against
`Slab_GrossTopSurfaceAreaWithHoles` and `Slab_GrossBottomSurfaceAreaWithHoles`.
Compare the saved slab boundary lengths against `General_FloorPlanPerimeter` and `General_FloorPlanHolesPerimeter`
with an absolute tolerance of `1e-7` metres. For a curved slab outline, compare the extents reconstructed from all vertices and arc extrema with
`Get3DBoundingBoxes`. Verify analytic `OutlineArea` by independently integrating the saved circular edges.
Track differences from Archicad's scheduled face quantities separately.

Compare each Opening profile against `Opening_Shape`, `General_Width` and `General_Height`. Use exact shape names
and the same `1e-7` metre tolerance for widths and heights. Check Opening elevations in both reference systems.
Verify associated hosts and cut targets from both ends of the saved relationships. The Archicad JSON API supplies
the compared profile and elevation values.
Compare Door width, height and sill height relative to its anchor against `General_Width`, `General_Height`
and `WindowDoor_SillHeightFromAnchor` at the same tolerance. Verify the Door wall GUID from the reciprocal
relations and compare the wall's Element ID against `IdAndCategories_ParentId`.
Compare Opening project profile bounds against `Get2DBoundingBoxes` at the coordinate tolerance above. Use
`General_NetVolume` for evaluated Opening cut volumes with a tolerance of `2e-9` cubic metres on the validation set.
For a slab with verified straight-edged through Openings, check the void topology, compare the cut boundaries with
the native profile positions and verify the slab's aggregate net areas and volume.

Regressions for confirmed layouts belong in synthetic tests. The private input files, API exports and project-specific
results stay outside the repository.
