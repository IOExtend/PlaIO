namespace PlaIO.Model;

/// <summary>A layer attribute in the saved project.</summary>
/// <param name="Guid">The layer's stored attribute GUID.</param>
/// <param name="Index">The positive, project-local layer index used by element records.</param>
/// <param name="Name">The saved name, or null if the file stores a localization token.</param>
/// <param name="IntersectionGroup">The stored intersection group, or null when unavailable.</param>
public sealed record PlaLayer(Guid Guid, int Index, string? Name, uint? IntersectionGroup);
