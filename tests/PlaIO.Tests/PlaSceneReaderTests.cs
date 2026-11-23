using IOScene;
using PlaIO.Scene;
using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

/// <summary>The tests of <see cref="PlaSceneReader"/> on synthetic containers. The identity tests are golden: the model
/// key derivation is what the consumers of a scene store together with the source ids of the elements, so a change of
/// these literals is a breaking release of PlaIO.Scene.</summary>
public class PlaSceneReaderTests
{
    [Fact]
    public void OneModelWithTheFileNameAsItsKey()
    {
        using var scope = TempContainer("sample.pla");

        var result = PlaSceneReader.Read(scope.Path);
        var document = result.Document;
        var model = Assert.Single(document.Models);

        // The frozen identity derivation: the file name with its extension, without its directory.
        Assert.Equal("sample.pla", model.Key);
        Assert.Equal("sample.pla", model.Name);
        Assert.Equal("pla", model.SourceFormat);
        Assert.Equal(1, model.Id);
        Assert.StartsWith("PlaIO.Scene ", model.Producer);
        Assert.Equal("sample.pla", document.Title);

        // This fixture contains only a container header and an unrelated block.
        Assert.Empty(document.Elements);
        Assert.Empty(document.Geometries);
        Assert.Empty(document.Instances);
        Assert.Empty(document.PropertySets);
        Assert.Empty(document.Validate());

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(SceneSeverity.Info, diagnostic.Severity);
        Assert.Equal("container-only", diagnostic.Code);
    }

    [Fact]
    public void PlnExtensionIsTheSourceFormat()
    {
        using var scope = TempContainer("project.pln");

        var model = Assert.Single(PlaSceneReader.Read(scope.Path).Document.Models);

        Assert.Equal("project.pln", model.Key);
        Assert.Equal("pln", model.SourceFormat);
    }

    [Fact]
    public void AnotherExtensionWritesNoSourceFormat()
    {
        using var scope = TempContainer("container.dat");

        var model = Assert.Single(PlaSceneReader.Read(scope.Path).Document.Models);

        Assert.Equal("container.dat", model.Key);
        Assert.Null(model.SourceFormat);
    }

    [Fact]
    public void ModelKeyOptionReplacesTheFileName()
    {
        using var scope = TempContainer("sample.pla");

        var model = Assert.Single(PlaSceneReader.Read(scope.Path, new PlaSceneOptions { ModelKey = "campus" }).Document.Models);

        Assert.Equal("campus", model.Key);
        Assert.Equal("campus", model.Name);
    }

    [Fact]
    public void AnEmptyPathThrows()
    {
        Assert.Throws<ArgumentException>(() => PlaSceneReader.Read(""));
    }

    [Fact]
    public void AnEmptyModelKeyThrows()
    {
        using var scope = TempContainer("sample.pla");

        Assert.Throws<ArgumentException>(() => PlaSceneReader.Read(scope.Path, new PlaSceneOptions { ModelKey = "" }));
    }

