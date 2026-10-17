using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Viewport;

/// <summary>
/// The person the 3D previews stand beside a model so its size reads at a glance (owner, 2026-10-17: "the box man looks
/// bad; put a real character of the game"): the game's own prisoner (<see cref="CharacterMesh"/>, the orange-suited
/// survivor every player starts as) with his textures, standing, in UE space (centimetres, facing +X, feet at Z 0).
/// It comes from the game files, so it exists only once a catalog is open (<see cref="Load"/>): until then, or when the
/// mesh cannot be read, no figure is drawn at all (the metre grid still gives the scale), never a stand-in of boxes.
/// </summary>
/// <remarks>
/// Use from any preview: call <see cref="Load"/> (or <see cref="LoadAsync"/>) once with the open catalog, then
/// <see cref="Character"/> is the figure's model and <see cref="Placement"/> where it stands beside a model;
/// <see cref="PreviewScene.ShowScale"/> does both. <see cref="CharacterChanged"/> tells views to draw it once it arrived.
/// </remarks>
public static class ScaleFigure
{
    /// <summary>The game's prisoner: an armed-NPC body of the lowest level (orange jumpsuit, beanie), standing arms down.</summary>
    public const string CharacterMesh = "/Game/ConZ_Files/Characters/NPCs/Armed_NPCs/NPC_LVL_01/LVL_01_V01/SK_NPC_LVL_01_V01.SK_NPC_LVL_01_V01";

    /// <summary>Height the figure is drawn at when the game's mesh is not quite that tall (cm): SCUM's prisoners are 1.8 m.</summary>
    public const float Height = 180f;

    /// <summary>
    /// Models whose largest edge is shorter than this (cm) get no figure: next to a 1.8 m person a bullet or a key would be
    /// a speck; the centimetre grid and the size readout give their scale.
    /// </summary>
    public const float MinModelSize = 50f;

    /// <summary>Largest texture edge the figure is loaded with (it is a size cue, not a subject).</summary>
    private const int TextureSize = 1024;

    private static readonly object Gate = new();
    private static PreviewModel? _character;
    private static AssetCatalog? _loadedFrom;
    private static AssetCatalog? _loadingFor;
    private static Task<PreviewModel?>? _loading;
    private static int _generation;

    /// <summary>Raised (on the loading thread) when <see cref="Character"/> changed: views redraw to show it.</summary>
    public static event EventHandler? CharacterChanged;

    /// <summary>The figure (one part per material, UE space, facing +X, feet at Z 0, centred on X/Y 0), or null until loaded.</summary>
    public static PreviewModel? Character
    {
        get
        {
            lock (Gate)
            {
                return _character;
            }
        }
    }

    /// <summary>Half the figure's width, shoulder to shoulder (cm): the gap it keeps from the model.</summary>
    public static float HalfWidth => Character is { } c ? MathF.Max(20f, Bounds(c).Extent.Y) : 31f;

    /// <summary>
    /// Reads the figure from <paramref name="catalog"/> on this thread (a worker: 0.3-1 s) unless it is already loaded from
    /// it; null (logged) when the game files have no such mesh. Thread-safe: a load overtaken by a newer one (another
    /// catalog, <see cref="Forget"/>, <see cref="Use"/>) changes nothing.
    /// </summary>
    public static PreviewModel? Load(AssetCatalog catalog, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        int generation;
        lock (Gate)
        {
            if (ReferenceEquals(_loadedFrom, catalog))
            {
                return _character;
            }

            generation = ++_generation;
        }

        PreviewModel? figure = null;
        try
        {
            var loader = new MeshPreviewLoader(catalog, logger) { TextureSize = TextureSize, ForGpu = true, LodOf = lods => Math.Min(1, Math.Max(0, lods.Count - 1)) };
            var pose = catalog.TryLoadObject<USkeletalMesh>(CharacterMesh, out var skeletal) ? ArmsDown(MeshExtractor.ReferenceBones(skeletal)) : null;
            figure = Stand(loader.LoadMesh(CharacterMesh, pose: pose));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            (logger ?? NullLogger.Instance).LogInformation("No scale figure: {Mesh} could not be read ({Message}).", CharacterMesh, ex.Message);
        }

        lock (Gate)
        {
            if (generation != _generation)
            {
                return figure;
            }

            _character = figure;
            _loadedFrom = catalog;
        }

        CharacterChanged?.Invoke(null, EventArgs.Empty);
        return figure;
    }

