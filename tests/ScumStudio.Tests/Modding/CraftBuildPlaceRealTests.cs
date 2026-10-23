using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The owner's "Base Cabin 03" on the real game (SCUM 1.3.3, <c>SCUM_PAKS</c>, key from this PC's store; owner, 2026-10-17:
/// "it appears around me, sinks into the ground; a flag option; decay and repair like the game's"). The game ships no recipe
/// for its cabins, so 3.4.0 built it on the chair (20 health, the C++ default height rule, may tilt); built like the game's it is the
/// cabin itself. With <c>SCUMSTUDIO_OWNER_PROJECT</c> set to a COPY of his project folder, his own craftables.json is used.
/// </summary>
public sealed class CraftBuildPlaceRealTests
{
    private const string Elements = "/Game/ConZ_Files/BaseBuilding/BaseElements/";
    private const string Placeables = "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/";
    private const string Cabin = Elements + "BP_Base_Cabin_03";
    private const string Chair = Elements + "BP_Chair_Improvised_Wood";

    private static AssetCatalog Open() => AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    private static Dictionary<string, Tunable> Stored(AssetCatalog catalog, string element)
    {
        var cdo = $"Default__{element[(element.LastIndexOf('/') + 1)..]}_C";
        return TunableReader.Read(ModdableAssets.ReadPackage(catalog, element)).Where(t => t.Export == cdo).ToDictionary(t => t.Path, StringComparer.Ordinal);
    }

    /// <summary>His craftable: from the copy of his project when <c>SCUMSTUDIO_OWNER_PROJECT</c> names one, else the same values.</summary>
    private static Craftable Owners()
    {
        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_OWNER_PROJECT") is { Length: > 0 } project && File.Exists(Path.Combine(project, CraftablesFile.FileName)))
        {
            return CraftablesFile.Parse(File.ReadAllText(Path.Combine(project, CraftablesFile.FileName))).Items.Single(i => i.Token == "SS_Base_Cabin_03");
        }

        return new Craftable
        {
            Name = "Base Cabin 03",
            Token = "SS_Base_Cabin_03",
            Source = Cabin,
            Mesh = "/Game/ConZ_Files/Models/Objects/Outdoor/Base_Building_Elements/Wooden_Cabin/SM_Wooden_Cabin_Closed_Windows_03",
            Kind = CraftKind.Furniture,
            Material = CraftMaterial.Wood,
            Category = "CraftingCategory.BaseBuilding.Flags",
            Icon = CraftPictures.FromModel,
            SizeMeters = 15,
            Ingredients = [new("CI_Plank", 50, Item: "/Game/ConZ_Files/Items/Equipment/Active_Items/Wooden_Plank"), new("CI_Group_Logs", 35), new("CI_Sand_Bag", 10, Item: "/Game/ConZ_Files/Items/Equipment/SandBag")],
            Exported = true,
        };
    }

