using ScumStudio.Assets.Textures;
using ScumStudio.Level.Model;

namespace ScumStudio.Viewport;

/// <summary>
/// What <see cref="LevelScenePreparer.Prepare"/> computed for earlier scenes, kept so a moving camera that loads the
/// levels around it reads, decodes, bends and bakes each mesh, texture, spline piece and terrain tile once: meshes and
/// their textures, bent spline copies and baked terrain. Entries the last <see cref="KeepGenerations"/> preparations did
/// not use are dropped after each preparation. One preparation at a time uses a cache (it is locked while in use).
/// </summary>
public sealed class LevelPrepareCache
{
    /// <summary>Preparations an unused entry survives (going back to where the camera just was costs nothing).</summary>
    public const int KeepGenerations = 2;

    internal readonly object Gate = new();
    internal readonly Dictionary<string, (PreparedMeshAsset? Asset, string Reason, int Used)> Meshes = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, TextureImage> Textures = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, MaterialLook> Materials = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<(string Path, SplineMeshParams Spline), (string Key, PreparedMeshAsset Asset, int Used)> Splines = [];
    internal readonly Dictionary<string, (List<PreparedTerrain> Terrain, int Used)> Terrain = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, (PreparedMeshAsset? Asset, int Used)> Variants = new(StringComparer.OrdinalIgnoreCase);
    internal TerrainBakeSettings? BakeSettings;
    internal IReadOnlyList<string> MissingLayerTextures = [];
    internal string? OptionsKey;
    internal int NextSplineId;

    /// <summary>The number of the preparation in progress (entries remember the last one that used them).</summary>
    internal int Generation { get; private set; }

    /// <summary>Distinct meshes kept (diagnostics).</summary>
    public int MeshCount
    {
        get
        {
            lock (Gate)
            {
                return Meshes.Count + Splines.Count;
            }
        }
    }

    /// <summary>Starts a preparation with <paramref name="options"/>; a change of mesh or ground options empties the cache.</summary>
    internal void Begin(LevelSceneOptions options)
    {
        var key = $"{options.Lod}|{options.MaxLods}|{options.TextureSize}|{options.LandscapeStep}|{options.Ground}|{options.TerrainTextureSize}|{options.ResolveLayerTextures}|{options.SeaLevelCm}";
        if (key != OptionsKey)
        {
            Meshes.Clear();
            Textures.Clear();
            Materials.Clear();
            Splines.Clear();
            Terrain.Clear();
            Variants.Clear();
            BakeSettings = null;
            MissingLayerTextures = [];
            OptionsKey = key;
        }

        Generation++;
    }

    /// <summary>Drops what the last <see cref="KeepGenerations"/> preparations did not use, and textures no kept mesh uses.</summary>
    internal void Trim()
    {
        var oldest = Generation - KeepGenerations + 1;
        foreach (var key in Meshes.Where(m => m.Value.Used < oldest).Select(m => m.Key).ToList())
        {
            Meshes.Remove(key);
        }

        foreach (var key in Splines.Where(s => s.Value.Used < oldest).Select(s => s.Key).ToList())
        {
            Splines.Remove(key);
        }

        foreach (var key in Terrain.Where(t => t.Value.Used < oldest).Select(t => t.Key).ToList())
        {
            Terrain.Remove(key);
        }

        foreach (var key in Variants.Where(v => v.Value.Used < oldest).Select(v => v.Key).ToList())
        {
            Variants.Remove(key);
        }

        var used = Meshes.Values.Select(m => m.Asset).Concat(Variants.Values.Select(v => v.Asset)).Concat(Splines.Values.Select(s => s.Asset))
            .OfType<PreparedMeshAsset>().SelectMany(TexturePathsOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Textures.Keys.Where(p => !used.Contains(p)).ToList())
        {
            Textures.Remove(path);
        }
    }

    /// <summary>Every texture a prepared mesh draws with.</summary>
    internal static IEnumerable<string> TexturePathsOf(PreparedMeshAsset asset) =>
        asset.TexturePath is { } main ? asset.MaterialTextures.Values.Append(main) : asset.MaterialTextures.Values;
}
