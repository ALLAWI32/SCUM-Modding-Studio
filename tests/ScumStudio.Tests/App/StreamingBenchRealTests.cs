using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Core.Settings;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.Gl;
using ScumStudio.Rendering.Resources;
using ScumStudio.Rendering.Targets;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// BENCHMARK (owner: "it stutters while I fly over the map"). Whole-island mode on the real game files and OpenGL: the
/// camera flies from the B_4 outpost north into A_4 and west towards A_3 (30 m over the ground, a fixed speed in real time),
/// the map page streams the levels around it as the app does (<see cref="MapPageViewModel.UpdateWorldCamera"/> twice a
/// second, the load's continuations run on the frame thread like Avalonia's UI thread) and every frame is drawn offscreen
/// the way <c>LevelViewport</c> draws it (island backdrop, then the levels). Prints p50/p95/max frame time, every frame over
/// 50 ms and each streaming step (levels in/out, read and prepare on the worker, show/upload/apply/first draw on the frame
/// thread, GC pause); asserts only that the flight streamed. Real game files only (<c>SCUM_PAKS</c>); keeps its data folder
/// (the island terrain cache) in the temp folder between runs. Run it with the app's collector:
/// <c>DOTNET_gcServer=1 DOTNET_GCDynamicAdaptationMode=1</c>.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class StreamingBenchRealTests(ITestOutputHelper output)
{
    private const float SpeedCmPerSecond = 8_000f; // 80 m/s: the drone flying fast
    private const float HeightCm = 3_000f;
    private const int Width = 1280;
    private const int Height = 720;
    private static readonly ScumStudio.Rendering.SceneGraph.Scene EmptyScene = new();

    [GlFact]
    public void FlyingFromB4IntoA4AndA3IsTimed() => Fly(debug: false);

    /// <summary>
    /// D03 (AMD cards crash as areas load, "most at POIs with mechs and military stuff"): the same flight into the A_4
    /// airfield on a debug context, drawn as the Map draws (4 samples, sun shadows, a pick now and then), with the driver's
    /// debug output at every severity: the real game's meshes and textures streaming in and out must leave no error,
    /// undefined behaviour or portability message.
    /// </summary>
    [GlFact]
    public void FlyingIntoTheAirfieldOnADebugContextLeavesTheDriverNothingToReport() => Fly(debug: true);

    private void Fly(bool debug)
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                FrameThread.Run(() => FlyAsync(paks, debug));
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, 16 << 20);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }

    private async Task FlyAsync(string paks, bool debug)
    {
        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_BENCH_SLL") == "1")
        {
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        }

        var root = Path.Combine(Path.GetTempPath(), "scumstudio-streambench");
        using var services = ScumStudio.App.Services.AppServices.Create(new ScumStudio.App.Services.AppServicesOptions
        {
            DataDirectory = Path.Combine(root, "data"), // kept: the island terrain cache makes the next run start in seconds
            Dispatcher = new ScumStudio.App.Services.InlineUiDispatcher(),
            LogToFile = false,
            LogToConsole = false,
            ToastLifetime = null,
            LocatorFactory = logger => new ScumStudio.Core.Games.GameLocator(new ScumStudio.Core.Games.GameLocatorOptions { SteamRoots = [Path.Combine(root, "no-steam")], ServerSearchRoots = [] }, logger),
            KeyFinder = _ => Task.FromResult<string?>(null),
            ProjectsFolder = Path.Combine(root, "projects"),
        });
        services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        services.UpdateSettings(s => s with { GamePaksFolder = paks, Ui = s.Ui with { RenderQuality = Enum.TryParse<RenderQuality>(Environment.GetEnvironmentVariable("SCUMSTUDIO_BENCH_QUALITY"), out var q) ? q : RenderQuality.Balanced } });
        await services.Workspace.ConnectAsync(ProgressSink.Null);
        using var map = new MapPageViewModel(services);
        await map.LoadCompletion;
        var world = map.World!;
        await map.OpenWholeIslandAsync();
        var farClock = Stopwatch.StartNew();
        if (map.FarModelsLoading is { } far)
        {
            await far; // the game's far models join the island once, before the flight (their load is not a streaming step)
        }

        var backdropScene = map.WorldBackdrop ?? throw new InvalidOperationException("no island backdrop");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"island backdrop: {backdropScene.Placements.Count:N0} placements (far models waited {farClock.Elapsed.TotalSeconds:0.0} s), render distance {map.RenderDistance} sectors of {map.SectorSizeCm / 100f:0} m"));

        // The flight: from the middle of the B_4 outpost to the middle of A_4, then west to the A_3/A_4 border.
        var outpost = world.Packages.Single(p => p.PackagePath.EndsWith("/B_4_Outpost", StringComparison.OrdinalIgnoreCase)).Tile!;
        var start = (outpost.BoundsMin + outpost.BoundsMax) * 0.5f;
        FVector[] path = [start, new(466_800f, -447_600f, 0f), new(314_400f, -447_600f, 0f)];
        if (debug)
        {
            path = [.. path, path[1], start]; // and back: what streamed out must leave the GPU (D03: "every 2 minutes")
        }

        var legs = Enumerable.Range(0, path.Length - 1).Select(i => FVector.Distance(path[i] with { Z = 0f }, path[i + 1] with { Z = 0f })).ToArray();
        var length = legs.Sum();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"flight {length / 100f:0} m at {SpeedCmPerSecond / 100f:0} m/s, quality {map.RenderQuality} (radius {map.Quality.StreamRadiusCm / 100f:0} m), {Width}x{Height}, server GC {System.Runtime.GCSettings.IsServerGC} (DATAS {Environment.GetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode") ?? "-"}), viewport: {ViewportReplica.Mode}"));

        using var context = OffscreenGlContext.Create(Width, Height, debug);
        using var capture = debug ? GlDebugOutput.Capture(context.Gl) : null;
        using var renderer = new SceneRenderer(context);
        using var target = renderer.CreateTarget(Width, Height, samples: debug ? 4 : 1); // the Map's anti-aliased target
        var quality = map.Quality;
        renderer.Settings = renderer.Settings with
        {
            ShowGrid = true, ObjectDrawDistance = quality.ObjectDistanceCm, LodBias = quality.LodBias, CullPixelSize = quality.CullPixels, Shadows = debug,
        };
        var detailSettings = renderer.Settings;
        var backdropSettings = FrameBudget.Backdrop(detailSettings, map.RenderDistance, map.SectorSizeCm); // as LevelViewport draws the island
        using var view = new ViewportReplica(renderer);
        view.ShowBackdrop(backdropScene);
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 3_000_000f);
        camera.Position = UeToGl.Point(start with { Z = (backdropScene.HeightField?.SampleHeight(start.X, start.Y) ?? 0f) + HeightCm });
        renderer.Render(target, view.Backdrop!.Scene, camera); // the driver compiles its shaders on the first frame: not part of the flight
        context.Gl.Finish();

        var frames = new List<double>();
        var slow = new List<string>();
        var gpu = new List<(int Meshes, long MeshBytes, int Textures, long TextureBytes)>(); // alive after each streaming step (debug run)
        var steps = new List<string>();
        var clock = Stopwatch.StartNew();
        var flown = 0f;
        var nextCheck = 0.0;
        var lastLoad = map.LastLoadTimings;
        var gcPause = GC.GetTotalPauseDuration();
        var gen2 = GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes();
        PreparedLevelScene? shown = null;
        var shownLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var frameStart = clock.Elapsed.TotalSeconds;
            var at = PointAt(path, legs, flown);
            var ahead = PointAt(path, legs, MathF.Min(length, flown + 1_000f)) - at;
            var ground = backdropScene.HeightField?.SampleHeight(at.X, at.Y) ?? 0f;
            camera.Position = UeToGl.Point(at with { Z = ground + HeightCm });
            camera.Yaw = MathF.Atan2(ahead.Y, ahead.X) * (180f / MathF.PI);
            camera.Pitch = -15f;

            // The map page's half-second timer, then the dispatcher's queued jobs (a finished load shows its scene here).
            var ui = Stopwatch.StartNew();
            if (frameStart >= nextCheck)
            {
                nextCheck = frameStart + 0.5;
                map.UpdateWorldCamera(UeToGl.ToUePoint(camera.Position));
            }

            FrameThread.RunPending();
            var uiMs = ui.Elapsed.TotalMilliseconds;

            // LevelViewport.OnOpenGlRender: a new scene is uploaded and the page's state applied, then the frame is drawn.
            string? step = null;
            if (map.PreparedScene is { } prepared && view.Frame(prepared, map) is { } upload)
            {
                var levels = prepared.Documents.Select(d => d.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var cameIn = levels.Count(l => !shownLevels.Contains(l));
                var wentOut = shownLevels.Count(l => !levels.Contains(l));
                shown = prepared;
                shownLevels = levels;
                var load = map.LastLoadTimings;
                step = string.Create(CultureInfo.InvariantCulture,
                    $"+{cameIn,2} -{wentOut,2} = {levels.Count,3} levels {prepared.Placements.Count,7:N0} placements | worker: read {load?.ReadMs,6:0} prepare {load?.PrepareMs,6:0} ms, allocating {load?.ReadMb}+{load?.PrepareMb} MB | frame thread: show {load?.ShowMs,5:0} upload {upload.UploadMs,5:0} ({upload.Detail}) apply {upload.ApplyMs,5:0}");
                lastLoad = load;
                var allocated = GC.GetTotalAllocatedBytes();
                step += string.Create(CultureInfo.InvariantCulture, $" | {(allocated - allocatedBefore) >> 20} MB allocated since the last step |");
                if (debug)
                {
                    var (meshCount, meshBytes) = GpuMesh.Live;
                    var (textureCount, textureBytes) = GpuTexture.Live;
                    gpu.Add((meshCount, meshBytes, textureCount, textureBytes));
                    step += string.Create(CultureInfo.InvariantCulture, $" GPU: {meshCount} meshes {meshBytes >> 20} MB, {textureCount} textures {textureBytes >> 20} MB, {FreeVideoMemoryMb(context.Gl)} MB free |");
                }

                allocatedBefore = allocated;
            }

            var rebuildBefore = renderer.LastRebuild;
            var draw = Stopwatch.StartNew();
            renderer.Settings = backdropSettings;
            renderer.Render(target, view.Backdrop!.Scene, camera);
            renderer.Settings = detailSettings;
            var stats = renderer.Render(target, view.Level?.Scene ?? EmptyScene, camera, clear: false);
            if (debug && frames.Count % 30 == 0 && view.Level is { } pickable)
            {
                _ = renderer.Pick(target, pickable.Scene, camera, Width / 2, Height / 2, radius: 4); // a click on the Map
            }

            var drawCpuMs = draw.Elapsed.TotalMilliseconds;
            context.Gl.Finish();
            var drawMs = draw.Elapsed.TotalMilliseconds;
            var frameMs = (clock.Elapsed.TotalSeconds - frameStart) * 1000.0;
            var pause = GC.GetTotalPauseDuration();
            var pauseMs = (pause - gcPause).TotalMilliseconds;
            gcPause = pause;
            var collections = GC.CollectionCount(2) - gen2;
            gen2 += collections;
            frames.Add(frameMs);
            if (step is not null)
            {
                steps.Add(string.Create(CultureInfo.InvariantCulture, $"{frameStart,5:0.0}s {step} first draw {drawMs,5:0} (cpu {drawCpuMs:0}){Rebuild(ReferenceEquals(renderer.LastRebuild, rebuildBefore) ? null : renderer.LastRebuild)} | frame {frameMs,5:0} ms (gc pause {pauseMs:0} ms)"));
            }

            if (frameMs > 50.0)
            {
                slow.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{frameStart,5:0.0}s frame {frameMs,5:0} ms: ui {uiMs,5:0} draw {drawMs,5:0} gc pause {pauseMs,4:0} ms ({collections} gen2; last GC: {LastGc()}) {stats.Instances:N0} drawn{(step is null ? string.Empty : " [streaming step]")}"));
            }

            if (flown >= length && !map.IsLoadingLevel && ReferenceEquals(map.PreparedScene, shown))
            {
                break;
            }

            flown = MathF.Min(length, (float)(clock.Elapsed.TotalSeconds * SpeedCmPerSecond));
        }

        var sorted = frames.Order().ToList();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{frames.Count} frames in {clock.Elapsed.TotalSeconds:0.0} s: p50 {Percentile(sorted, 0.50):0.0} ms, p95 {Percentile(sorted, 0.95):0.0} ms, p99 {Percentile(sorted, 0.99):0.0} ms, worst {sorted[^1]:0} ms; {frames.Count(f => f > 50.0)} frames over 50 ms, {frames.Count(f => f > 100.0)} over 100 ms; {steps.Count} streaming steps"));
        using (var process = Process.GetCurrentProcess())
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"peak working set {process.PeakWorkingSet64 >> 20} MB, GC pauses {GC.GetTotalPauseDuration().TotalMilliseconds:0} ms in all, {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} collections (gen0/1/2)"));
        }

        output.WriteLine("Streaming steps:");
        steps.ForEach(output.WriteLine);
        output.WriteLine("Frames over 50 ms:");
        slow.ForEach(output.WriteLine);
        Assert.True(steps.Count >= 3, $"{steps.Count} streaming steps");
        if (capture is not null)
        {
            var messages = capture.Messages;
            output.WriteLine($"Driver messages: {messages.Count}, problems: {capture.Problems.Count} ({renderer.Info})");
            foreach (var group in messages.GroupBy(m => (m.Type, m.Severity, m.Id)).OrderByDescending(g => g.First().IsProblem))
            {
                output.WriteLine($"{group.Count(),6}x {group.First()}");
            }

            Assert.True(capture.Problems.Count == 0, string.Join(Environment.NewLine, capture.Problems.Distinct().Take(20)));

            // Back where it started: what streamed out went from the GPU too (a leak would grow step after step).
            var (first, last, most) = (gpu[0], gpu[^1], gpu.Max(g => g.TextureBytes + g.MeshBytes));
            output.WriteLine($"GPU at the first step {first}, at the last {last}, most {most >> 20} MB");
            Assert.True(last.TextureBytes + last.MeshBytes <= (first.TextureBytes + first.MeshBytes) * 5 / 4,
                $"back at the start the GPU holds {(last.TextureBytes + last.MeshBytes) >> 20} MB, {(first.TextureBytes + first.MeshBytes) >> 20} MB when it set off");
        }
    }

    private static string Rebuild(BatchRebuild? rebuild) => rebuild is null
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $" ({(rebuild.Partial ? "partial" : "full")} batch build {rebuild.BuildMs:0} ms, sent {rebuild.Batches} batches / {rebuild.Instances:N0} instances in {rebuild.UploadMs:0} ms)");

    private static string LastGc()
    {
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        return string.Create(CultureInfo.InvariantCulture,
            $"gen{info.Generation}{(info.Compacted ? " compacting" : string.Empty)}{(info.Concurrent ? " background" : string.Empty)} pause {(info.PauseDurations.Length > 0 ? info.PauseDurations[0].TotalMilliseconds : 0):0} ms, promoted {info.PromotedBytes >> 20} MB, heap {info.HeapSizeBytes >> 20} MB");
    }

    /// <summary>NVX_gpu_memory_info's free video memory in MB, or -1 on a driver without it.</summary>
    private static long FreeVideoMemoryMb(Silk.NET.OpenGL.GL gl) =>
        GlInfo.HasExtension(gl, "GL_NVX_gpu_memory_info") ? gl.GetInteger((Silk.NET.OpenGL.GetPName)0x9049) / 1024 : -1; // CURRENT_AVAILABLE_VIDMEM_NVX, KB

    private static double Percentile(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static FVector PointAt(FVector[] path, float[] legs, float distance)
    {
        for (var i = 0; i < legs.Length; i++)
        {
            if (distance <= legs[i] || i == legs.Length - 1)
            {
                var t = legs[i] <= 0f ? 1f : Math.Clamp(distance / legs[i], 0f, 1f);
                return path[i] + ((path[i + 1] - path[i]) * t) with { Z = 0f };
            }

            distance -= legs[i];
        }

        return path[^1];
    }

    /// <summary>
    /// The part of <c>LevelViewport.OnOpenGlRender</c> that takes a new scene from the page and applies the page's state to
    /// it: the scene's new meshes and textures staged a few milliseconds a frame, then <see cref="LevelScene.Update"/>; with
    /// <c>SCUMSTUDIO_BENCH_FULL=1</c> the way it was before (the old scene thrown away, the new one uploaded whole at once).
    /// </summary>
    private sealed class ViewportReplica(SceneRenderer renderer) : IDisposable
    {
        private const double StageBudgetMs = 4.0; // LevelViewport.StageBudgetMovingMs: the camera always moves here
        private static readonly bool Full = Environment.GetEnvironmentVariable("SCUMSTUDIO_BENCH_FULL") == "1";
        private readonly GpuMeshCache _cache = new();
        private PreparedLevelScene? _shown;
        private double _stagedMs;
        private int _stagedFrames;

        public LevelScene? Level { get; private set; }

        public LevelScene? Backdrop { get; private set; }

        public static string Mode => Full ? "full re-upload (old)" : "staged + incremental (new)";

        public void ShowBackdrop(PreparedLevelScene island) => Backdrop = LevelSceneUploader.Upload(renderer, island);

        /// <summary>One frame's scene work: null while nothing new is shown, else what showing <paramref name="pending"/> cost.</summary>
        public (double UploadMs, string Detail, double ApplyMs)? Frame(PreparedLevelScene? pending, MapPageViewModel map)
        {
            if (pending is null || ReferenceEquals(pending, _shown))
            {
                return null;
            }

            var clock = Stopwatch.StartNew();
            if (!Full && !(_cache.Stage(renderer, pending, StageBudgetMs) && (Level?.Prebuild(pending) ?? true)))
            {
                _stagedMs += clock.Elapsed.TotalMilliseconds;
                _stagedFrames++;
                return null;
            }

            var staged = string.Create(CultureInfo.InvariantCulture, $"staged {_stagedMs:0} ms over {_stagedFrames} frames, ");
            (_stagedMs, _stagedFrames) = (0, 0);
            clock.Restart();
            if (Full || Level is null)
            {
                Level?.Dispose();
                Level = LevelSceneUploader.Upload(renderer, pending, cache: _cache);
            }
            else
            {
                Level.Update(pending);
            }

            _cache.Trim(renderer);
            _shown = pending;
            var prepared = pending;
            var uploadMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var level = Level;
            foreach (var (id, pins) in map.PinOverrides)
            {
                level.ReplacePins(id, pins);
            }

            if (map.SelectedInstanceKey is { } instance && instance.SelectableId == map.SelectedActorId)
            {
                level.SelectInstance(instance);
            }
            else
            {
                level.SetSelection(map.SelectedActorId == 0 ? [] : [map.SelectedActorId]);
            }

            level.HighlightAlso(map.KindSelectionIds, map.KindSelectionInstances);
            foreach (var extra in map.ExtraMeshes)
            {
                level.AddMesh(extra);
            }

            foreach (var clone in map.Clones)
            {
                level.AddClone(clone.Id, clone.SourceId, clone.RootWorld, clone.Name, clone.MeshPath, clone.Placements);
            }

            foreach (var (id, rootWorld) in map.ActorTransforms)
            {
                level.SetActorTransform(id, rootWorld);
            }

            level.SetInstanceTransforms(map.InstanceTransforms);
            var hidden = map.HiddenActorIds;
            var hiddenInstances = map.HiddenInstanceKeys;
            var hiddenPins = map.HiddenPinKinds;
            foreach (var node in level.Scene.Nodes)
            {
                var visible = node.SelectableId == 0 || !hidden.Contains(node.SelectableId);
                if (visible && hiddenInstances.Count > 0 && node.Tag is ScenePlacement { InstanceKey: { } key })
                {
                    visible = !hiddenInstances.Contains(key);
                }

                if (visible && hiddenPins.Count > 0 && node.Tag is ScenePlacement pin && SpawnMarkers.KindOfMesh(pin.MeshPath) is { } kind)
                {
                    visible = !hiddenPins.Contains(kind);
                }

                node.Visible = visible;
            }

            // The tiles and far models of the levels drawn in detail are hidden in the backdrop (LevelScene.HideTerrainOf).
            Backdrop!.HideTerrainOf(prepared);

            var t = level.UploadTimings!;
            var detail = staged + string.Create(CultureInfo.InvariantCulture, $"textures {t.TexturesMs:0} meshes {t.MeshesMs:0} ({t.MeshesUploaded} new) nodes {t.NodesMs:0} terrain {t.TerrainMs:0}");
            return (uploadMs, detail, clock.Elapsed.TotalMilliseconds);
        }

        public void Dispose()
        {
            Level?.Dispose();
            Backdrop?.Dispose();
        }
    }

    /// <summary>
    /// A one-thread synchronization context, like Avalonia's UI thread: continuations posted to it run when the frame loop
    /// calls <see cref="RunPending"/> (once a frame), on the thread that draws.
    /// </summary>
    private sealed class FrameThread : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        [ThreadStatic]
        private static FrameThread? _current;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        /// <summary>Runs <paramref name="body"/> on this thread with the context installed, pumping until it completes.</summary>
        public static void Run(Func<Task> body)
        {
            var context = _current = new FrameThread();
            SetSynchronizationContext(context);
            var task = body();
            while (!task.IsCompleted)
            {
                if (context._queue.TryTake(out var item, 20))
                {
                    item.Callback(item.State);
                }
            }

            task.GetAwaiter().GetResult();
        }

        /// <summary>Runs every job queued so far.</summary>
        public static void RunPending()
        {
            var queue = _current!._queue;
            for (var n = queue.Count; n > 0 && queue.TryTake(out var item); n--)
            {
                item.Callback(item.State);
            }
        }
    }
}
