using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Games;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "make loading faster: the paks, the levels". Times the app's loading phases on the real game files and prints
/// them (asserts nothing about speed): connect (pak index), world index, the whole-island backdrop and the levels streamed
/// around the outpost, the A_0 cell twice (second = in-memory prepare cache), a 20-edit export without and with the pak.
/// Two passes over one data folder: the first starts with no app caches, the second is a fresh app over the caches the
/// first left. Each pass prints fingerprints of the world index, the prepared scenes and the exported files: compare them
/// with an older build's to see that a speed-up changed no result; the test asserts that both passes agree. Real game
/// files only (<c>SCUM_PAKS</c>). The test host runs the workstation GC: set <c>DOTNET_gcServer=1</c> and
/// <c>DOTNET_GCDynamicAdaptationMode=1</c> to time it with the collector the app runs (ScumStudio.App.csproj).
/// </summary>
public sealed class PerfRealTests
{
    private static readonly FVector Outpost = new(-613500f, -549500f, 2450f);

    private readonly ITestOutputHelper _output;
    private readonly Dictionary<string, List<string>> _hashes = [];

    public PerfRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task LoadingPhasesAreTimed()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        var root = Path.Combine(Path.GetTempPath(), "scumstudio-perf");
        Delete(root);
        try
        {
            await PassAsync("cold", root, paks);
            await PassAsync("warm", root, paks);

            // Built anew or read back from the caches the first pass left, everything is the same.
            Assert.Equal(_hashes["cold"], _hashes["warm"]);
        }
        finally
        {
            Delete(root);
        }
    }

    private async Task PassAsync(string pass, string root, string paks)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var work = Path.Combine(root, pass);
        using var services = AppServices.Create(new AppServicesOptions
        {
            DataDirectory = Path.Combine(root, "data"), // shared by both passes: the second sees the first one's caches
            Dispatcher = new InlineUiDispatcher(),
            LogToFile = false,
            LogToConsole = false,
            ToastLifetime = null,
            LocatorFactory = logger => new GameLocator(new GameLocatorOptions { SteamRoots = [Path.Combine(work, "no-steam")], ServerSearchRoots = [] }, logger),
            KeyFinder = _ => Task.FromResult<string?>(null),
        });
        services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        services.UpdateSettings(s => s with { GamePaksFolder = paks });

        var clock = Stopwatch.StartNew();
        await services.Workspace.ConnectAsync(ProgressSink.Null);
        Report(pass, "connect (pak index)", clock, $"{services.Workspace.MountedContainers} paks, {services.Workspace.PackageCount:N0} packages");

        clock.Restart();
        using var map = new MapPageViewModel(services);
        await map.LoadCompletion;
        var world = map.World!;
        Report(pass, "world index (tile info)", clock, $"{world.Packages.Count:N0} packages");
        using (var index = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            foreach (var p in world.Packages)
            {
                index.AppendData(Encoding.UTF8.GetBytes($"{p}|{(p.Tile is { } t ? string.Join(",", t.LodList) : "-")}\n"));
            }

            Hash(pass, "world index", $"{world.Packages.Count(p => p.Tile is not null)} tiles {Convert.ToHexString(index.GetHashAndReset())[..12]}");
        }

        clock.Restart();
        while (map.WorldBackdrop is null && clock.Elapsed < TimeSpan.FromMinutes(5))
        {
            await Task.Delay(20);
        }

        Report(pass, "island backdrop", clock, $"{map.WorldBackdrop?.Terrain.Count} terrain components");
        Fingerprint(pass, "backdrop", map.WorldBackdrop!);

        clock.Restart();
        // What the streamer loads for a camera at the outpost (StreamAround; its continuation needs the UI scheduler).
        var around = MapPageViewModel.StreamingSet(world, Outpost, map.Quality.StreamRadiusCm, []);
        await map.LoadLevelsAsync(around, landscapeStep: 4, seaPlane: false, streamed: true);
        Report(pass, "stream around outpost", clock, Describe(map.PreparedScene));
        Fingerprint(pass, "stream", map.PreparedScene!);

        var a0 = MapPageViewModel.CellPackages(world, new MapCell('A', 0));
        for (var run = 1; run <= 2; run++)
        {
            clock.Restart();
            await map.LoadLevelsAsync(a0, landscapeStep: 4);
            Report(pass, $"A_0 cell load #{run}", clock, Describe(map.PreparedScene));
            Fingerprint(pass, $"A_0#{run}", map.PreparedScene!);
        }

        // 20 edits over the cell's levels: ten moves and ten copies, round-robin over the levels.
        var project = await services.Projects.CreateAsync(Path.Combine(work, "projects"), "Perf");
        var picks = map.AllActors
            .Where(a => a.Actor.Kind is ActorKind.StaticMeshActor or ActorKind.Blueprint && a.Actor.Root is not null)
            .GroupBy(a => a.Level.PackagePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Take(4).Select((a, i) => (a, i)))
            .OrderBy(t => t.i)
            .Select(t => t.a)
            .Take(20)
            .ToList();
        for (var i = 0; i < picks.Count; i++)
        {
            var at = picks[i].Actor.Root!.Relative;
            project.Apply(i % 2 == 0
                ? EditOpFactory.SetTransform(picks[i].Level, picks[i].Actor, at with { Location = at.Location + new FVector(100f, 0f, 0f) }, project.State)
                : EditOpFactory.Duplicate(picks[i].Level, picks[i].Actor, null, project.State));
        }

        services.Projects.Refresh();
        foreach (var writePak in new[] { false, true })
        {
            clock.Restart();
            var result = await new ProjectExporter().ExportAsync(project, services.Workspace.Catalog!,
                new ExportOptions { OutputDirectory = Path.Combine(work, writePak ? "out-pak" : "out-loose"), WritePak = writePak });
            Report(pass, $"export {project.History.Count} edits, pak={writePak}", clock, $"{result.Levels.Count} levels");
            Hash(pass, $"export pak={writePak}", HashFolder(result.StagingDirectory) + (result.PakPath is { } pak ? $" pak {HashFile(pak)}" : string.Empty));
        }
    }

    private void Hash(string pass, string label, string value)
    {
        _output.WriteLine($"[{pass}] hash {label}: {value}");
        (_hashes.TryGetValue(pass, out var list) ? list : _hashes[pass] = []).Add($"{label}: {value}");
    }

    private void Report(string pass, string phase, Stopwatch clock, string detail)
    {
        // GC pause since the last phase and the process's peak working set: loading is allocation-heavy, so the collector
        // the app runs with (see ScumStudio.App.csproj) shows here.
        var pause = GC.GetTotalPauseDuration();
        using var process = Process.GetCurrentProcess();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{pass}] {phase,-34} {clock.Elapsed.TotalMilliseconds,9:0} ms   gc pause {(pause - _pause).TotalMilliseconds,6:0} ms   peak {process.PeakWorkingSet64 >> 20,6} MB   {detail}"));
        _pause = pause;
    }

    private TimeSpan _pause = GC.GetTotalPauseDuration();

    private static string Describe(PreparedLevelScene? scene) => scene is null
        ? "no scene"
        : $"{scene.Documents.Count} levels, {scene.Placements.Count:N0} placements, {scene.Meshes.Count:N0} meshes, {scene.Textures.Count:N0} textures, {scene.Terrain.Count} terrain, prepare {scene.Elapsed.TotalMilliseconds:0} ms";

    /// <summary>Hashes of what the viewport gets: placements in order, meshes and textures by path, terrain in order.</summary>
    private void Fingerprint(string pass, string label, PreparedLevelScene scene)
    {
        using var placements = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in scene.Placements)
        {
            Text(placements, $"{p.MeshPath}|{p.SelectableId}|{p.Name}|{p.DocumentIndex}|{p.CullDistance}|{p.InstanceKey}|{p.World.Translation}|{p.World.Rotation}|{p.World.Scale3D}");
            Floats(placements, [p.GlModel.M11, p.GlModel.M12, p.GlModel.M13, p.GlModel.M21, p.GlModel.M22, p.GlModel.M23, p.GlModel.M31, p.GlModel.M32, p.GlModel.M33, p.GlModel.M41, p.GlModel.M42, p.GlModel.M43]);
        }

        using var meshes = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (key, m) in scene.Meshes.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            Text(meshes, $"{key}|{m.MeshPath}|{m.TexturePath}|{m.IsEditorOnly}|{m.Shimmer}|{m.Billboard}|{string.Join(",", m.LodScreenSizes)}|{string.Join(",", m.MaterialSlots)}");
            Text(meshes, string.Join(";", m.MaterialTextures.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")));
            Text(meshes, string.Join(";", m.MaterialAlphaCutoffs.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")));
            Text(meshes, string.Join(";", m.MaterialTints.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")));
            foreach (var lod in m.Lods)
            {
                Mesh(meshes, lod);
            }
        }

        using var textures = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (key, t) in scene.Textures.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            Text(textures, $"{key}|{t.Name}|{t.Width}|{t.Height}|{t.PixelFormat}|{t.MipIndex}|{t.IsSrgb}|{t.IsNormalMap}");
            textures.AppendData(t.Rgba);
        }

        using var terrain = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var t in scene.Terrain)
        {
            Text(terrain, $"{t.Name}|{t.LevelName}|{t.Albedo?.Size}");
            Mesh(terrain, t.Mesh);
            if (t.Albedo is { } albedo)
            {
                terrain.AppendData(albedo.Rgba);
            }
        }

        using var rest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Text(rest, string.Join("\n", scene.MissingMeshes) + "\n--\n" + string.Join("\n", scene.Warnings) + "\n--\n" + string.Join("\n", scene.MissingLayerTextures)
                   + $"\n--\n{scene.RequestedPlacements}|{scene.SeaLevelCm}|{scene.Ground}|{string.Join(",", scene.SplineSources.Keys.Order(StringComparer.Ordinal))}");
        Hash(pass, label, $"placements {Hex(placements)} meshes {Hex(meshes)} textures {Hex(textures)} terrain {Hex(terrain)} rest {Hex(rest)}");

        static void Mesh(IncrementalHash h, ScumStudio.Core.Geometry.MeshData mesh)
        {
            Text(h, $"{mesh.Name}|{string.Join(",", mesh.Sections.Select(s => $"{s.MaterialName}:{s.FirstIndex}:{s.IndexCount}"))}");
            Floats(h, mesh.Positions);
            Floats(h, mesh.Normals);
            Floats(h, mesh.Uv0);
            h.AppendData(MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
        }

        static void Floats(IncrementalHash h, float[] values) => h.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));

        static void Text(IncrementalHash h, string text) => h.AppendData(Encoding.UTF8.GetBytes(text + "\n"));

        static string Hex(IncrementalHash h) => Convert.ToHexString(h.GetHashAndReset())[..12];
    }

    private static string HashFolder(string folder)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        foreach (var file in files)
        {
            h.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, file).Replace('\\', '/') + "\n"));
            h.AppendData(File.ReadAllBytes(file));
        }

        return $"{files.Count} files {Convert.ToHexString(h.GetHashAndReset())[..12]}";
    }

    private static string HashFile(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))[..12];

    private static void Delete(string directory)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
        }
    }
}
