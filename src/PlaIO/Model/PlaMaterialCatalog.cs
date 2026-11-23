namespace PlaIO.Model;

/// <summary>The saved building materials and composite definitions, sorted by project-local index.</summary>
/// <param name="BuildingMaterials">The building materials.</param>
/// <param name="Composites">The composite definitions.</param>
public sealed record PlaMaterialCatalog(IReadOnlyList<PlaBuildingMaterial> BuildingMaterials, IReadOnlyList<PlaComposite> Composites);

/// <summary>A building material attribute used by model elements and composite skins.</summary>
/// <param name="Guid">The saved attribute GUID.</param>
/// <param name="Index">The positive project-local material index.</param>
/// <param name="Name">The saved attribute name.</param>
/// <param name="Id">The user-defined material ID, or null if unavailable.</param>
/// <param name="ConnectionPriority">The normalized intersection priority from 0 to 999, or null for an unsupported encoding.</param>
public sealed record PlaBuildingMaterial(Guid Guid, int Index, string Name, string? Id, int? ConnectionPriority);

/// <summary>A saved layered construction, in the attribute's skin order.</summary>
/// <param name="Guid">The saved attribute GUID.</param>
/// <param name="Index">The positive project-local composite index.</param>
/// <param name="Name">The saved attribute name.</param>
/// <param name="TotalThickness">The total thickness in metres.</param>
/// <param name="Skins">The saved skins. Walls start at the outside face and slabs start at the top face.</param>
/// <param name="UseWith">The supported element types, or null if the stored usage flags are unsupported.</param>
public sealed record PlaComposite(Guid Guid, int Index, string Name, double TotalThickness,
    IReadOnlyList<PlaCompositeSkin> Skins, IReadOnlyList<string>? UseWith);

/// <summary>A layer in a composite definition.</summary>
/// <param name="BuildingMaterialIndex">The saved reference to a project-local building material.</param>
/// <param name="Thickness">The layer thickness in metres.</param>
/// <param name="IsCore">Whether the layer is part of the core.</param>
/// <param name="IsFinish">Whether the layer is a finish.</param>
public readonly record struct PlaCompositeSkin(int BuildingMaterialIndex, double Thickness, bool IsCore, bool IsFinish);
