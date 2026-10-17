using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "real-file tests on a real vehicle spawn point: move it, export, read back the new position". Real game files only
/// (<c>SCUM_PAKS</c>, key from this PC's store): a world vehicle spawn point of the island moved by hand onto the real
/// terrain and turned, written by the project export, is read back from the written <c>The_Island_LevelStaticData</c>
/// where it was put, facing the new way, spawning the same group; its neighbours and the count are as they were.
/// </summary>
public sealed class SpawnPlaceMovesRealTests
{
    [Fact]
    public async Task AMovedAndTurnedVehicleSpawnPointIsWrittenAndReadBack()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var places = SpawnPlaces.ReadFrom(catalog);
        var stockCars = places.Where(p => p.Kind == SpawnPlaceKind.Vehicle).ToList();
        var car = stockCars[100];
        var state = new EditState();

        // 12.345 m east and 6.78 m south, on the island's real terrain there (a car stands 20 cm above it).
        var (x, y) = (car.Transform.Translation.X + 1234.5f, car.Transform.Translation.Y + 678f);
        var ground = new GroundHeights(catalog).At(x, y);
        Assert.NotNull(ground);
        var z = SpawnPlaceMoves.GroundHeight(car, ground)!.Value;
        Assert.Equal(ground!.Value + SpawnGroundCheck.VehicleLift, z);
        state.Apply(SpawnPlaceMoves.Move(car, state, x, y, z)!);
        var yaw = TransformValue.FromTransform(car.Transform).Rotation.Yaw;
        state.Apply(SpawnPlaceMoves.Turn(SpawnPlaces.WithEdits(places, state).Single(p => p.ActorName == car.ActorName), state, 37f)!);

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-spawnmove-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new ProjectExporter().ExportAsync(state, "SpawnMove", catalog,
                new ExportOptions { OutputDirectory = dir, WritePak = false }, ScumStudio.Level.Projects.ProjectSourceRole.Client);
            var stem = Path.Combine(result.StagingDirectory, "SCUM", "Content", "ConZ_Files", "Maps", "The_Island", "The_Island_LevelStaticData");
            var written = SpawnPlaces.Read(CookedPackage.Parse(File.ReadAllBytes(stem + ".uasset"), File.ReadAllBytes(stem + ".uexp"), null, SpawnPlaces.StaticDataPath));
            var cars = written.Where(p => p.Kind == SpawnPlaceKind.Vehicle).ToList();

            Assert.Equal(stockCars.Count, cars.Count);
            Assert.Equal(new FVector(x, y, z), cars[100].Transform.Translation);
            Assert.Equal(UeMath.NormalizeAxis(yaw + 37f), TransformValue.FromTransform(cars[100].Transform).Rotation.Yaw, 1);
            Assert.Equal(car.Group, cars[100].Group);
            Assert.Equal(stockCars[99].Transform.Translation, cars[99].Transform.Translation);
            Assert.Equal(stockCars[101].Transform.Translation, cars[101].Transform.Translation);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
