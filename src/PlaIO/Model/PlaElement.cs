namespace PlaIO.Model;

/// <summary>A model element of a recognized class.</summary>
/// <param name="Guid">The element GUID stored in the file.</param>
/// <param name="Type">The recognized Archicad API element type.</param>
/// <param name="ElementId">The stored Element ID, or null if unavailable.</param>
/// <param name="WallThickness">The thickness in metres of a recognized constant-width wall, or null.</param>
/// <param name="WallReferenceLineChordLength">The distance in metres between the endpoints of a wall's reference
/// line, or null. On a curved wall this is the chord length.</param>
public sealed record PlaElement(
    Guid Guid,
    string Type,
    string? ElementId,
    double? WallThickness,
    double? WallReferenceLineChordLength)
{
    /// <summary>The index of the effective layer in this project, or null if unavailable.</summary>
    public int? LayerIndex { get; init; }

    /// <summary>The stored name of the effective layer, or null if unavailable.</summary>
    public string? LayerName { get; init; }

    /// <summary>The project-relative home story number. It can be negative. Null means unavailable.</summary>
    public int? HomeStoryIndex { get; init; }

    /// <summary>The stored home story name, or null if unavailable. A saved empty name is preserved.</summary>
    public string? HomeStoryName { get; init; }

    /// <summary>The project-relative index of the story to which the element's top is linked, or null.</summary>
    public int? TopLinkedStoryIndex { get; init; }

    /// <summary>The saved building material index of a supported basic wall or slab, or null.</summary>
    public int? BuildingMaterialIndex { get; init; }

    /// <summary>The saved composite attribute index of a supported composite wall or slab, or null.</summary>
    public int? CompositeIndex { get; init; }

    /// <summary>Saved assignments in system GUID order. Empty means no assignment in a readable catalog.
    /// Null means the catalog is unavailable.</summary>
    public IReadOnlyList<PlaClassificationAssignment>? Classifications { get; init; }

    /// <summary>Saved user-defined property overrides in definition GUID order, or null when no override was read.</summary>
    public IReadOnlyList<PlaPropertyOverride>? PropertyOverrides { get; init; }

    /// <summary>The verified GUID of a Door's wall or an Opening's associated element, or null if unavailable.</summary>
    public Guid? HostGuid { get; init; }

    /// <summary>The nominal dimensions of a Door, or null if unavailable.</summary>
    public PlaDoorDimensions? DoorDimensions { get; init; }

    /// <summary>The reference line position and nominal sill of a supported Door, or null if unavailable.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PlaDoorPlacement? DoorPlacement { get; init; }

    /// <summary>The saved wall-hole dimensions supplied by the Door's GDL parameter table, or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PlaDoorWallHoleDimensions? DoorWallHoleDimensions { get; init; }

    /// <summary>The library part identity from a Door's reciprocal saved descriptor, or null if unavailable.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PlaLibraryPartReference? LibraryPartReference { get; init; }

    /// <summary>The verified GUIDs of elements cut by an Opening, or null if the list is unavailable.</summary>
    public IReadOnlyList<Guid>? OpeningCutElementGuids { get; init; }

    /// <summary>The base profile of an Opening, or null if unavailable.</summary>
    public PlaOpeningProfile? OpeningProfile { get; init; }

    /// <summary>The project profile and verified slab cuts of a supported horizontal Opening, or null.</summary>
    public PlaOpeningGeometry? OpeningGeometry { get; init; }

    /// <summary>The reference line and transverse placement of a supported straight wall, or null.</summary>
    public PlaWallReferencePlan? WallReferencePlan { get; init; }

    /// <summary>The saved local cross-section of a supported complex-profile wall, or null if unavailable.</summary>
    public PlaWallProfile? WallProfile { get; init; }

    /// <summary>The evaluated solid of a verified straight prismatic wall, or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PlaWallExtrusion? WallExtrusion { get; internal init; }

    /// <summary>The evaluated solid of a verified vertical column, or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PlaColumnExtrusion? ColumnExtrusion { get; internal init; }

    /// <summary>The evaluated mesh of a supported column cut by inclined beams, or null.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PlaColumnCutMesh? ColumnCutMesh { get; internal init; }

    /// <summary>The saved local cross-section of a supported single-segment beam or column, or null.</summary>
    public PlaMemberProfile? MemberProfile { get; init; }

    /// <summary>The saved or reconstructed 3D bounding box in project coordinates, or null if unavailable.</summary>
    public PlaBoundingBox? BoundingBox { get; init; }

    /// <summary>The saved beam reference axis in project coordinates, or null if unavailable.</summary>
    public PlaBeamReferenceAxis? BeamReferenceAxis { get; init; }

    /// <summary>The evaluated beam volume in cubic metres, or null if unavailable.</summary>
    public double? BeamNetVolume { get; init; }

    /// <summary>The saved slab outline and its volume when the slab is a verified vertical extrusion.</summary>
    public PlaSlabGeometry? SlabGeometry { get; init; }

    /// <summary>The bottom elevation in metres relative to project zero, or null if unavailable.</summary>
    public double? BottomElevationToProjectZero { get; init; }

    /// <summary>The top elevation in metres relative to project zero, or null if unavailable.</summary>
    public double? TopElevationToProjectZero { get; init; }

    /// <summary>The bottom elevation in metres relative to the home story, or null if unavailable.</summary>
    public double? BottomElevationToHomeStory { get; init; }

    /// <summary>The top elevation in metres relative to the home story, or null if unavailable.</summary>
    public double? TopElevationToHomeStory { get; init; }
}
