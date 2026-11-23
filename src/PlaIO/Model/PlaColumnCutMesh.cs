namespace PlaIO.Model;

/// <summary>The evaluated boundary of a column whose remaining section varies along its height or width.</summary>
/// <param name="Origin">The mesh origin in project metres.</param>
/// <param name="Vertices">The vertices in metres relative to the origin.</param>
/// <param name="Indices">The vertex indices of successive outward-facing triangles.</param>
/// <param name="BoundingBox">The tight bounds of the remaining solid in project metres.</param>
/// <param name="Volume">The net volume in cubic metres.</param>
public sealed record PlaColumnCutMesh(PlaColumnMeshPoint Origin, IReadOnlyList<PlaColumnMeshPoint> Vertices,
    IReadOnlyList<int> Indices, PlaBoundingBox BoundingBox, double Volume);

/// <summary>A 3D point with coordinates in metres.</summary>
public readonly record struct PlaColumnMeshPoint(double X, double Y, double Z);
