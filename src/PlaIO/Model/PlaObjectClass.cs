namespace PlaIO.Model;

/// <summary>A class in the file's object class table.</summary>
/// <param name="Number">The class number local to this file.</param>
/// <param name="Guid">The class GUID stored in the file.</param>
/// <param name="Name">The Archicad API label for a recognized class, or null.</param>
public sealed record PlaObjectClass(ushort Number, Guid Guid, string? Name);
