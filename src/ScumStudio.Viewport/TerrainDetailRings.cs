using System.Diagnostics;
using System.Numerics;
using ScumStudio.Assets.Landscape;
using ScumStudio.Core.Mathematics;
using ScumStudio.Core.Settings;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Resources;

namespace ScumStudio.Viewport;

/// <summary>How much of the ground near the camera <see cref="TerrainDetailRings"/> draws from every landscape sample.</summary>
/// <param name="FullRadiusCm">Landscape components this close to the camera are drawn from every sample (1.5 m apart, as the game draws the ground near the player).</param>
/// <param name="HalfRadiusCm">Components this close are drawn from every second sample (3 m).</param>
/// <param name="VertexBudget">At most this many vertices in the finer components together (nearest first; the rest stay as loaded).</param>
public sealed record TerrainDetailSettings(float FullRadiusCm, float HalfRadiusCm, int VertexBudget)
{
    /// <summary>No finer ground: every component as it was loaded.</summary>
    public static TerrainDetailSettings Off { get; } = new(0f, 0f, 0);

    /// <summary>True when some ground can be drawn finer.</summary>
    public bool Enabled => VertexBudget > 0 && (FullRadiusCm > 0f || HalfRadiusCm > 0f);

    /// <summary>
    /// The rings of a Map quality. A landscape component is 381 m across and 65,025 vertices drawn from every sample
    /// (16,384 from every second one, 4,225 as whole-island mode loads it), so Performance keeps the one or two under the
    /// camera, Balanced about six, High the nine around it plus a second ring.
    /// </summary>
    public static TerrainDetailSettings For(RenderQuality quality) => quality switch
    {
        RenderQuality.Performance => Performance,
        RenderQuality.High => High,
        RenderQuality.Ultra or RenderQuality.GameLike => Ultra,
        _ => Balanced,
    };

    // One object per quality: the Map asks for its settings every frame.
    private static readonly TerrainDetailSettings Performance = new(15_000f, 0f, 135_000);
    private static readonly TerrainDetailSettings Balanced = new(25_000f, 60_000f, 450_000);
    private static readonly TerrainDetailSettings High = new(30_000f, 80_000f, 700_000);
    private static readonly TerrainDetailSettings Ultra = new(40_000f, 100_000f, 1_000_000);
}

/// <summary>
/// Draws the landscape near the camera from all of its samples. Whole-island and cell views load the ground at every fourth
/// sample (6 m apart: a quarter of the vertices in each direction), and up close that is not the ground the game draws:
/// ditches, banks and bumps were smoothed away, and objects placed on the real ground looked buried or afloat (pheidian:
/// "it shows only the lowest LOD I think, and it doesnt match at all when placing stuff on ground").
/// <para>Each frame <see cref="Update"/> gives every visible component a step by its distance from the camera
/// (<see cref="TerrainDetailSettings"/>: every sample in the near ring, every second one in the next, nearest first within a
/// vertex budget, the rest as loaded); a component whose step changes gets its vertex data rebuilt from the full-resolution
/// heights on a worker and swapped into the same GPU mesh (<see cref="SceneRenderer.ReplaceMeshGeometry"/>), a few
/// milliseconds a frame. The border of a finer component keeps the loaded step's heights
/// (<see cref="LandscapeExtractor.BuildMesh"/>), so neighbours meet without cracks whatever their step.</para>
/// </summary>
public sealed class TerrainDetailRings
{
    /// <summary>A ring a component already in it keeps until it is this much farther out (no flicker at the line).</summary>
    public const float Hysteresis = 1.15f;

    // The step each GPU mesh draws now (meshes not in here draw their loaded step).
    private readonly Dictionary<int, int> _shown = [];

    // Vertex data being built, by GPU mesh id: the component, the step and the worker's result.
    private readonly Dictionary<int, (PreparedTerrain Terrain, int Step, Task<PreparedMesh> Task)> _building = [];

    // Meshes whose finer data could not be sent (it would not fit their bounds): they stay as loaded.
    private readonly HashSet<int> _stuck = [];

    // The terrain list, settings and camera position the last finished pass settled for (see Update).
    private IReadOnlyList<TerrainPart>? _settledFor;
    private TerrainDetailSettings? _settledSettings;
    private Vector3 _settledEye;

    /// <summary>The camera moves this far (cm) before an idle pass looks at the rings again.</summary>
    private const float SettledMoveCm = 200f;

    /// <summary>The rings and the budget (<see cref="TerrainDetailSettings.Off"/> puts every component back as loaded).</summary>
    public TerrainDetailSettings Settings { get; set; } = TerrainDetailSettings.Off;