    [PaksFact]
    public void TheGameShipsNoRecipeForItsCabinsSoTheLargeLogShedsIsBorrowed()
    {
        using var catalog = Open();
        foreach (var leaf in new[] { "BP_Base_Cabin", "BP_Base_Cabin_02", "BP_Base_Cabin_03" })
        {
            var family = ElementFamilies.Of(catalog, Elements + leaf);
            Assert.True(family is not null, ElementFamilies.WhyNot(catalog, Elements + leaf));
            Assert.True(family!.BorrowsRecipe, leaf);
            Assert.Equal(CraftablesPlanner.LargeBuildingRecipe, family.Recipe); // the game's walk-in building's, not the chair's (3.5.0: always red)
            Assert.Equal(Elements + leaf, Assert.Single(family.Levels).Element);
        }

        // Every placeable recipe of the game checks mesh against mesh (BoxOverlapFlags 2: the chair's as much as the shed's), so
        // the shed's recipe alone does not make a cabin placeable; what it adds are a building's placement values.
        var shed = TunableReader.Read(ModdableAssets.ReadPackage(catalog, PackageMap.Normalize(CraftablesPlanner.LargeBuildingRecipe)))
            .Where(t => t.Export == "CR_Large_Log_Shed").ToDictionary(t => t.Path, StringComparer.Ordinal);
        var chairRecipe = TunableReader.Read(ModdableAssets.ReadPackage(catalog, PackageMap.Normalize(CraftablesPlanner.FurnitureRecipe)))
            .Where(t => t.Export == "CR_Chair_Improvised_Wood").ToDictionary(t => t.Path, StringComparer.Ordinal);
        Assert.Equal("2", shed["BoxOverlapFlags"].Value); // EPlaceableActorBoxOverlapFlag::MeshVsMesh
        Assert.Equal("2", chairRecipe["BoxOverlapFlags"].Value);
        Assert.Equal("50", shed["GroundOffsetWhilePlacing"].Value);
        Assert.Equal("50", shed["MaxDistanceFromGroundWhilePlacing"].Value);
        Assert.False(chairRecipe.ContainsKey("GroundOffsetWhilePlacing"));
        Assert.False(shed.ContainsKey("CollisionMeshScale")); // what the planner adds for a big building (the workbench's is 1, 1, 0.9)

        Assert.False(ElementFamilies.Of(catalog, Elements + "BP_Bed_Improvised")!.BorrowsRecipe); // its own CR_Bed_Improvised

        // The cabin: 3000 health, 28 days of decay, the large buildings' placement; no flag value (the C++ default: a flag
        // is needed); 1000 health per repair use. The chair 3.4.0 built it on: 20 health, no height rule (C++ default), may tilt.
        var cabin = Stored(catalog, Cabin);
        Assert.Equal("3000", cabin["_healthPoints"].Value);
        Assert.Equal("672", cabin["_hoursToDecay"].Value);
        Assert.Equal(CraftablesPlanner.HighestPointHeight, cabin["_placementHeightType"].Value);
        Assert.Equal("true", cabin["_mustBePlacedUpright"].Value);
        Assert.True(TunableValue.AreEqual(TunableKind.Rotator, "0, 360, 0", cabin["_maxRotationWhilePlacing"].Value));
        Assert.Equal("1000", cabin["_repairableParams.HealthRepairedPerUse"].Value);
        Assert.False(cabin.ContainsKey("_canBePlacedWithoutFlag"));
        Assert.False(cabin.ContainsKey("RelativeLocation"));

        var chair = Stored(catalog, Chair);
        Assert.Equal("20", chair["_healthPoints"].Value);
        Assert.False(chair.ContainsKey("_placementHeightType"));
        Assert.False(chair.ContainsKey("_mustBePlacedUpright"));
        Assert.True(TunableValue.AreEqual(TunableKind.Rotator, "360, 360, 0", chair["_maxRotationWhilePlacing"].Value));

        // The shelter proves the flag value's type and default: it stores true, so the C++ default is false.
        Assert.Equal("true", Stored(catalog, Elements + "BP_Base_Shelter")["_canBePlacedWithoutFlag"].Value);
    }

