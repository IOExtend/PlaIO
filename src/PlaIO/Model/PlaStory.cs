namespace PlaIO.Model;

/// <summary>A story in the saved project story table.</summary>
/// <param name="Index">The signed number of the story relative to the project's story zero.</param>
/// <param name="Name">The saved story name. An empty name is preserved.</param>
/// <param name="ElevationToProjectZero">The elevation in metres, or null if unavailable.</param>
public sealed record PlaStory(int Index, string Name, double? ElevationToProjectZero);