    /// <summary>Components drawn finer than loaded (diagnostics).</summary>
    public int FinerCount => _shown.Count(s => s.Value > 0);

    /// <summary>Vertices of the components drawn finer than loaded (diagnostics).</summary>
    public long FinerVertices { get; private set; }

    /// <summary>Vertex data swapped into GPU meshes so far (diagnostics).</summary>
    public int Swaps { get; private set; }

    /// <summary>
    /// Brings the terrain of <paramref name="scene"/> to the steps its distance from <paramref name="eyeGl"/> (the camera, GL
    /// world) asks for, sending at most about <paramref name="budgetMs"/> of vertex data this call (render thread, GL context
    /// current). Returns true while work is left (call again next frame).
    /// </summary>
    public bool Update(SceneRenderer renderer, LevelScene? scene, Vector3 eyeGl, double budgetMs)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (scene is null || scene.IsDisposed)
        {
            _building.Clear(); // what the workers still build is dropped; the meshes keep the data they have
            _settledFor = null;
            return false;
        }

        var ue = UeToGl.ToUePoint(eyeGl);
        var eye = new Vector3(ue.X, ue.Y, ue.Z);

        // At rest nothing changes: no work (and nothing allocated) until the camera moves a few metres or the scene changes.
        if (_building.Count == 0 && ReferenceEquals(_settledFor, scene.TerrainParts) && Settings == _settledSettings
            && Vector3.DistanceSquared(eye, _settledEye) < SettledMoveCm * SettledMoveCm)
        {
            return false;
        }

        Forget(renderer);
        var wanted = Plan(scene, eye, Settings);
        var clock = Stopwatch.StartNew();
        var pending = false;
        foreach (var (part, step) in wanted)
        {
            var id = part.Handle.Id;
            var shown = _shown.GetValueOrDefault(id);
            if (shown == step)
            {
                _building.Remove(id); // wanted back where it is: a build under way is not needed
                continue;
            }

            if (_building.TryGetValue(id, out var build) && (build.Step != step || !ReferenceEquals(build.Terrain, part.Source)))
            {
                _building.Remove(id); // the camera moved on before it was done
                build = default;
            }

            if (build.Task is null)
            {
                var (terrain, finer) = (part.Source, step);
                _building[id] = (terrain, finer, Task.Run(() => Pack(terrain, finer)));
                pending = true;
                continue;
            }

            if (!build.Task.IsCompleted || clock.Elapsed.TotalMilliseconds >= budgetMs)
            {
                pending = true;
                continue;
            }

            _building.Remove(id);
            if (!build.Task.IsCompletedSuccessfully || !renderer.Meshes.TryGetValue(id, out var gpu) || !Inside(build.Task.Result, gpu.Bounds))
            {
                _stuck.Add(id); // nothing to send (the data would poke out of the mesh's bounds): it stays as loaded
                continue;
            }

            renderer.ReplaceMeshGeometry(part.Handle, build.Task.Result);
            Swaps++;
            if (step == 0)
            {
                _shown.Remove(id);
            }
            else
            {
                _shown[id] = step;
            }
        }

        // Builds for components that left the scene are not needed any more.
        if (_building.Count > 0)
        {
            var ids = wanted.Select(w => w.Part.Handle.Id).ToHashSet();
            foreach (var id in _building.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                _building.Remove(id);
            }
        }

        FinerVertices = 0;
        foreach (var (part, _) in wanted)
        {
            if (_shown.GetValueOrDefault(part.Handle.Id) is > 0 and var s)
            {
                var verts = LandscapeExtractor.VertsPerSide(part.Source.Surface!.ComponentSizeQuads, s);
                FinerVertices += (long)verts * verts;
            }
        }