    [PaksFact]
    public async Task TheOwnersCabinExportsAsTheGamesCabinForClientAndServer()
    {
        using var catalog = Open();
        var owner = Owners();
        Assert.Equal(CraftKind.Furniture, owner.Kind);
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-buildplace-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Before (his file as it is): the chair's element showing the cabin; this version places it like the game's large
            // buildings, but its health is still the chair's.
            var before = await ExportAsync(catalog, Path.Combine(dir, "before"), ProjectSourceRole.Client, owner);
            var chairCabin = Load(before, Elements + "BP_SS_Base_Cabin_03.Default__BP_SS_Base_Cabin_03_C");
            Assert.Equal(20f, chairCabin.GetOrDefault<float>("_healthPoints"));
            Assert.Equal(CraftablesPlanner.HighestPointHeight, chairCabin.GetOrDefault<FName>("_placementHeightType").Text);
            Assert.True(chairCabin.GetOrDefault<bool>("_mustBePlacedUpright"));

            // After: one click to the game's cabin, built without a flag, a repair at 200 % and 14 days of decay.
            var after = owner with
            {
                Kind = CraftKind.Element,
                Element = new ElementSettings { Template = Cabin, Levels = [new Dictionary<string, string> { ["_hoursToDecay"] = "336" }] },
                Use = CraftUse.Build,
                NeedsFlag = false,
                RepairCostPercent = 200,
            };
            foreach (var role in new[] { ProjectSourceRole.Client, ProjectSourceRole.Server })
            {
                var result = await ExportAsync(catalog, Path.Combine(dir, "after"), role, after);
                Assert.Equal(
                    before.Assets.Where(a => !a.PackagePath.Contains("/ICO_", StringComparison.OrdinalIgnoreCase)).Select(a => a.PackagePath).Order(StringComparer.OrdinalIgnoreCase),
                    result.Assets.Where(a => !a.PackagePath.Contains("/ICO_", StringComparison.OrdinalIgnoreCase)).Select(a => a.PackagePath).Order(StringComparer.OrdinalIgnoreCase));

                var cdo = Load(result, Elements + "BP_SS_Base_Cabin_03.Default__BP_SS_Base_Cabin_03_C");
                Assert.Equal("Base Cabin 03", cdo.GetOrDefault<FText>("_name").Text);
                Assert.Equal(3000f, cdo.GetOrDefault<float>("_healthPoints"));
                Assert.Equal(336f, cdo.GetOrDefault<float>("_hoursToDecay"));
                Assert.True(cdo.GetOrDefault<bool>("_canBePlacedWithoutFlag"));
                Assert.Equal(CraftablesPlanner.HighestPointHeight, cdo.GetOrDefault<FName>("_placementHeightType").Text);
                Assert.True(cdo.GetOrDefault<bool>("_mustBePlacedUpright"));
                Assert.Equal("EBaseElementType::Cabin", cdo.GetOrDefault<FName>("_baseElementType").Text);
                Assert.Equal(500f, cdo.GetOrDefault<FStructFallback>("_repairableParams").GetOrDefault<float>("HealthRepairedPerUse"));

                var recipe = Load(result, Placeables + "CR_SS_Base_Cabin_03.CR_SS_Base_Cabin_03");
                Assert.Equal(Elements + "BP_SS_Base_Cabin_03.BP_SS_Base_Cabin_03_C", recipe.GetOrDefault<FSoftObjectPath>("Product").AssetPathName.Text);

                // Only new packages: the game's cabin and chair are untouched.
                Assert.All(result.Assets, a => Assert.False(catalog.PackageExists(a.PackagePath), a.PackagePath));
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    /// His cabin, as his file has it (Furniture, 15 m) and as the game's cabin, is placed with the game's Large Log Shed recipe
    /// and its placement check drawn in (owner, 3.5.0: on the chair's it was always red, "Overlapping character": the game
    /// centres the ghost on the aimed point, so inside the cabin its floor meets his feet, and every recipe checks mesh
    /// against mesh). The copied recipe keeps the shed's placement values, makes his element, and gets the collision mesh
    /// scaled and the box drawn in to 1.5 m around the pivot, the way the game's own wall lamps are. "Place in front"
    /// (experimental) is left out with it (the game added that offset in world directions after finding the ground: the ghost
    /// floated and did not turn with him). A mid-sized furniture craftable keeps the chair's recipe, where the switch still
    /// writes the offset; a chair-sized one with the switch on is untouched. With <c>SCUMSTUDIO_PREVIEW_OUT</c>, a top and a
    /// side view of the bounds.
    /// </summary>
    [PaksFact]
    public async Task ABigBuildingIsPlacedWithTheLargeLogShedsRecipeAndPlaceInFrontIsLeftOut()
    {
        using var catalog = Open();
        var owner = Owners();
        var mesh = catalog.LoadFirstExport<CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh>(owner.Mesh)!;
        var bounds = ScumStudio.Assets.Meshes.MeshExtractor.DescribeStaticMesh(mesh).Bounds;
        var reach = new[] { -bounds.Min.X, bounds.Max.X, -bounds.Min.Y, bounds.Max.Y }.Max(); // cm, horizontally from the pivot
        Assert.InRange(reach, 750f, 770f); // the cabin is centred on its pivot: Y -761 .. 739, so the player aims from inside it
        Assert.Equal(15f, owner.SizeMeters, 1);
        Assert.True(CraftablesPlanner.UsesLargeBuildingRecipe(catalog, owner));

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-infront-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gameCabin = owner with { Kind = CraftKind.Element, Element = new ElementSettings { Template = Cabin }, Use = CraftUse.Build };
            Assert.True(CraftablesPlanner.UsesLargeBuildingRecipe(catalog, gameCabin));
            foreach (var (label, cabin) in new[] { ("furniture", owner), ("game-cabin", gameCabin) })
            {
                var off = await ExportAsync(catalog, Path.Combine(dir, label, "off"), ProjectSourceRole.Client, cabin);
                var on = await ExportAsync(catalog, Path.Combine(dir, label, "on"), ProjectSourceRole.Client, cabin with { InFront = new InFrontSettings(18f) });
                Assert.Empty(ChangedFiles(off, on)); // the switch writes nothing where the shed's recipe places it
                Assert.Contains(on.Warnings, w => w.Contains("Large Log Shed", StringComparison.Ordinal) && w.Contains("not written", StringComparison.Ordinal));

                var recipe = Placeables + "CR_SS_Base_Cabin_03";
                var read = Load(off, recipe + ".CR_SS_Base_Cabin_03");
                Assert.Equal((byte)2, read.GetOrDefault<byte>("BoxOverlapFlags")); // MeshVsMesh: the shed's, and every recipe's
                Assert.Equal(50f, read.GetOrDefault<float>("GroundOffsetWhilePlacing"));
                Assert.Equal(50f, read.GetOrDefault<float>("MaxDistanceFromGroundWhilePlacing"));
                Assert.False(read.GetOrDefault<bool>("CanPlaceOverItems", true));
                Assert.False(read.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FVector _, "PlacementLocationOffset"));

                // The check drawn in to 1.5 m around the pivot on every side (review 3.5.1: one inset from the longest reach,
                // 6.11 m, crossed the faces over on the 5.6 m X sides): the collision mesh scaled per axis, each face of the box
                // moved in to 1.5 m from the pivot, never past it.
                var scale = read.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("CollisionMeshScale");
                Assert.Equal((MathF.Round(150 / MathF.Max(-bounds.Min.X, bounds.Max.X), 2), MathF.Round(150 / MathF.Max(-bounds.Min.Y, bounds.Max.Y), 2), 1f),
                    (MathF.Round(scale.X, 2), MathF.Round(scale.Y, 2), scale.Z));
                var insetMin = read.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("BoundsExtensionWhilePlacingMin");
                var insetMax = read.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("BoundsExtensionWhilePlacingMax");
                var box = (MinX: bounds.Min.X - insetMin.X, MaxX: bounds.Max.X + insetMax.X, MinY: bounds.Min.Y - insetMin.Y, MaxY: bounds.Max.Y + insetMax.Y);
                Assert.True(box.MinX < box.MaxX && box.MinY < box.MaxY, $"the drawn-in box is inverted: {box}");
                foreach (var face in new[] { -box.MinX, box.MaxX, -box.MinY, box.MaxY })
                {
                    Assert.InRange(face, 149.5f, 150.5f);
                }

                Assert.Equal((0f, 0f), (insetMin.Z, insetMax.Z));
                Assert.Contains(off.Warnings, w => w.Contains("drawn in", StringComparison.Ordinal) && w.Contains("Base Cabin 03", StringComparison.Ordinal));
                Assert.Equal(Elements + "BP_SS_Base_Cabin_03.BP_SS_Base_Cabin_03_C", read.GetOrDefault<FSoftObjectPath>("Product").AssetPathName.Text); // his element, not the shed
                Assert.Equal("Base Cabin 03", read.GetOrDefault<FText>("Caption").Text);
                Assert.Equal(
                    "/Game/ConZ_Files/Blueprints/PlaceableActor/BP_PlaceableActorBaseBuilding.BP_PlaceableActorBaseBuilding_C",
                    read.GetOrDefault<FSoftObjectPath>("PlaceableActorClass").AssetPathName.Text);
                Assert.DoesNotContain(off.Assets, a => a.PackagePath.Contains("Log_Shed", StringComparison.OrdinalIgnoreCase)); // the shed itself is not shipped
                Assert.DoesNotContain(catalog.GetExports(PackageMap.Normalize(CraftablesPlanner.LargeBuildingRecipe)), e => e.Name == "CR_SS_Base_Cabin_03");
            }

            // A mid-sized furniture craftable (below 3 m) keeps the chair's recipe: the switch writes its offset there.
            var bench = owner with { Name = "Bench", Token = "SS_Bench_Test", SizeMeters = 2.9f };
            Assert.False(CraftablesPlanner.UsesLargeBuildingRecipe(catalog, bench));
            var plainBench = await ExportAsync(catalog, Path.Combine(dir, "bench", "off"), ProjectSourceRole.Client, bench);
            var movedBench = await ExportAsync(catalog, Path.Combine(dir, "bench", "on"), ProjectSourceRole.Client, bench with { InFront = new InFrontSettings(5f) });
            var benchRecipe = Placeables + "CR_SS_Bench_Test";
            Assert.Equal(
                [StagedFile(benchRecipe, ".uasset"), StagedFile(benchRecipe, ".uexp")],
                ChangedFiles(plainBench, movedBench).Where(f => !f.EndsWith("AssetRegistry.bin", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase));
            var moved = Load(movedBench, benchRecipe + ".CR_SS_Bench_Test");
            var offset = moved.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("PlacementLocationOffset");
            Assert.Equal((500f, 0f, 0f), (offset.X, offset.Y, offset.Z));
            Assert.Equal((byte)2, moved.GetOrDefault<byte>("BoxOverlapFlags")); // the chair's own
            Assert.False(moved.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FVector _, "CollisionMeshScale")); // not drawn in: below 3 m

            // A chair-sized craftable: the switch on writes nothing (0 m), every file as without it.
            var stool = owner with { Name = "Stool", Token = "SS_Stool_Test", Source = Chair, Mesh = CraftablesPlanner.MeshOf(catalog, Chair)!, SizeMeters = 0.9f };
            var plain = await ExportAsync(catalog, Path.Combine(dir, "stool", "off"), ProjectSourceRole.Client, stool);
            var switched = await ExportAsync(catalog, Path.Combine(dir, "stool", "on"), ProjectSourceRole.Client, stool with { InFront = new InFrontSettings() });
            Assert.Empty(ChangedFiles(plain, switched));

            if (Environment.GetEnvironmentVariable("SCUMSTUDIO_PREVIEW_OUT") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "cabin-in-front.svg"), InFrontSvg(bounds.Min.X, bounds.Max.X, bounds.Min.Y, bounds.Max.Y, bounds.Min.Z, bounds.Max.Z, 850));
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Review 3.5.1: two big buildings the draw-in missed. A model imported from a 3D file (its mesh is in the project's imports
    /// folder, not the game's files) used the shed's recipe without the drawn-in check (the 3.5.0 complaint stayed), and a
    /// station of 15 m hid "Place in front" in the page but still wrote a saved one, without the drawn-in check. The import is
    /// a stand-in made without Unreal (the game's cabin mesh cloned to an import's path, as <c>MapModelsRealTests</c> does).
    /// </summary>
    [PaksFact]
    public async Task AnImportedBigModelAndABigStationGetTheDrawnInCheckToo()
    {
        using var catalog = Open();
        var owner = Owners();
        using var temp = new ScumStudio.Tests.Level.LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Hall.ssproj"), "Hall");
        var model = new ScumStudio.Level.Import.LibraryModel("SS_My_Hall", ScumStudio.Level.Import.UeCook.ImportsFolder + "/SS_My_Hall");
        var source = PackageMap.Normalize(owner.Mesh);
        Assert.True(catalog.TryGetPackageFile(source, out var file));
        var (uasset, uexp, ubulk) = ProjectExporter.ReadPackageFiles(catalog, file);
        var clone = PackageCloner.Clone(ScumStudio.Formats.Packages.CookedPackage.Parse(uasset, uexp, ubulk, source), source, new PackageMap([KeyValuePair.Create(source, model.MeshPackage)]));
        var stem = Path.Combine(project.DirectoryPath, model.Folder, "SCUM", "Content", "ScumStudio", "Imports", model.Token, "SM_" + model.Token);
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        await clone.Bytes.WriteAsync(stem, ".uasset");
        if (clone.UBulk is not null)
        {
            await File.WriteAllBytesAsync(stem + ".ubulk", clone.UBulk);
        }

        var hall = ScumStudio.App.ViewModels.CraftablesPageViewModel.DescribeModel(project.DirectoryPath, model)! with
        {
            Name = "My Hall", Token = "SS_My_Hall_Test", Kind = CraftKind.Furniture, Ingredients = owner.Ingredients,
        };
        Assert.InRange(hall.SizeMeters, 14f, 16f);
        Assert.True(CraftablesPlanner.UsesLargeBuildingRecipe(catalog, hall));
        var imported = await CraftablesExporter.ExportAsync(new CraftablesFile { Items = [hall], ProjectDirectory = project.DirectoryPath }, "MyMapMod", catalog,
            new ExportOptions { OutputDirectory = temp.Combine("out-hall"), WritePak = true }, ProjectSourceRole.Client);
        Assert.DoesNotContain(imported!.Warnings, w => w.Contains("not drawn in", StringComparison.Ordinal));
        var hallRecipe = Load(imported, Placeables + "CR_SS_My_Hall_Test.CR_SS_My_Hall_Test");
        var hallScale = hallRecipe.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("CollisionMeshScale");
        Assert.InRange(hallScale.X, 0.1f, 0.5f);
        Assert.InRange(hallScale.Y, 0.1f, 0.5f);
        Assert.True(hallRecipe.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("BoundsExtensionWhilePlacingMin").X < -100, "the box is not drawn in");

        // A 15 m station keeps the workbench's recipe (a station's), placed like a large building with its check drawn in; a
        // saved "Place in front" is not written, as for big furniture (the page hides the switch for both).
        var forge = owner with { Name = "Big Forge", Token = "SS_Big_Forge_Test", Kind = CraftKind.Station, InFront = new InFrontSettings(5f) };
        Assert.False(CraftablesPlanner.UsesLargeBuildingRecipe(catalog, forge));
        Assert.True(CraftablesPlanner.DrawsPlacementIn(catalog, forge));
        var station = await ExportAsync(catalog, temp.Combine("out-forge"), ProjectSourceRole.Client, forge);
        Assert.Contains(station.Warnings, w => w.Contains("Big Forge", StringComparison.Ordinal) && w.Contains("not written", StringComparison.Ordinal));
        var forgeRecipe = Load(station, Placeables + "CR_SS_Big_Forge_Test.CR_SS_Big_Forge_Test");
        Assert.False(forgeRecipe.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FVector _, "PlacementLocationOffset"));
        var forgeScale = forgeRecipe.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("CollisionMeshScale");
        Assert.InRange(forgeScale.X, 0.1f, 0.5f);
        Assert.Equal(0.9f, forgeScale.Z, 3); // the workbench's own height scale is kept
        Assert.True(forgeRecipe.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector>("BoundsExtensionWhilePlacingMax").Y < -100, "the box is not drawn in");
    }

    /// <summary>The staged files of <paramref name="b"/> whose bytes differ from <paramref name="a"/>'s (or that only one has).</summary>
    private static List<string> ChangedFiles(ExportResult a, ExportResult b)
    {
        static Dictionary<string, byte[]> Files(ExportResult r) => Directory.EnumerateFiles(r.StagingDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(r.StagingDirectory, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        var left = Files(a);
        var right = Files(b);
        return left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(k => !left.TryGetValue(k, out var x) || !right.TryGetValue(k, out var y) || !x.AsSpan().SequenceEqual(y))
            .ToList();
    }

    private static string StagedFile(string package, string extension) => "SCUM/Content/" + package["/Game/".Length..] + extension;

    /// <summary>
    /// Top view (player looking up the page) and side view of the cabin's real bounds (cm), centred on the aimed point as
    /// the game does, then <paramref name="ahead"/> further along the view. The aimed point 3 m ahead is an illustration.
    /// </summary>
    private static string InFrontSvg(float minX, float maxX, float minY, float maxY, float minZ, float maxZ, float ahead)
    {
        const float aim = 300, scale = 0.22f; // cm ahead of the player; px per cm
        var svg = new System.Text.StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='1200' height='800' font-family='Segoe UI, sans-serif' font-size='14'>");
        svg.Append("<rect width='1200' height='800' fill='#ffffff'/>");
        void Top(int left, string title, float centre)
        {
            // Forward is up. Expected in game: the cabin's +Y faces the player (the furniture's direction arrow), so its Y runs along the view.
            float X(float side) => left + 300 + side * scale;
            float Y(float forward) => 590 - forward * scale;
            svg.Append($"<text x='{left + 20}' y='30' font-size='16' font-weight='bold'>{title}</text>");
            svg.Append($"<rect x='{X(-maxX):0}' y='{Y(centre - minY):0}' width='{(maxX - minX) * scale:0}' height='{(maxY - minY) * scale:0}' fill='#7a5230' fill-opacity='0.45' stroke='#3b2716'/>");
            svg.Append($"<circle cx='{X(0):0}' cy='{Y(0):0}' r='7' fill='#1f5fbf'/><text x='{X(0) + 10:0}' y='{Y(0) + 5:0}' fill='#1f5fbf'>player</text>");
            svg.Append($"<line x1='{X(0):0}' y1='{Y(0):0}' x2='{X(0):0}' y2='{Y(centre + maxY + 100):0}' stroke='#1f5fbf' stroke-dasharray='4 4'/>");
            svg.Append($"<circle cx='{X(0):0}' cy='{Y(aim):0}' r='5' fill='#d22'/><text x='{X(0) + 10:0}' y='{Y(aim) + 5:0}' fill='#d22'>aimed point</text>");
            svg.Append($"<circle cx='{X(0):0}' cy='{Y(centre):0}' r='4' fill='#000'/><text x='{X(0) + 10:0}' y='{Y(centre) + 18:0}'>pivot</text>");
            var near = centre - maxY;
            svg.Append($"<text x='{left + 20}' y='740'>{(near < 0 ? $"the player stands {(-near) / 100:0.0} m inside it" : $"its near wall {near / 100:0.0} m ahead of the player")}</text>");
        }

        Top(0, "Before: centred on the aimed point (the game)", aim);
        Top(600, $"After: +{ahead / 100:0.0} m along +X (experimental)", aim + ahead);
        svg.Append($"<text x='20' y='780' fill='#555'>Top view of SM_Wooden_Cabin_Closed_Windows_03 (real bounds {(maxX - minX) / 100:0.0} x {(maxY - minY) / 100:0.0} x {(maxZ - minZ) / 100:0.0} m). The axis the game turns it by is checked in game.</text></svg>");
        return svg.ToString();
    }

    /// <summary>
    /// Only with <c>SCUMSTUDIO_PREVIEW_OUT</c> (a folder): the picture each export draws for the cabin before (Furniture) and
    /// after (the game's cabin), and a side view of the cabin's real bounds on a 10° slope under the chair's placement rule
    /// (the C++ default, drawn as LowestPointHeight = 0) and the cabin's (highest), both centred on the aimed point as the game does.
    /// </summary>
    [PaksFact]
    public async Task PreviewsBeforeAndAfter()
    {
        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_PREVIEW_OUT") is not { Length: > 0 } output)
        {
            return; // only on request
        }

        Directory.CreateDirectory(output);
        using var catalog = Open();
        using var renderer = new ScumStudio.Viewport.CraftIconRenderer(catalog);
        var owner = Owners();
        var after = owner with { Kind = CraftKind.Element, Element = new ElementSettings { Template = Cabin }, Use = CraftUse.Build };
        foreach (var (label, craftable) in new[] { ("before-furniture", owner), ("after-game-cabin", after) })
        {
            var picture = Assert.Single(CraftablesPlanner.Plan(catalog, [craftable]).Pictures);
            var image = renderer.Render(new CraftPictureRequest(picture.Mesh, 512));
            Assert.NotNull(image);
            await ScumStudio.Rendering.Imaging.ImageExport.SavePngAsync(image!.Rgba, image.Width, image.Height, Path.Combine(output, $"cabin-{label}.png"));
        }

        var mesh = catalog.LoadFirstExport<CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh>(owner.Mesh)!;
        var bounds = ScumStudio.Assets.Meshes.MeshExtractor.DescribeStaticMesh(mesh).Bounds;
        File.WriteAllText(Path.Combine(output, "cabin-placement.svg"), PlacementSvg(bounds.Min.Y, bounds.Max.Y, bounds.Min.Z, bounds.Max.Z));
    }

    /// <summary>Two side views (y along the slope, z up, cm) of a box from <paramref name="minY"/> to <paramref name="maxY"/>.</summary>
    private static string PlacementSvg(float minY, float maxY, float minZ, float maxZ)
    {
        var slope = MathF.Tan(10 * MathF.PI / 180);
        float Ground(float y) => y * slope;
        var svg = new System.Text.StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' width='1200' height='520' font-family='Segoe UI, sans-serif' font-size='14'>");
        svg.Append("<rect width='1200' height='520' fill='#ffffff'/>");
        void Panel(int left, string title, bool highest)
        {
            const float scale = 0.3f; // px per cm
            float X(float y) => left + 300 + y * scale;
            float Y(float z) => 330 - z * scale;
            var pivot = highest ? Ground(maxY) - minZ : Ground(minY) - minZ; // the element's lowest point on the highest or lowest ground point
            svg.Append($"<text x='{left + 20}' y='30' font-size='16' font-weight='bold'>{title}</text>");
            svg.Append($"<polygon points='{X(-1000):0},{Y(Ground(-1000)):0} {X(1000):0},{Y(Ground(1000)):0} {X(1000):0},{Y(-600):0} {X(-1000):0},{Y(-600):0}' fill='#c9b48a'/>");
            svg.Append($"<rect x='{X(minY):0}' y='{Y(pivot + maxZ):0}' width='{(maxY - minY) * scale:0}' height='{(maxZ - minZ) * scale:0}' fill='#7a5230' fill-opacity='0.55' stroke='#3b2716'/>");
            svg.Append($"<circle cx='{X(0):0}' cy='{Y(Ground(0)):0}' r='5' fill='#d22'/><text x='{X(0) + 8:0}' y='{Y(Ground(0)) + 18:0}' fill='#d22'>aimed point = its centre (game code)</text>");
            svg.Append($"<rect x='{X(-300) - 6:0}' y='{Y(Ground(-300) + 180):0}' width='12' height='{180 * scale:0}' fill='#1f5fbf'/><text x='{X(-300) - 40:0}' y='{Y(Ground(-300) + 200):0}' fill='#1f5fbf'>player 3 m away</text>");
            var sunk = Ground(maxY) - (pivot + minZ);
            svg.Append($"<text x='{left + 20}' y='470'>{(highest ? $"downhill gap up to {(pivot + minZ - Ground(minY)) / 100:0.0} m, nothing sinks" : $"uphill end {sunk / 100:0.0} m inside the ground")}</text>");
        }

        Panel(0, "Before: the chair (C++ default rule, drawn as lowest point; may tilt)", highest: false);
        Panel(600, "After: the game's cabin (highest ground point, upright)", highest: true);
        svg.Append("<text x='20' y='505' fill='#555'>Side view of SM_Wooden_Cabin_Closed_Windows_03 (real bounds) on a 10° slope. Illustration of the rule names; only the game shows the exact result.</text></svg>");
        return svg.ToString();
    }

    private static async Task<ExportResult> ExportAsync(AssetCatalog catalog, string dir, ProjectSourceRole role, Craftable craftable)
    {
        var result = await CraftablesExporter.ExportAsync(new CraftablesFile { Items = [craftable] }, "MyMapMod", catalog, new ExportOptions { OutputDirectory = dir, WritePak = true }, role);
        Assert.NotNull(result);
        Assert.True(File.Exists(result!.PakPath), result.PakPath);
        return result;
    }

    /// <summary>An object of the exported pak, read with the game's reader (CUE4Parse) from the pak itself.</summary>
    private static UObject Load(ExportResult result, string objectPath)
    {
        using var pak = AssetCatalog.OpenPaks(result.PakPath!);
        return pak.LoadObject<UObject>(objectPath);
    }
}