    /// <summary><see cref="Load"/> on a worker, started once per catalog (later calls for it share the same task).</summary>
    public static Task<PreviewModel?> LoadAsync(AssetCatalog catalog, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (Gate)
        {
            if (ReferenceEquals(_loadedFrom, catalog))
            {
                return Task.FromResult(_character);
            }

            if (_loading is { IsCompleted: false } running && ReferenceEquals(_loadingFor, catalog))
            {
                return running;
            }

            _loadingFor = catalog;
            return _loading = Task.Run(() => Load(catalog, logger));
        }
    }

    /// <summary>Forgets the figure (the catalog it came from closed); no figure is drawn until the next <see cref="Load"/>.</summary>
    public static void Forget() => Use(null);

    /// <summary>Uses <paramref name="model"/> as the figure (tests, tools without a catalog); null removes it.</summary>
    public static void Use(PreviewModel? model)
    {
        var standing = model is null ? null : Stand(model);
        lock (Gate)
        {
            _generation++;
            _character = standing;
            _loadedFrom = null;
            _loadingFor = null;
        }

        CharacterChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The UE-space bounds of every part of <paramref name="model"/> where it stands.</summary>
    public static BoundingBox Bounds(PreviewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var box = BoundingBox.Empty;
        foreach (var part in model.Parts)
        {
            box = box.Union(BoundingBox.FromPositions(Moved(part.Mesh.Positions, part.Transform)));
        }

        return box;
    }

    /// <summary>
    /// Where the figure stands for a model with GL-space bounds <paramref name="model"/> seen from <paramref name="viewYaw"/>
    /// (the previews' default camera yaw): on the model's ground, just beside it on the view's right, turned to the camera.
    /// </summary>
    public static Matrix4x4 Placement(BoundingBox model, float viewYaw)
    {
        var (sy, cy) = MathF.SinCos(viewYaw * UeMath.DegreesToRadians);
        var right = new Vector3(-sy, 0f, cy); // FlyCamera.Right at that yaw
        var half = model.Extent;
        var reach = (MathF.Abs(right.X) * half.X) + (MathF.Abs(right.Z) * half.Z);
        var gap = MathF.Max(25f, MathF.Max(model.Size.X, MathF.Max(model.Size.Y, model.Size.Z)) * 0.05f);
        var at = model.Center + (right * (reach + gap + HalfWidth));
        at.Y = model.Min.Y;
        // Its +X (UE forward) towards the camera: the opposite of the camera's horizontal forward (cos yaw, 0, sin yaw).
        return Matrix4x4.CreateRotationY(MathF.Atan2(sy, -cy)) * Matrix4x4.CreateTranslation(at);
    }

    /// <summary>
    /// <paramref name="model"/> (a character mesh as <see cref="MeshPreviewLoader.LoadMesh"/> gives it) turned to face +X (a skeletal mesh of the game faces +Y), its feet put on Z 0 and its middle
    /// on X/Y 0, scaled to <see cref="Height"/> when it is shorter or taller by more than 5 %: one baked part per material.
    /// </summary>
    public static PreviewModel Stand(PreviewModel model)
    {
        var turn = Matrix4x4.CreateRotationZ(-MathF.PI / 2f); // +Y → +X
        var parts = model.Parts.Select(p => (Part: p, Positions: Turned(Moved(p.Mesh.Positions, p.Transform), turn))).ToList();
        var box = parts.Aggregate(BoundingBox.Empty, (b, p) => b.Union(BoundingBox.FromPositions(p.Positions)));
        if (box.IsEmpty)
        {
            return model with { Parts = [] };
        }

        var height = box.Size.Z;
        var scale = height > 1f && MathF.Abs((height / Height) - 1f) > 0.05f ? Height / height : 1f;
        var centre = new Vector3(box.Center.X, box.Center.Y, box.Min.Z);
        var standing = new List<PreviewPart>(parts.Count);
        foreach (var (part, positions) in parts)
        {
            for (var i = 0; i < positions.Length; i += 3)
            {
                positions[i] = (positions[i] - centre.X) * scale;
                positions[i + 1] = (positions[i + 1] - centre.Y) * scale;
                positions[i + 2] = (positions[i + 2] - centre.Z) * scale;
            }

            var normals = Turned(Normals(part.Mesh.Normals, part.Transform), turn);
            var mesh = MeshData.Create("ScaleFigure", positions, part.Mesh.Indices, normals.Length == positions.Length ? normals : null, part.Mesh.Uv0);
            standing.Add(part with { Name = "ScaleFigure", Mesh = mesh, Transform = FTransform.Identity, Attachment = string.Empty });
        }

        return model with { Name = "ScaleFigure", Parts = standing };
    }

    /// <summary>Arms in the game's bind pose spread to an A; this lowers each upper arm to hang 10° off the vertical.</summary>
    /// <returns>The upper arms' mesh-space transforms (the rest of the body follows its bones), or null when the skeleton has none.</returns>
    internal static Dictionary<string, FTransform>? ArmsDown(IReadOnlyList<SkinBone> bones)
    {
        var worlds = Skinning.BindWorlds(bones);
        var pose = new Dictionary<string, FTransform>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < bones.Count; i++)
        {
            var name = bones[i].Name;
            if (!name.Contains("upperarm", StringComparison.OrdinalIgnoreCase) || name.Contains("twist", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var child = Enumerable.Range(0, bones.Count).FirstOrDefault(c => bones[c].ParentIndex == i && !bones[c].Name.Contains("twist", StringComparison.OrdinalIgnoreCase), -1);
            if (child < 0)
            {
                continue;
            }

            var shoulder = worlds[i].Translation;
            var elbow = worlds[child].Translation;
            var arm = Vector3.Normalize(new Vector3(elbow.X - shoulder.X, elbow.Y - shoulder.Y, elbow.Z - shoulder.Z));
            var side = new Vector3(arm.X, arm.Y, 0f);
            if (side.LengthSquared() < 1e-4f)
            {
                continue; // already hanging
            }

            var target = Vector3.Normalize((-Vector3.UnitZ * MathF.Cos(0.1745f)) + (Vector3.Normalize(side) * MathF.Sin(0.1745f)));
            var axis = Vector3.Cross(arm, target);
            var angle = MathF.Acos(Math.Clamp(Vector3.Dot(arm, target), -1f, 1f));
            if (axis.LengthSquared() < 1e-8f || angle < 0.01f)
            {
                continue;
            }

            axis = Vector3.Normalize(axis);
            var turn = FQuat.FromAxisAngle(new FVector(axis.X, axis.Y, axis.Z), angle);
            var turned = turn.RotateVector(new FVector(arm.X, arm.Y, arm.Z));
            if (Vector3.Dot(new Vector3(turned.X, turned.Y, turned.Z), target) < 0.99f)
            {
                turn = FQuat.FromAxisAngle(new FVector(axis.X, axis.Y, axis.Z), -angle); // the other handedness
            }

            pose[name] = new FTransform(turn * worlds[i].Rotation, worlds[i].Translation, worlds[i].Scale3D);
        }

        return pose.Count == 0 ? null : pose;
    }

    private static float[] Moved(float[] positions, FTransform transform)
    {
        var moved = new float[positions.Length];
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = transform.TransformPosition(new FVector(positions[i], positions[i + 1], positions[i + 2]));
            (moved[i], moved[i + 1], moved[i + 2]) = (p.X, p.Y, p.Z);
        }

        return moved;
    }

    private static float[] Normals(float[] normals, FTransform transform)
    {
        var turned = new float[normals.Length];
        for (var i = 0; i + 2 < normals.Length; i += 3)
        {
            var n = transform.Rotation.RotateVector(new FVector(normals[i], normals[i + 1], normals[i + 2]));
            (turned[i], turned[i + 1], turned[i + 2]) = (n.X, n.Y, n.Z);
        }

        return turned;
    }

    private static float[] Turned(float[] xyz, Matrix4x4 turn)
    {
        for (var i = 0; i + 2 < xyz.Length; i += 3)
        {
            var v = Vector3.TransformNormal(new Vector3(xyz[i], xyz[i + 1], xyz[i + 2]), turn);
            (xyz[i], xyz[i + 1], xyz[i + 2]) = (v.X, v.Y, v.Z);
        }

        return xyz;
    }
}