        var done = !pending && _building.Count == 0;
        (_settledFor, _settledSettings, _settledEye) = (done ? scene.TerrainParts : null, Settings, eye);
        return !done;
    }

    /// <summary>Forgets every mesh (the GL context goes away with them).</summary>
    public void Clear()
    {
        _shown.Clear();
        _building.Clear();
        _stuck.Clear();
        _settledFor = null;
        FinerVertices = 0;
    }

    /// <summary>
    /// The step each visible terrain component of <paramref name="scene"/> should be drawn at for a camera at
    /// <paramref name="eyeUe"/> (UE world centimetres): 1 or 2 for a finer one, 0 for the step it was loaded at.
    /// </summary>
    internal List<(TerrainPart Part, int Step)> Plan(LevelScene scene, Vector3 eyeUe, TerrainDetailSettings settings)
    {
        var candidates = new List<(TerrainPart Part, float Distance, int Loaded)>();
        foreach (var part in scene.TerrainParts)
        {
            if (part.Source.Surface is not { } surface || LoadedStep(part.Source) is not { } loaded)
            {
                continue;
            }

            var visible = part.Node.IsEffectivelyVisible && ReferenceEquals(part.Node.Mesh, part.Handle);
            candidates.Add((part, visible ? DistanceCm(surface, eyeUe) : float.PositiveInfinity, loaded));
        }

        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var result = new List<(TerrainPart, int)>(candidates.Count);
        long used = 0;
        foreach (var (part, distance, loaded) in candidates)
        {
            var size = part.Source.Surface!.ComponentSizeQuads;
            var shown = _shown.GetValueOrDefault(part.Handle.Id);
            var step = 0;
            if (settings.Enabled && !_stuck.Contains(part.Handle.Id))
            {
                foreach (var (finer, radius) in new[] { (1, settings.FullRadiusCm), (2, settings.HalfRadiusCm) })
                {
                    var reach = shown > 0 && shown <= finer ? radius * Hysteresis : radius;
                    var verts = (long)LandscapeExtractor.VertsPerSide(size, finer) * LandscapeExtractor.VertsPerSide(size, finer);
                    if (finer < loaded && distance <= reach && used + verts <= settings.VertexBudget)
                    {
                        step = finer;
                        used += verts;
                        break;
                    }
                }
            }

            result.Add((part, step));
        }

        return result;
    }

    /// <summary>
    /// The vertex step <paramref name="terrain"/> was loaded at (from its vertex count), or null when it is not a square grid
    /// of one (nothing to refine).
    /// </summary>
    internal static int? LoadedStep(PreparedTerrain terrain)
    {
        if (terrain.Surface is not { } surface)
        {
            return null;
        }

        var verts = (int)Math.Round(Math.Sqrt(terrain.Mesh.VertexCount));
        if (verts < 2 || verts * verts != terrain.Mesh.VertexCount)
        {
            return null;
        }

        var size = surface.ComponentSizeQuads;
        var step = (int)Math.Ceiling(size / (double)(verts - 1));
        return LandscapeExtractor.VertsPerSide(size, step) == verts ? step : null;
    }

    /// <summary>Distance in cm from <paramref name="eyeUe"/> to the ground of <paramref name="surface"/>: across its footprint, and above its highest or below its lowest sample.</summary>
    internal static float DistanceCm(LandscapeSurface surface, Vector3 eyeUe)
    {
        var dx = MathF.Max(0f, MathF.Max(surface.WorldMinXY.X - eyeUe.X, eyeUe.X - surface.WorldMaxXY.X));
        var dy = MathF.Max(0f, MathF.Max(surface.WorldMinXY.Y - eyeUe.Y, eyeUe.Y - surface.WorldMaxXY.Y));
        var dz = MathF.Max(0f, MathF.Max(surface.MinHeightCm - eyeUe.Z, eyeUe.Z - surface.MaxHeightCm));
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>The vertex data of <paramref name="terrain"/> at <paramref name="step"/> (0 = as loaded), CPU only.</summary>
    internal static PreparedMesh Pack(PreparedTerrain terrain, int step)
    {
        if (step == 0 || terrain.Surface is not { } surface || LoadedStep(terrain) is not { } loaded)
        {
            return LevelSceneUploader.PackTerrain(terrain);
        }

        var mesh = LandscapeExtractor.BuildMesh(surface, step, loaded, computeNormals: terrain.Mesh.Normals.Length > 0);
        return PreparedMesh.From(mesh, MeshSpace.Unreal, 1f);
    }

    /// <summary>Drops what belongs to meshes the renderer no longer has.</summary>
    private void Forget(SceneRenderer renderer)
    {
        foreach (var id in _shown.Keys.Where(id => !renderer.Meshes.ContainsKey(id)).ToList())
        {
            _shown.Remove(id);
        }

        foreach (var id in _building.Keys.Where(id => !renderer.Meshes.ContainsKey(id)).ToList())
        {
            _building.Remove(id);
        }

        _stuck.RemoveWhere(id => !renderer.Meshes.ContainsKey(id));
    }

    private static bool Inside(PreparedMesh mesh, Core.Geometry.BoundingBox bounds)
    {
        const float slack = 1f; // cm
        var b = mesh.Bounds;
        return !b.IsEmpty && b.Min.X >= bounds.Min.X - slack && b.Min.Y >= bounds.Min.Y - slack && b.Min.Z >= bounds.Min.Z - slack
               && b.Max.X <= bounds.Max.X + slack && b.Max.Y <= bounds.Max.Y + slack && b.Max.Z <= bounds.Max.Z + slack;
    }
}