    [Fact]
    public void RejectsAMalformedClassTable()
    {
        byte[] file = Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Raw, new byte[18], ver: 1, kind: 0xFDE9));
        using var scope = TempContainer("malformed.pln", file);
        Assert.Throws<InvalidDataException>(() => PlaSceneReader.Read(scope.Path));
    }

    [Theory]
    [InlineData("shortened line", false)]
    [InlineData("hidden arc", true)]
    public void GridOnlyScenePreservesFullAxesAndProjectElevations(string variant, bool curved)
    {
        var (file, id) = ElementReaderTests.GridAxisFixture(variant, withStory: true);
        using var scope = TempContainer("grids.pln", file);
        using var source = PlaDocument.Open(scope.Path);
        var axis = Assert.Single(source.ReadGridAxes());
        Assert.Equal(id, axis.Guid);
        Assert.Equal(-2, axis.HomeStoryIndex);
        Assert.Equal(2.5, axis.ElevationToHomeStory);
        Assert.Equal(-1.5, axis.ElevationToProjectZero);

        var result = PlaSceneReader.Read(scope.Path);
        Assert.Empty(result.Diagnostics);
        var scene = SceneJson.Read(SceneJson.Write(result.Document));
        Assert.Empty(scene.Validate());
        Assert.Empty(scene.Elements);
        var grid = Assert.Single(scene.Grids);
        Assert.Equal(Assert.Single(scene.Models).Id, grid.ModelId);
        const double z = -1.5;
        if (curved)
        {
            Assert.Empty(grid.Lines);
            var path = Assert.Single(grid.Paths!);
            Assert.Equal(axis.Label, path.Name);
            Assert.Equal(new Vector3d(axis.Start.X, axis.Start.Y, z), path.Points[0]);
            Assert.Equal(new Vector3d(axis.End.X, axis.End.Y, z), path.Points[^1]);
            Assert.All(path.Points, point => Assert.Equal(z, point.Z));
            Assert.True(path.Points.Count > 2);
            foreach (var (first, second) in path.Points.Zip(path.Points.Skip(1)))
            {
                double midpointRadius = double.Hypot((first.X + second.X) / 2 + 2, (first.Y + second.Y) / 2 - 3);
                Assert.InRange(2.75 - midpointRadius, 0, 0.001);
            }
        }
        else
        {
            Assert.Null(grid.Paths);
            var line = Assert.Single(grid.Lines);
            Assert.Equal(axis.Label, line.Name);
            Assert.Equal(z, line.Elevation);
            Assert.Equal(new Vector3d(axis.Start.X, axis.Start.Y, z), line.Start);
            Assert.Equal(new Vector3d(axis.End.X, axis.End.Y, z), line.End);
        }
    }

    [Theory]
    [InlineData("verified", false, 2.5, null, "grid-elevation-unavailable")]
    [InlineData("nonfinite elevation", true, null, null, "grid-elevation-unavailable")]
    [InlineData("excessive samples", true, 2.5, -1.5, "grid-tessellation-unavailable")]
    public void ReportsGridAxesThatCannotBePlacedInScene(string variant, bool withStory, double? offset,
        double? elevation, string diagnosticCode)
    {
        var (file, _) = ElementReaderTests.GridAxisFixture(variant, withStory);
        using var scope = TempContainer("grids.pln", file);
        using var source = PlaDocument.Open(scope.Path);
        var axis = Assert.Single(source.ReadGridAxes());
        Assert.Equal(offset, axis.ElevationToHomeStory);
        Assert.Equal(elevation, axis.ElevationToProjectZero);

        var result = PlaSceneReader.Read(scope.Path);
        Assert.Empty(result.Document.Grids);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(SceneSeverity.Info, diagnostic.Severity);
        Assert.Equal(diagnosticCode, diagnostic.Code);
    }

    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, false)]
    [InlineData(false, 1, false)]
    public void MapsOnlyVerifiedDoorPlacement(bool matchingHost, int placementCase, bool hasPlacement)
    {
        var fixture = ElementReaderTests.HostedElementsFixture(matchingHost, reciprocal: 1, invalidDimensions: 0, placementCase);
        using var scope = TempContainer("door.pln", fixture.File);

        var scene = SceneJson.Read(SceneJson.Write(PlaSceneReader.Read(scope.Path).Document));
        Assert.Empty(scene.Validate());
        var door = Assert.Single(scene.Elements, element => element.SourceId == fixture.Door.ToString("D"));
        var wall = Assert.Single(scene.Elements, element => element.SourceId == fixture.Wall.ToString("D"));
        Assert.Equal(matchingHost ? wall.Id : (long?)null, door.ParentId);
        var properties = Assert.Single(scene.PropertySets, set => set.Id == Assert.Single(door.PropertySetIds)).Properties;
        (string Key, double Number)[] expected =
        [
            ("door-reference-x", 3.25),
            ("door-reference-y", 5),
            ("door-distance-along-wall", 1.25),
            ("door-sill-to-project-zero", -3.65),
            ("door-sill-to-host-story", 0.35),
        ];
        foreach (var (key, number) in expected)
        {
            var values = properties.Where(property => property.Key == "archicad:" + key);
            if (hasPlacement)
            {
                var value = Assert.Single(values).Value;
                Assert.Equal(number, value.Number);
                Assert.Equal(SceneUnits.Metre, value.Unit);
            }
            else
                Assert.Empty(values);
        }
    }

    [Theory]
    [InlineData(0.25, 4, false, 3.5)]
    [InlineData(0.25, 4, true, 3.5)]
    [InlineData(-0.25, 4, false, 3.625)]
    [InlineData(0.25, 2.5, false, 3.75)]
    public void BuildsClosedWallsAroundThroughDoorCuts(double sill, double secondStation, bool reverse, double expectedVolume)
    {
        var (file, wallId) = ElementReaderTests.WallWithDoorCutsFixture(sill, secondStation, reverse);
        using var scope = TempContainer("wall.pln", file);
        using var doc = PlaDocument.Open(scope.Path);
        var elements = doc.ReadElements();
        var element = Assert.Single(elements, e => e.Guid == wallId);
        Assert.Equal(new PlaBoundingBox(0, 1.8125, -3.75, 6, 2.0625, -0.75), element.BoundingBox);
        Assert.All(elements.Where(e => e.Type == "Door"), door =>
        {
            Assert.Equal(wallId, door.HostGuid);
            Assert.NotNull(door.DoorPlacement);
            Assert.Equal(new PlaDoorWallHoleDimensions(1, 2), door.DoorWallHoleDimensions);
        });
        var solid = Assert.IsType<PlaWallExtrusion>(element.WallExtrusion);
        Assert.Equal(expectedVolume, solid.Volume, 12);
        Assert.Equal((-3.75, -0.75, 0.25, 3.25), (
            element.BottomElevationToProjectZero, element.TopElevationToProjectZero,
            element.BottomElevationToHomeStory, element.TopElevationToHomeStory));

        var scene = SceneJson.Read(SceneJson.Write(PlaSceneReader.Read(scope.Path).Document));
        Assert.Empty(scene.Validate());
        var wall = Assert.Single(scene.Elements, e => e.SourceId == wallId.ToString("D"));
        var instance = Assert.Single(scene.Instances);
        Assert.Equal(wall.Id, instance.ElementId);
        Assert.Equal(expectedVolume, MeasureClosedMesh(Assert.Single(scene.Geometries), measureHorizontalCaps: false).Volume, 12);
        Assert.Equal(new Vector3d(0, 1.8125, -3.75), scene.ComputeBounds().Min);
        Assert.Equal(new Vector3d(6, 2.0625, -0.75), scene.ComputeBounds().Max);
    }

    [Theory]
    [InlineData(0.125, false)]
    [InlineData(0, true)]
    public void WithholdsDoorCutWallMeshesWhenTheCutOrAnotherRelationshipIsUnsupported(double oversize, bool unsupportedRelation)
    {
        var (file, wallId) = ElementReaderTests.WallWithDoorCutsFixture(0.25, 4, reverse: false, oversize, unsupportedRelation);
        using var scope = TempContainer("wall.pln", file);
        using var doc = PlaDocument.Open(scope.Path);
        var element = Assert.Single(doc.ReadElements(), e => e.Guid == wallId);
        Assert.Null(element.WallExtrusion);
        Assert.Null(element.BoundingBox);
        Assert.Null(element.BottomElevationToProjectZero);
        Assert.Null(element.TopElevationToHomeStory);
        Assert.Empty(PlaSceneReader.Read(scope.Path).Document.Instances);
    }

    [Fact]
    public void MapsTheVerifiedWallTopWhenDoorCutsCoverTheWholeBottom()
    {
        var (file, wallId) = ElementReaderTests.WallWithDoorCutsFixture(0, 4.5, reverse: false, width: 3, firstStation: 1.5);
        using var scope = TempContainer("wall.pln", file);
        var scene = SceneJson.Read(SceneJson.Write(PlaSceneReader.Read(scope.Path).Document));
        Assert.Empty(scene.Validate());
        Assert.Empty(scene.Instances);

        var wall = Assert.Single(scene.Elements, e => e.SourceId == wallId.ToString("D"));
        var properties = scene.PropertySets.Where(set => wall.PropertySetIds.Contains(set.Id)).SelectMany(set => set.Properties).ToArray();
        Assert.DoesNotContain(properties, p => p.Key is "archicad:bottom-to-project-zero" or "archicad:bottom-to-home-story");
        var topProject = Assert.Single(properties, p => p.Key == "archicad:top-to-project-zero").Value;
        var topStory = Assert.Single(properties, p => p.Key == "archicad:top-to-home-story").Value;
        Assert.Equal((-0.75, SceneUnits.Metre), (topProject.Number, topProject.Unit));
        Assert.Equal((3.25, SceneUnits.Metre), (topStory.Number, topStory.Unit));
    }

    [Theory]
    [InlineData(false, 4.0)]
    [InlineData(true, 3.9375)]
    public void MapsLevelsPropertiesAndAClosedSlabMesh(bool concave, double volume)
    {
        var fixture = ElementReaderTests.SlabSceneFixture(concave, beveled: false);
        using var scope = TempContainer("floor.pln", fixture.File);

        var result = PlaSceneReader.Read(scope.Path);
        var scene = SceneJson.Read(SceneJson.Write(result.Document));
        Assert.Empty(result.Diagnostics);
        Assert.Empty(scene.Validate());
        var element = Assert.Single(scene.Elements);
        Assert.Equal(fixture.Element.ToString("D"), element.SourceId);
        Assert.Equal("Slab", element.Category);
        Assert.Equal("S", element.Name);
        Assert.Equal(2, scene.Levels.Count);
        var level = Assert.Single(scene.Levels, level => level.Id == element.LevelId);
        Assert.Equal("Подвал", level.Name);
        Assert.Equal(-4, level.Elevation);
        var properties = Assert.Single(scene.PropertySets);
        Assert.Equal(properties.Id, Assert.Single(element.PropertySetIds));
        Assert.Equal("archicad:instance", properties.Key);
        Assert.Equal(-1, Assert.Single(properties.Properties, p => p.Key == "archicad:home-story-index").Value.Integer);
        var netVolume = Assert.Single(properties.Properties, p => p.Key == "archicad:slab-net-volume").Value;
        Assert.Equal(SceneUnits.CubicMetre, netVolume.Unit);
        Assert.Equal(volume, netVolume.Number);
        var mesh = Assert.Single(scene.Geometries);
        var instance = Assert.Single(scene.Instances);
        Assert.Equal(element.Id, instance.ElementId);
        Assert.Equal(mesh.Id, instance.GeometryId);
        Assert.Equal(concave ? 20 : 12, mesh.Indices.Count / 3);
        Assert.Equal(new Vector3d(-1, -1, -4.5), scene.ComputeBounds().Min);
        Assert.Equal(new Vector3d(1, 1, -3.5), scene.ComputeBounds().Max);

        var (measuredVolume, top, bottom) = MeasureClosedMesh(mesh);
        Assert.Equal(volume, measuredVolume, 12);
        Assert.Equal(volume, top, 12);
        Assert.Equal(volume, bottom, 12);
    }

    [Fact]
    public void TessellatesTheConcaveSlabAroundBothSavedHoles()
    {
        var fixture = ElementReaderTests.SlabWithHolesSceneFixture();
        using var scope = TempContainer("holes.pln", fixture.File);
        var result = PlaSceneReader.Read(scope.Path);
        Assert.Empty(result.Diagnostics);
        var mesh = Assert.Single(result.Document.Geometries);
        Assert.Equal(60, mesh.Indices.Count / 3);
        Assert.Equal((10, 10, 10), MeasureClosedMesh(mesh));
        Assert.Empty(result.Document.Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportsTheRotatedWallPrismOnlyWhenItsConnectionsAreVerified(bool joined)
    {
        var (file, wallId) = ElementReaderTests.WallReferenceSceneFixture(0, flipped: false, 0, joined, inconsistentContour: false);
        using var scope = TempContainer("wall.pln", file);
        var result = PlaSceneReader.Read(scope.Path);
        var element = Assert.Single(result.Document.Elements);
        Assert.Equal(wallId.ToString("D"), element.SourceId);
        if (joined)
        {
            Assert.Empty(result.Document.Instances);
            Assert.Empty(result.Document.Geometries);
            Assert.Equal("geometry-incomplete", Assert.Single(result.Diagnostics).Code);
            return;
        }

        var mesh = Assert.Single(result.Document.Geometries);
        var instance = Assert.Single(result.Document.Instances);
        Assert.Equal(element.Id, instance.ElementId);
        Assert.Equal((3.75, 0.75, 0.75), MeasureClosedMesh(mesh));
        var bounds = result.Document.ComputeBounds();
        Assert.Equal(new Vector3d(1, -3, -3.75), bounds.Min);
        Assert.Equal(new Vector3d(4.2, 1.15, -0.75), bounds.Max);
        var volume = Assert.Single(Assert.Single(result.Document.PropertySets).Properties, p => p.Key == "archicad:wall-net-volume");
        Assert.Equal(SceneUnits.CubicMetre, volume.Value.Unit);
        Assert.Equal(3.75, volume.Value.Number);
        Assert.Empty(result.Document.Validate());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RetainsTheWallSolidWhenItsZoneCalculationIdentifiesTheSameWall(bool valid)
    {
        var (file, wallId) = ElementReaderTests.WallReferenceSceneFixture(0, flipped: false, 0,
            joined: false, inconsistentContour: false, validZoneCalculation: valid);
        using var scope = TempContainer("zone-boundary.pln", file);
        var scene = PlaSceneReader.Read(scope.Path).Document;
        var wall = Assert.Single(scene.Elements, e => e.SourceId == wallId.ToString("D"));
        if (!valid)
        {
            Assert.Empty(scene.Instances);
            return;
        }
        Assert.Equal(wall.Id, Assert.Single(scene.Instances).ElementId);
        Assert.Equal((3.75, 0.75, 0.75), MeasureClosedMesh(Assert.Single(scene.Geometries)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JoinsTheProfileFillsAlongTheirSharedEdgeAndKeepsTheHole(bool overlapping)
    {
        var (file, wallId) = ElementReaderTests.ProfiledWallSceneFixture(overlapping);
        using var scope = TempContainer("profiled-wall.pla", file);
        using var document = PlaDocument.Open(scope.Path);
        var wall = Assert.Single(document.ReadElements(), e => e.Guid == wallId);
        Assert.NotNull(wall.WallProfile);
        var scene = PlaSceneReader.Read(scope.Path).Document;
        Assert.Empty(scene.Validate());
        if (overlapping)
        {
            Assert.Null(wall.WallExtrusion);
            Assert.Empty(scene.Instances);
            return;
        }

        var extrusion = Assert.IsType<PlaWallExtrusion>(wall.WallExtrusion);
        Assert.Equal(2, Assert.Single(extrusion.Sections).Rings.Count);
        Assert.Equal(24, extrusion.Volume);
        Assert.Equal((24, 12, 12), MeasureClosedMesh(Assert.Single(scene.Geometries)));
        Assert.Equal(new Vector3d(-1, -5, -3.75), scene.ComputeBounds().Min);
        Assert.Equal(new Vector3d(1, 0, -0.75), scene.ComputeBounds().Max);
    }

    [Theory]
    [InlineData(0, 2, 3.75, null, false)]
    [InlineData(0, 2, 3.75, null, true)]
    [InlineData(4, 2, 3.75, 0.9375, false)]
    [InlineData(5, 0, null, null, false)]
    [InlineData(5, 0, null, null, true)]
    [InlineData(7, 2, 3.75, 1.0, false)]
    [InlineData(9, 0, null, null, false)]
    [InlineData(10, 1, 3.75, null, false)]
    public void MeshesTheVerifiedSlabAroundASeparateThroughOpening(int variation, int rings, double? volume,
        double? secondaryVolume, bool slabAlignedFrame)
    {
        var (file, opening, slab, _, secondary) = ElementReaderTests.SlabOpeningFixture(variation, rectangular: true,
            slabAlignedFrame: slabAlignedFrame);
        using var scope = TempContainer("aperture.pln", file);
        using var document = PlaDocument.Open(scope.Path);
        var elements = document.ReadElements();
        var geometry = Assert.IsType<PlaSlabGeometry>(Assert.Single(elements, e => e.Guid == slab).SlabGeometry);
        Assert.Single(geometry.Rings);
        Assert.Equal(-4.5, geometry.Bottom);
        Assert.Equal(-3.5, geometry.Top);
        Assert.False(geometry.IsUncutVerticalPrism);
        if (rings == 0) Assert.Null(geometry.VerticalPrisms);
        else Assert.Equal(rings, Assert.Single(geometry.VerticalPrisms!).Rings.Count);
        if (slabAlignedFrame && rings != 0)
        {
            var aperture = Assert.IsType<PlaOpeningGeometry>(Assert.Single(elements, e => e.Guid == opening).OpeningGeometry);
            Assert.Equal(new PlaPlanPoint(0.25, -0.375), aperture.Origin);
            Assert.Equal(-0.625, Assert.Single(aperture.Rings!).Min(p => p.Y));
            Assert.Equal(-0.125, Assert.Single(aperture.Rings!).Max(p => p.Y));
        }

        var scene = PlaSceneReader.Read(scope.Path).Document;
        Assert.Empty(scene.Validate());
        Assert.Equal((volume.HasValue ? 1 : 0) + (secondaryVolume.HasValue ? 1 : 0), scene.Instances.Count);
        CheckSlabMesh(slab, volume, 1);
        if (secondaryVolume is not null) CheckSlabMesh(secondary, secondaryVolume, 0.25);

        void CheckSlabMesh(Guid sourceId, double? expected, double height)
        {
            var element = Assert.Single(scene.Elements, e => e.SourceId == sourceId.ToString("D"));
            if (expected is not { } value)
            {
                Assert.DoesNotContain(scene.Instances, i => i.ElementId == element.Id);
                return;
            }
            var instance = Assert.Single(scene.Instances, i => i.ElementId == element.Id);
            var mesh = Assert.Single(scene.Geometries, m => m.Id == instance.GeometryId);
            var measured = MeasureClosedMesh(mesh);
            Assert.Equal(value, measured.Volume, 12);
            Assert.Equal(value / height, measured.Top, 12);
            Assert.Equal(value / height, measured.Bottom, 12);
        }
    }

    [Theory]
    [InlineData((int)ElementReaderTests.SlabColumnContact.PartialInterior, 0, 0, 3.90625, 0)]
    [InlineData((int)ElementReaderTests.SlabColumnContact.Interior, 1, 2, 3.875, 1)]
    [InlineData((int)ElementReaderTests.SlabColumnContact.Edge, 1, 1, 3.9375, 1)]
    [InlineData((int)ElementReaderTests.SlabColumnContact.Split, 2, 1, 3.5, 1)]
    [InlineData((int)ElementReaderTests.SlabColumnContact.TrimOuterEdge, 1, 1, 3.75, 0.875)]
    public void MapsTheEvaluatedSlabWhenTheColumnCutsEverySkin(int contact, int parts, int ringsPerPart, double volume,
        double maxX)
    {
        var (file, slabId) = ElementReaderTests.SlabColumnSceneFixture(contact: (ElementReaderTests.SlabColumnContact)contact);
        using var scope = TempContainer("column-cut.pla", file);
        using var document = PlaDocument.Open(scope.Path);
        var slab = Assert.IsType<PlaSlabGeometry>(Assert.Single(document.ReadElements(), e => e.Guid == slabId).SlabGeometry);
        Assert.Single(slab.Rings);
        Assert.Equal(1, slab.Rings[0].Max(p => p.X));
        Assert.False(slab.IsUncutVerticalPrism);
        Assert.Equal(volume, Assert.IsType<double>(slab.Volume), 12);
        if (parts == 0) Assert.Null(slab.VerticalPrisms);
        else
        {
            Assert.NotNull(slab.VerticalPrisms);
            Assert.Equal(parts, slab.VerticalPrisms.Count);
            Assert.All(slab.VerticalPrisms, prism => Assert.Equal(ringsPerPart, prism.Rings.Count));
        }
        var result = PlaSceneReader.Read(scope.Path);
        if (parts == 0) Assert.Equal("geometry-incomplete", Assert.Single(result.Diagnostics).Code);
        else Assert.Empty(result.Diagnostics);
        var scene = result.Document;
        var element = Assert.Single(scene.Elements, e => e.Category == "Slab");
        var slabInstances = scene.Instances.Where(i => i.ElementId == element.Id).ToArray();
        Assert.Equal(parts, slabInstances.Length);
        Assert.Equal(parts + 1, scene.Geometries.Count);
        var measured = slabInstances.Select(i => MeasureClosedMesh(scene.Geometries.Single(g => g.Id == i.GeometryId))).ToArray();
        if (parts != 0)
        {
            Assert.Equal(volume, measured.Sum(m => m.Volume), 12);
            Assert.Equal(volume, measured.Sum(m => m.Top), 12);
            Assert.Equal(volume, measured.Sum(m => m.Bottom), 12);
            Assert.Equal(maxX, slabInstances.Max(i => scene.Geometries.Single(g => g.Id == i.GeometryId).Vertices
                .Max(v => (i.Transform ?? Transform3d.Identity).Apply(v).X)), 12);
        }
        var column = Assert.Single(document.ReadElements(), e => e.Type == "Column");
        var solid = Assert.IsType<PlaColumnExtrusion>(column.ColumnExtrusion);
        var sceneColumn = Assert.Single(scene.Elements, e => e.Category == "Column");
        var instance = Assert.Single(scene.Instances, i => i.ElementId == sceneColumn.Id);
        var mesh = Assert.Single(scene.Geometries, g => g.Id == instance.GeometryId);
        var transform = Assert.IsType<Transform3d>(instance.Transform);
        Assert.Equal(solid.Volume, MeasureClosedMesh(mesh).Volume, 12);
        Assert.Equal(solid.BoundingBox.ZMin, mesh.Vertices.Min(v => transform.Apply(v).Z), 12);
        Assert.Equal(solid.BoundingBox.XMax, mesh.Vertices.Max(v => transform.Apply(v).X), 12);
        Assert.Contains(scene.PropertySets.Where(p => sceneColumn.PropertySetIds.Contains(p.Id)).SelectMany(p => p.Properties),
            p => p.Key == "archicad:column-net-volume" && p.Value.Number == solid.Volume);
        Assert.Empty(scene.Validate());
    }

    [Fact]
    public void MapsASlopedColumnCapInProjectCoordinates()
    {
        var (file, _) = ElementReaderTests.SlabColumnSceneFixture(contact: ElementReaderTests.SlabColumnContact.Split,
            slopeUpperCap: true);
        using var scope = TempContainer("sloped-cap.pln", file);
        var scene = PlaSceneReader.Read(scope.Path).Document;
        var column = Assert.Single(scene.Elements, e => e.Category == "Column");
        var instance = Assert.Single(scene.Instances, i => i.ElementId == column.Id);
        var mesh = Assert.Single(scene.Geometries, g => g.Id == instance.GeometryId);
        var frame = Assert.IsType<Transform3d>(instance.Transform);
        var vertices = mesh.Vertices.Select(frame.Apply).ToArray();
        Assert.Equal(-5, vertices.Min(v => v.Z));
        Assert.Equal(-2.9375, vertices.Max(v => v.Z));
        foreach (var point in vertices.Where(v => v.Z > -5))
            Assert.Equal(-3 - 0.5 * point.Y, point.Z, 12);
        Assert.Equal(1, MeasureClosedMesh(mesh).Volume, 12);
        Assert.Empty(scene.Validate());
    }

    [Theory]
    [InlineData(true, 900, false, 0.25, 36)]
    [InlineData(true, 900, false, 0.4, 48)]
    [InlineData(true, 900, false, 0.02, 12)]
    [InlineData(false, 900, false, 0.25, 36)]
    [InlineData(true, 1200, false, 0.25, 36)]
    [InlineData(true, 900, true, 0.25, 36)]
    public void MapsAValidatedCircularColumn(bool validArcs, int slabPriority, bool wrongOwner, double radius, int listedSides)
    {
        var (file, _) = ElementReaderTests.SlabColumnSceneFixture(slabPriority, wrongOwner, validCircularSection: validArcs,
            circularRadius: radius);
        using var scope = TempContainer("circular-column.pln", file);
        using var document = PlaDocument.Open(scope.Path);
        var element = Assert.Single(document.ReadElements(), e => e.Type == "Column");
        var scene = PlaSceneReader.Read(scope.Path).Document;
        var column = Assert.Single(scene.Elements, e => e.SourceId == element.Guid.ToString("D"));
        if (!validArcs || slabPriority > 1000 || wrongOwner)
        {
            Assert.Null(element.ColumnExtrusion);
            Assert.DoesNotContain(scene.Instances, i => i.ElementId == column.Id);
            return;
        }

        var cylinder = Assert.IsType<PlaColumnExtrusion>(element.ColumnExtrusion);
        Assert.Equal(radius, cylinder.CircularRadius);
        double area = Math.PI * radius * radius;
        Assert.Equal(area * 1.25, cylinder.Volume);
        double listedArea = listedSides * (radius * radius) / 2 * Math.Sin(Math.Tau / listedSides);
        Assert.Equal(listedArea, cylinder.ScheduledCapArea!.Value, 14);
        double listedPerimeter = 2 * listedSides * radius * Math.Sin(Math.PI / listedSides);
        Assert.Equal(listedPerimeter * 1.25, cylinder.ScheduledLateralArea!.Value, 14);
        Assert.Equal(listedArea * 1.25, cylinder.ScheduledVolume!.Value, 14);
        var properties = scene.PropertySets.Where(set => column.PropertySetIds.Contains(set.Id)).SelectMany(set => set.Properties).ToArray();
        Assert.Equal(cylinder.Volume, Assert.Single(properties, p => p.Key == "archicad:column-geometric-volume").Value.Number);
        Assert.Equal(cylinder.ScheduledVolume, Assert.Single(properties, p => p.Key == "archicad:column-net-volume").Value.Number);
        Assert.Equal((-0.25, 1, -4.25, -3), (element.BottomElevationToHomeStory, element.TopElevationToHomeStory,
            element.BottomElevationToProjectZero, element.TopElevationToProjectZero));
        var instance = Assert.Single(scene.Instances, i => i.ElementId == column.Id);
        var mesh = Assert.Single(scene.Geometries, g => g.Id == instance.GeometryId);
        var frame = Assert.IsType<Transform3d>(instance.Transform);
        var vertices = mesh.Vertices.Select(frame.Apply).ToArray();
        Assert.Equal(new PlaBoundingBox(1 - radius, -radius, -4.25, 1 + radius, radius, -3), cylinder.BoundingBox);
        Assert.Equal(1 - radius, vertices.Min(p => p.X), 14);
        Assert.Equal(1 + radius, vertices.Max(p => p.X), 14);
        Assert.Equal(-radius, vertices.Min(p => p.Y), 14);
        Assert.Equal(radius, vertices.Max(p => p.Y), 14);
        Assert.Equal((-4.25, -3), (vertices.Min(p => p.Z), vertices.Max(p => p.Z)));
        var (volume, top, bottom) = MeasureClosedMesh(mesh);
        double perimeter = Math.Tau * radius, surface = perimeter * 1.25 + 2 * area;
        Assert.InRange(volume, cylinder.Volume - 1e-7 * surface, cylinder.Volume);
        Assert.InRange(top, area - 1e-7 * perimeter, area);
        Assert.Equal(top, bottom, 12);
        var ring = vertices.Take(vertices.Length / 2).ToArray();
        for (int i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            double midpointRadius = double.Hypot((a.X + b.X) / 2 - cylinder.Origin.X, (a.Y + b.Y) / 2 - cylinder.Origin.Y);
            Assert.InRange(radius - midpointRadius, 0, 1e-7);
        }
        Assert.Empty(scene.Validate());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SeparatesFullColumnSpansAfterBeamCuts(bool otherColumn, bool wrongOtherOwner)
    {
        using var scope = TempContainer("column-spans.pln", ElementReaderTests.ColumnBeamBandsFixture(false, otherColumn, wrongOtherOwner));
        using var document = PlaDocument.Open(scope.Path);
        var column = Assert.Single(document.ReadElements(), e => e.Type == "Column" && e.MemberProfile is not null);
        var scene = PlaSceneReader.Read(scope.Path).Document;
        var element = Assert.Single(scene.Elements, e => e.SourceId == column.Guid.ToString("D"));
        var instances = scene.Instances.Where(i => i.ElementId == element.Id).ToArray();
        if (wrongOtherOwner)
        {
            Assert.Null(column.ColumnExtrusion);
            Assert.Null(column.ColumnCutMesh);
            Assert.Empty(instances);
            return;
        }

        var solid = Assert.IsType<PlaColumnExtrusion>(column.ColumnExtrusion);
        Assert.Equal(new PlaColumnSpan[] { new(-3.25, -2.5), new(-1.5, 0) }, solid.Spans);
        Assert.Equal((0.75, 4, -3.25, 0), (column.BottomElevationToHomeStory, column.TopElevationToHomeStory,
            column.BottomElevationToProjectZero, column.TopElevationToProjectZero));
        Assert.Equal(0.5625, solid.Volume);
        Assert.Equal(2, instances.Length);
        double volume = 0;
        foreach (var (instance, span) in instances.Zip(solid.Spans!))
        {
            var mesh = Assert.Single(scene.Geometries, g => g.Id == instance.GeometryId);
            var frame = Assert.IsType<Transform3d>(instance.Transform);
            var heights = mesh.Vertices.Select(v => frame.Apply(v).Z).ToArray();
            Assert.Equal((span.Bottom, span.Top), (heights.Min(), heights.Max()));
            volume += MeasureClosedMesh(mesh).Volume;
        }
        Assert.Equal(solid.Volume, volume, 12);
        Assert.Empty(scene.Validate());
    }

    [Fact]
    public void KeepsAProfiledColumnWhenVerifiedBeamSweepsMissIt()
    {
        using var scope = TempContainer("column-clear-beams.pln", ElementReaderTests.ColumnBeamBandsFixture(
            partialCut: false, otherColumn: false, wrongOtherOwner: false, clearBeams: true));
        using var document = PlaDocument.Open(scope.Path);
        var column = Assert.Single(document.ReadElements(), e => e.Type == "Column");
        var solid = Assert.IsType<PlaColumnExtrusion>(column.ColumnExtrusion);
        Assert.Null(solid.Spans);
        Assert.Equal(new PlaBoundingBox(-0.25, -0.25, -4, 0.25, 0.25, 0), solid.BoundingBox);
        Assert.Equal(1, solid.Volume);
    }

    [Theory]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.EqualTee, 0.5625)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.WeakerTee, 0.625)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.RepeatedWeakerTee, 0.625)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.DifferentHeightTee, null)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.TrimmedThroughTee, null)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.AngledTee, 0.5625)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.AngledTeeNearCap, 0.375)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.AngledWeakerTee, 0.626923525400276)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.MiterAway, 0.5625)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.MiterAtFace, null)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.EqualMiter, 0.5625)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.EqualMiterWithWeakPeer, 0.5625)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.EqualMiterWithDifferentWeakPeer, null)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.WeakerOverlap, 0.5625)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.SeparateEqualOverlap, 0.5625)]
    [InlineData((int)ElementReaderTests.ColumnBeamEnd.EqualOverlap, null)]
    public void CutsAProfiledColumnWithConnectedRectangularBeams(int end, double? volume)
    {
        using var scope = TempContainer("column-beam-junction.pln", ElementReaderTests.ColumnBeamBandsFixture(
            partialCut: false, otherColumn: false, wrongOtherOwner: false, beamEnd: (ElementReaderTests.ColumnBeamEnd)end));
        using var document = PlaDocument.Open(scope.Path);
        var column = Assert.Single(document.ReadElements(), e => e.Type == "Column");
        var scene = PlaSceneReader.Read(scope.Path).Document;
        var owner = Assert.Single(scene.Elements, e => e.SourceId == column.Guid.ToString("D"));
        var meshes = scene.Instances.Where(i => i.ElementId == owner.Id)
            .Select(i => Assert.Single(scene.Geometries, g => g.Id == i.GeometryId)).ToArray();
        if (volume is null)
        {
            Assert.Null(column.ColumnCutMesh);
            Assert.Null(column.ColumnExtrusion);
            Assert.Empty(meshes);
            return;
        }

        double? evaluatedVolume = column.ColumnCutMesh?.Volume ?? column.ColumnExtrusion?.Volume;
        Assert.NotNull(evaluatedVolume);
        Assert.Equal(volume.Value, evaluatedVolume.Value, 12);
        Assert.Equal(volume.Value, meshes.Sum(g => MeasureClosedMesh(g, measureHorizontalCaps: false).Volume), 12);
        var elevations = end == (int)ElementReaderTests.ColumnBeamEnd.AngledTeeNearCap ? (2.5, 4, -1.5, 0) : (0.75, 4, -3.25, 0);
        Assert.Equal(elevations, (column.BottomElevationToHomeStory, column.TopElevationToHomeStory,
            column.BottomElevationToProjectZero, column.TopElevationToProjectZero));
        Assert.Empty(scene.Validate());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void CutsAProfiledColumnWithAHorizontalPartialBeam(bool concave, bool wrongOwner, bool skewed)
    {
        using var scope = TempContainer("column-partial-beam.pln", ElementReaderTests.ColumnBeamBandsFixture(
            partialCut: true, otherColumn: true, wrongOtherOwner: wrongOwner, concaveProfile: concave, skewedBeam: skewed));
        using var document = PlaDocument.Open(scope.Path);
        var column = Assert.Single(document.ReadElements(), e => e.Type == "Column" && e.MemberProfile is not null);
        var scene = PlaSceneReader.Read(scope.Path).Document;
        var owner = Assert.Single(scene.Elements, e => e.SourceId == column.Guid.ToString("D"));
        if (wrongOwner)
        {
            Assert.Null(column.ColumnCutMesh);
            Assert.DoesNotContain(scene.Instances, i => i.ElementId == owner.Id);
            return;
        }

        var cut = Assert.IsType<PlaColumnCutMesh>(column.ColumnCutMesh);
        var instance = Assert.Single(scene.Instances, i => i.ElementId == owner.Id);
        var mesh = Assert.Single(scene.Geometries, g => g.Id == instance.GeometryId);
        Assert.Equal((0.75, 4, -3.25, 0), (column.BottomElevationToHomeStory, column.TopElevationToHomeStory,
            column.BottomElevationToProjectZero, column.TopElevationToProjectZero));
        Assert.Equal(new PlaBoundingBox(-0.25, -0.25, -3.25, 0.25, 0.25, 0), cut.BoundingBox);
        Assert.Equal(new PlaBoundingBox(-0.25, -0.25, -4, 0.25, 0.25, 0), column.BoundingBox);
        double expected = concave ? 0.515625 : skewed ? 0.8125 - Math.Sqrt(17) / 32 : 0.6875;
        Assert.Equal(expected, cut.Volume, 12);
        Assert.Equal(cut.Volume, MeasureClosedMesh(mesh, measureHorizontalCaps: false).Volume, 12);
        Assert.Empty(scene.Validate());
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void CutsAnObliqueColumnWithAnInclinedPartialBeam(bool wrongOwner, bool? validWallLabel)
    {
        using var scope = TempContainer("oblique-column.pln", ElementReaderTests.ColumnBeamBandsFixture(
            partialCut: true, otherColumn: true, wrongOtherOwner: wrongOwner, oblique: true, validWallLabel));
        using var document = PlaDocument.Open(scope.Path);
        var column = Assert.Single(document.ReadElements(), e => e.Type == "Column" && e.MemberProfile is not null);
        var scene = PlaSceneReader.Read(scope.Path).Document;
        var owner = Assert.Single(scene.Elements, e => e.SourceId == column.Guid.ToString("D"));
        if (wrongOwner || validWallLabel == false)
        {
            Assert.Null(column.ColumnCutMesh);
            Assert.DoesNotContain(scene.Instances, i => i.ElementId == owner.Id);
            return;
        }

        var cut = Assert.IsType<PlaColumnCutMesh>(column.ColumnCutMesh);
        var instance = Assert.Single(scene.Instances, i => i.ElementId == owner.Id);
        var mesh = Assert.Single(scene.Geometries, g => g.Id == instance.GeometryId);
        Assert.Equal((0.75, 4, -3.25, 0), (column.BottomElevationToHomeStory, column.TopElevationToHomeStory,
            column.BottomElevationToProjectZero, column.TopElevationToProjectZero));
        Assert.Equal(new PlaBoundingBox(-0.25, -0.25, -4, 0.25, 0.25, 0), column.BoundingBox);
        Assert.Equal(0.8125 - Math.Sqrt(17) / 32, cut.Volume, 12);
        Assert.Equal(cut.Volume, MeasureClosedMesh(mesh, measureHorizontalCaps: false).Volume, 12);
        Assert.Empty(scene.Validate());
    }

    private static (double Volume, double Top, double Bottom) MeasureClosedMesh(SceneGeometry mesh, bool measureHorizontalCaps = true)
    {
        double signedVolume = 0, top = 0, bottom = 0;
        var edges = new Dictionary<(int, int), (int Count, int Orientation)>();
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            var a = mesh.Vertices[mesh.Indices[i]];
            var b = mesh.Vertices[mesh.Indices[i + 1]];
            var c = mesh.Vertices[mesh.Indices[i + 2]];
            signedVolume += a.X * (b.Y * c.Z - b.Z * c.Y)
                + a.Y * (b.Z * c.X - b.X * c.Z) + a.Z * (b.X * c.Y - b.Y * c.X);
            if (measureHorizontalCaps && a.Z == b.Z && b.Z == c.Z)
            {
                double area = ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2;
                Assert.True(a.Z == 0 ? area < 0 : area > 0);
                if (a.Z == 0) bottom -= area;
                else top += area;
            }
            for (int j = 0; j < 3; j++)
            {
                int from = mesh.Indices[i + j], to = mesh.Indices[i + (j + 1) % 3];
                Assert.NotEqual(from, to);
                var key = (Math.Min(from, to), Math.Max(from, to));
                var (count, orientation) = edges.GetValueOrDefault(key);
                edges[key] = (count + 1, orientation + (from < to ? 1 : -1));
            }
        }
        Assert.All(edges.Values, e => Assert.Equal((2, 0), e));
        return (signedVolume / 6, top, bottom);
    }

    [Fact]
    public void KeepsSlabMetadataWithoutCreatingAMeshForAnUnverifiedSolid()
    {
        var fixture = ElementReaderTests.SlabSceneFixture(concave: false, beveled: true);
        using var scope = TempContainer("beveled.pla", fixture.File);
        var result = PlaSceneReader.Read(scope.Path);
        Assert.Single(result.Document.Elements);
        Assert.Empty(result.Document.Geometries);
        Assert.Empty(result.Document.Instances);
        Assert.Equal("geometry-incomplete", Assert.Single(result.Diagnostics).Code);
        var properties = Assert.Single(result.Document.PropertySets).Properties;
        Assert.Contains(properties, p => p.Key == "archicad:slab-outline-area");
        Assert.DoesNotContain(properties, p => p.Key == "archicad:slab-net-volume");
    }

    [Fact]
    public void MapsOnlyVerifiedClassificationAssignments()
    {
        var fixture = ElementReaderTests.ClassificationFixture();
        using var scope = TempContainer("classes.pln", fixture.File);
        var scene = PlaSceneReader.Read(scope.Path).Document;
        var classified = Assert.Single(scene.Elements, e => e.SourceId == fixture.Classified.ToString("D"));
        var unclassified = Assert.Single(scene.Elements, e => e.SourceId == fixture.Unclassified.ToString("D"));
        var set = Assert.Single(scene.PropertySets, s => s.Key == "archicad:classifications");
        Assert.Contains(set.Id, classified.PropertySetIds);
        Assert.DoesNotContain(set.Id, unclassified.PropertySetIds);
        Assert.Equal(fixture.Leaf.ToString("D"),
            Assert.Single(set.Properties, p => p.Key == $"archicad:classification:{fixture.First:D}").Value.Text);
    }

    [Theory]
    [InlineData(null, 0.5)]
    [InlineData("unlinked override", 0.125)]
    public void MapsApplicableUserPropertyValuesAndLeavesUndefinedValuesOut(string? fixtureError, double expectedLength)
    {
        var fixture = ElementReaderTests.PropertyCatalogFixture(fixtureError, withOverride: true, classified: true);
        using var scope = TempContainer("properties.pln", fixture.File);
        var scene = PlaSceneReader.Read(scope.Path).Document;
        var set = Assert.Single(scene.PropertySets, s => s.Key == $"archicad:group:{fixture.Group:D}");
        Assert.Contains(set.Id, Assert.Single(scene.Elements).PropertySetIds);
        Assert.Equal("Параметры", set.Name);
        Assert.Equal(2, set.Properties.Count);
        var length = Assert.Single(set.Properties, p => p.Key == $"archicad:property:{fixture.Length:D}").Value;
        Assert.Equal(SceneValueKind.Number, length.Kind);
        Assert.Equal(SceneUnits.Metre, length.Unit);
        Assert.Equal(expectedLength, length.Number);
        Assert.Equal("Étage", Assert.Single(set.Properties, p => p.Key == $"archicad:property:{fixture.Single:D}").Value.Text);
    }

    /// <summary>A synthetic container written to a file of the given name in its own temporary directory.</summary>
    private static TempScope TempContainer(string fileName, byte[]? bytes = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"plaio-scene-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, bytes ?? Synthetic.Fdb(Synthetic.Block(PlaBlockMode.Raw, [0x01, 0x02, 0x03])));
        return new TempScope(path, directory);
    }

    private sealed class TempScope(string path, string directory) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
