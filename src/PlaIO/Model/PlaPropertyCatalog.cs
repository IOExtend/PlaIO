namespace PlaIO.Model;

/// <summary>User-defined property groups and definitions saved in the project.</summary>
/// <param name="Groups">The saved property groups in GUID order.</param>
/// <param name="Definitions">The saved property definitions in GUID order.</param>
public sealed record PlaPropertyCatalog(IReadOnlyList<PlaPropertyGroup> Groups, IReadOnlyList<PlaPropertyDefinition> Definitions);

/// <summary>A saved user-defined property group.</summary>
/// <param name="Guid">The group GUID.</param>
/// <param name="Name">The group name.</param>
public sealed record PlaPropertyGroup(Guid Guid, string Name);

/// <summary>A saved user-defined property and its classification availability.</summary>
/// <param name="Guid">The property GUID.</param>
/// <param name="GroupGuid">The GUID of its property group.</param>
/// <param name="Name">The displayed name.</param>
/// <param name="Description">The saved description.</param>
/// <param name="DataType">The Archicad data type label, or null for an unsupported encoding.</param>
/// <param name="AvailableClassificationItemGuids">GUIDs of the classification items for which the property is available.</param>
/// <param name="Choices">The saved choices for a supported enumeration, or null if unavailable or inapplicable.</param>
/// <param name="DefaultValue">The verified saved default, or null if unavailable.</param>
public sealed record PlaPropertyDefinition(Guid Guid, Guid GroupGuid, string Name, string Description, string? DataType,
    IReadOnlyList<Guid> AvailableClassificationItemGuids, IReadOnlyList<PlaPropertyChoice>? Choices,
    PlaPropertyValue? DefaultValue);

/// <summary>A saved enumeration choice.</summary>
/// <param name="Guid">The stored GUID of the choice.</param>
/// <param name="DisplayValue">Its displayed label.</param>
public sealed record PlaPropertyChoice(Guid Guid, string DisplayValue);

/// <summary>The status and value of a saved property value or default.</summary>
/// <param name="Status">One of <c>normal</c>, <c>userUndefined</c> or <c>expression</c>.</param>
/// <param name="Value">A literal string, integer, double or single choice GUID for a verified normal default.
/// Numeric measurements are in SI units. An undefined or expression default has no literal value.</param>
public sealed record PlaPropertyValue(string Status, object? Value);

/// <summary>A saved override of a user-defined property on an element.</summary>
/// <param name="DefinitionGuid">The GUID of the property definition in the project catalog.</param>
/// <param name="Value">The stored value, or null for an unsupported encoding.</param>
public sealed record PlaPropertyOverride(Guid DefinitionGuid, PlaPropertyValue? Value);
