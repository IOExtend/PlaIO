using System.Globalization;
using IOScene;
using PlaIO.Model;

namespace PlaIO.Scene;

internal sealed class PlaSceneProperties(PlaPropertyCatalog catalog)
{
    private readonly Dictionary<Guid, string> groupNames = catalog.Groups.ToDictionary(group => group.Guid, group => group.Name);
    private readonly ILookup<Guid, PlaPropertyDefinition> available = catalog.Definitions
        .SelectMany(definition => definition.AvailableClassificationItemGuids.Select(item => (item, definition)))
        .ToLookup(pair => pair.item, pair => pair.definition);

    internal void Map(SceneDocument scene, PlaElement element, SceneElement mapped)
    {
        AddSet(scene, mapped, "archicad:instance", "Archicad element", NativeProperties(element));
        if (element.Classifications is not { Count: > 0 } assignments) return;

        AddSet(scene, mapped, "archicad:classifications", "Archicad classifications", assignments.Select(assignment =>
            new SceneProperty
            {
                Key = $"archicad:classification:{assignment.SystemGuid:D}",
                Name = $"Classification {assignment.SystemGuid:D}",
                Value = SceneValue.FromText(assignment.ItemGuid.ToString("D")),
            }).ToList());
        var overrides = element.PropertyOverrides?.ToDictionary(entry => entry.DefinitionGuid);
        foreach (var group in assignments.SelectMany(assignment => available[assignment.ItemGuid])
            .DistinctBy(definition => definition.Guid).OrderBy(definition => definition.Guid)
            .GroupBy(definition => definition.GroupGuid))
        {
            var properties = new List<SceneProperty>();
            foreach (var definition in group)
            {
                var saved = overrides is not null && overrides.TryGetValue(definition.Guid, out var entry)
                    ? entry.Value : definition.DefaultValue;
                if (ToSceneValue(definition, saved) is { } value)
                    properties.Add(new SceneProperty
                    {
                        Key = $"archicad:property:{definition.Guid:D}",
                        Name = definition.Name,
                        Value = value,
                    });
            }
            AddSet(scene, mapped, $"archicad:group:{group.Key:D}", groupNames[group.Key], properties);
        }
    }

    private static void AddSet(SceneDocument scene, SceneElement element, string key, string name,
        List<SceneProperty> properties)
    {
        if (properties.Count == 0) return;
        int id = scene.PropertySets.Count + 1;
        scene.PropertySets.Add(new ScenePropertySet { Id = id, Key = key, Name = name, Properties = properties });
        element.PropertySetIds.Add(id);
    }

