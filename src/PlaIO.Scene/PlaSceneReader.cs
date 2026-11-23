using System.Reflection;
using IOScene;

namespace PlaIO.Scene;

/// <summary>
/// Turns an Archicad container (a .pla or .pln file) into a scene (<see cref="SceneDocument"/>) with its decoded
/// elements, levels, properties and supported meshes.
/// </summary>
/// <remarks>
/// <para>The persisted identity is the model key together with the saved Archicad element GUID in D format.
/// The model uses the supplied <see cref="PlaSceneOptions.ModelKey"/> or the file name with its extension.</para>
/// </remarks>
public static class PlaSceneReader
{
    private const int ModelId = 1;

    /// <summary>Reads the container at <paramref name="path"/> and converts the decoded model to a scene.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty, or <see cref="PlaSceneOptions.ModelKey"/> is empty.</exception>
    public static PlaSceneResult Read(string path, PlaSceneOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string key = options?.ModelKey ?? Path.GetFileName(path);
        if (key.Length == 0) throw new ArgumentException("The model key is empty.", nameof(options));
        return Map(path, key);
    }

    private static PlaSceneResult Map(string path, string key)
    {
        using var document = PlaDocument.Open(path);
        var scene = new SceneDocument { Title = key };
        scene.Models.Add(new SceneModel
        {
            Id = ModelId,
            Key = key,
            Name = key,
            SourceFormat = SourceFormat(path),
            Producer = Producer,
        });
        bool hasClassTable = document.HasObjectClassTable;
        var levels = new Dictionary<int, int>();
        foreach (var story in hasClassTable ? document.ReadStories() : [])
        {
            int id = scene.Levels.Count + 1;
            levels.Add(story.Index, id);
            scene.Levels.Add(new SceneLevel { Id = id, ModelId = ModelId, Name = story.Name, Elevation = story.ElevationToProjectZero });
        }

        var elements = hasClassTable ? document.ReadElements() : [];
        if (elements.Count == 0)
            return new PlaSceneResult
            {
                Document = scene,
                Diagnostics = [new(SceneSeverity.Info, "container-only", "The model contains no decoded elements.")],
            };

        var properties = new PlaSceneProperties(document.ReadPropertyCatalog());
        var byGuid = new Dictionary<Guid, SceneElement>(elements.Count);
        foreach (var element in elements)
        {
            var mapped = new SceneElement
            {
                Id = scene.Elements.Count + 1,
                ModelId = ModelId,
                SourceId = element.Guid.ToString("D"),
                Name = string.IsNullOrEmpty(element.ElementId) ? element.Type : element.ElementId,
                Category = element.Type,
                LevelId = element.HomeStoryIndex is { } index && levels.TryGetValue(index, out int level) ? level : null,
            };
            scene.Elements.Add(mapped);
            byGuid.Add(element.Guid, mapped);
            properties.Map(scene, element, mapped);
            if (element.SlabGeometry is { VerticalPrisms: { } prisms } slab)
                PlaExtrusionMesh.Add(scene, mapped.Id, prisms, slab.Bottom, slab.Top);
            if (element.WallExtrusion is { } wall)
                PlaExtrusionMesh.Add(scene, mapped.Id, wall);
            if (element.ColumnExtrusion is { } column)
                PlaExtrusionMesh.Add(scene, mapped.Id, column);
            if (element.ColumnCutMesh is { } cut)
            {
                int geometryId = scene.Geometries.Count + 1;
                scene.Geometries.Add(new SceneGeometry
                {
                    Id = geometryId,
                    Vertices = cut.Vertices.Select(p => new Vector3d(p.X, p.Y, p.Z)).ToArray(),
                    Indices = cut.Indices,
                });
                scene.Instances.Add(new SceneInstance
                {
                    ElementId = mapped.Id,
                    GeometryId = geometryId,
                    Transform = Transform3d.Identity with { Translation = new Vector3d(cut.Origin.X, cut.Origin.Y, cut.Origin.Z) },
                });
            }
        }

        foreach (var element in elements)
        {
            if (element.Type == "Door" && element.HostGuid is { } host &&
                byGuid.TryGetValue(host, out var parent) && parent.Category == "Wall")
                byGuid[element.Guid].ParentId = parent.Id;
        }

        int withoutMesh = elements.Count - scene.Instances.Select(i => i.ElementId).Distinct().Count();
        return new PlaSceneResult
        {
            Document = scene,
            Diagnostics = withoutMesh == 0 ? [] :
                [new(SceneSeverity.Info, "geometry-incomplete", FormattableString.Invariant(
                    $"Triangle meshes are unavailable for {withoutMesh} of {elements.Count} decoded elements."))],
        };
    }

    private static string? SourceFormat(string path)
    {
        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension is "pla" or "pln" ? extension : null;
    }

    /// <summary>"PlaIO.Scene" and the version of this assembly without the source revision.</summary>
    private static string Producer { get; } = "PlaIO.Scene " + (typeof(PlaSceneReader).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(PlaSceneReader).Assembly.GetName().Version?.ToString(3));
}
