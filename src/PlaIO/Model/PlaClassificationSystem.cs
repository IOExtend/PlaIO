namespace PlaIO.Model;

/// <summary>A saved classification system and its visible item tree.</summary>
/// <param name="Guid">The system GUID.</param>
/// <param name="Name">The system name.</param>
/// <param name="Description">The system description.</param>
/// <param name="Source">The system source.</param>
/// <param name="Version">The saved version label.</param>
/// <param name="Date">The saved publication date, or null when absent.</param>
/// <param name="Items">The items in depth-first order, with siblings in saved order.</param>
public sealed record PlaClassificationSystem(Guid Guid, string Name, string Description, string Source,
    string Version, DateOnly? Date, IReadOnlyList<PlaClassificationItem> Items);

/// <summary>A saved classification item.</summary>
/// <param name="Guid">The item GUID.</param>
/// <param name="ParentGuid">The parent item GUID, or null for a top-level item.</param>
/// <param name="Id">The item identifier displayed in Archicad.</param>
/// <param name="Name">The item name.</param>
/// <param name="Description">The item description.</param>
public sealed record PlaClassificationItem(Guid Guid, Guid? ParentGuid, string Id, string Name, string Description);

/// <summary>An element's saved classification.</summary>
/// <param name="SystemGuid">The GUID of the classification system.</param>
/// <param name="ItemGuid">The GUID of the assigned item in that system.</param>
public readonly record struct PlaClassificationAssignment(Guid SystemGuid, Guid ItemGuid);