    private static List<SceneProperty> NativeProperties(PlaElement element)
    {
        var properties = new List<SceneProperty>();
        Text("element-id", "Element ID", element.ElementId);
        Text("layer", "Layer", element.LayerName);
        Integer("layer-index", "Layer index", element.LayerIndex);
        Text("home-story", "Home story", element.HomeStoryName);
        Integer("home-story-index", "Home story index", element.HomeStoryIndex);
        Integer("top-linked-story-index", "Top linked story index", element.TopLinkedStoryIndex);
        Integer("building-material-index", "Building material index", element.BuildingMaterialIndex);
        Integer("composite-index", "Composite index", element.CompositeIndex);
        Integer("profile-index", "Profile index", element.WallProfile?.ProfileIndex ?? element.MemberProfile?.ProfileIndex);
        Text("host-guid", "Host GUID", element.HostGuid?.ToString("D"));
        if (element.OpeningCutElementGuids is { } cutTargets)
            Text("opening-cut-guids", "Opening cut GUIDs", string.Join(", ", cutTargets));
        Number("bottom-to-project-zero", "Bottom elevation to project zero", element.BottomElevationToProjectZero);
        Number("top-to-project-zero", "Top elevation to project zero", element.TopElevationToProjectZero);
        Number("bottom-to-home-story", "Bottom elevation to home story", element.BottomElevationToHomeStory);
        Number("top-to-home-story", "Top elevation to home story", element.TopElevationToHomeStory);
        Number("wall-thickness", "Wall thickness", element.WallThickness);
        Number("wall-reference-chord", "Wall reference line chord", element.WallReferenceLineChordLength);
        if (element.DoorDimensions is { } door)
        {
            Number("door-width", "Nominal door width", door.Width);
            Number("door-height", "Nominal door height", door.Height);
            Number("door-sill-from-anchor", "Door sill height from anchor", door.SillHeightFromAnchor);
        }
        if (element.DoorPlacement is { } placement)
        {
            Number("door-reference-x", "Door reference point X", placement.ReferencePoint.X);
            Number("door-reference-y", "Door reference point Y", placement.ReferencePoint.Y);
            Number("door-distance-along-wall", "Door distance along host wall", placement.DistanceAlongWall);
            Number("door-sill-to-project-zero", "Nominal door sill to project zero", placement.SillElevationToProjectZero);
            Number("door-sill-to-host-story", "Nominal door sill to host story", placement.SillElevationToHostStory);
        }
        if (element.DoorWallHoleDimensions is { } wallHole)
        {
            Number("door-wall-hole-width", "Saved door wall-hole width", wallHole.Width);
            Number("door-wall-hole-height", "Saved door wall-hole height", wallHole.Height);
        }
        if (element.OpeningProfile is { } opening)
        {
            Text("opening-shape", "Opening shape", opening.Shape);
            Number("opening-width", "Opening profile width", opening.Width);
            Number("opening-height", "Opening profile height", opening.Height);
        }
        if (element.BoundingBox is { } bounds)
        {
            Number("bounds-min-x", "Minimum X", bounds.XMin);
            Number("bounds-min-y", "Minimum Y", bounds.YMin);
            Number("bounds-min-z", "Minimum Z", bounds.ZMin);
            Number("bounds-max-x", "Maximum X", bounds.XMax);
            Number("bounds-max-y", "Maximum Y", bounds.YMax);
            Number("bounds-max-z", "Maximum Z", bounds.ZMax);
        }
        if (element.SlabGeometry is { } slab)
        {
            Number("slab-outline-area", "Saved slab outline area", slab.OutlineArea, SceneUnits.SquareMetre);
            Number("slab-top-area", "Slab top area with openings", slab.TopAreaWithOpenings, SceneUnits.SquareMetre);
            Number("slab-bottom-area", "Slab bottom area with openings", slab.BottomAreaWithOpenings, SceneUnits.SquareMetre);
            Number("slab-perimeter", "Saved slab perimeter", slab.FloorPlanPerimeter);
            Number("slab-hole-perimeter", "Saved slab hole perimeter", slab.FloorPlanHolesPerimeter);
            Number("slab-net-volume", "Slab net volume", slab.Volume, SceneUnits.CubicMetre);
        }
        Number("wall-net-volume", "Wall net volume", element.WallExtrusion?.Volume, SceneUnits.CubicMetre);
        if (element.ColumnExtrusion is { CircularRadius: not null } cylinder)
        {
            Number("column-geometric-volume", "Column geometric volume", cylinder.Volume, SceneUnits.CubicMetre);
            Number("column-net-volume", "Column net volume", cylinder.ScheduledVolume, SceneUnits.CubicMetre);
        }
        else
            Number("column-net-volume", "Column net volume", element.ColumnExtrusion?.Volume ?? element.ColumnCutMesh?.Volume, SceneUnits.CubicMetre);
        Number("beam-net-volume", "Beam net volume", element.BeamNetVolume, SceneUnits.CubicMetre);
        Number("beam-reference-length", "Beam reference axis length", element.BeamReferenceAxis?.Length);
        Number("beam-inclination", "Beam inclination", element.BeamReferenceAxis?.InclinationRadians, SceneUnits.Radian);
        Number("opening-cut-volume", "Opening cut volume", element.OpeningGeometry?.CutVolume, SceneUnits.CubicMetre);
        return properties;

        void Add(string key, string name, SceneValue value) =>
            properties.Add(new SceneProperty { Key = "archicad:" + key, Name = name, Value = value });
        void Text(string key, string name, string? value)
        {
            if (value is not null) Add(key, name, SceneValue.FromText(value));
        }
        void Integer(string key, string name, int? value)
        {
            if (value is int n) Add(key, name, IntegerValue(n));
        }
        void Number(string key, string name, double? value, string unit = SceneUnits.Metre)
        {
            if (value is double n && double.IsFinite(n)) Add(key, name, NumberValue(n, unit));
        }
    }

    private static SceneValue? ToSceneValue(PlaPropertyDefinition definition, PlaPropertyValue? saved)
    {
        if (saved is not { Status: "normal" }) return null;
        return (definition.DataType, saved.Value) switch
        {
            ("string", string text) => SceneValue.FromText(text),
            ("integer", int number) => IntegerValue(number),
            ("number", double number) when double.IsFinite(number) => NumberValue(number, null),
            ("length", double number) when double.IsFinite(number) => NumberValue(number, SceneUnits.Metre),
            ("area", double number) when double.IsFinite(number) => NumberValue(number, SceneUnits.SquareMetre),
            ("volume", double number) when double.IsFinite(number) => NumberValue(number, SceneUnits.CubicMetre),
            ("singleEnum", Guid id) when definition.Choices?.FirstOrDefault(choice => choice.Guid == id) is { } choice =>
                SceneValue.FromText(choice.DisplayValue),
            _ => null,
        };
    }

    private static SceneValue IntegerValue(int value) =>
        SceneValue.FromInteger(value, value.ToString(CultureInfo.InvariantCulture));

    private static SceneValue NumberValue(double value, string? unit)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        return SceneValue.FromNumber(value, unit, unit is null ? text : $"{text} {unit}");
    }
}
