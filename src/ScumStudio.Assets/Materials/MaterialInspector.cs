using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;

namespace ScumStudio.Assets.Materials;

/// <summary>
/// Reads material instances (<c>UMaterialInstanceConstant</c>): scalar/vector/texture parameter values along the parent chain,
/// and guesses the base-colour texture and tint colour a simple viewport shader should use.
/// </summary>
public sealed class MaterialInspector
{
    /// <summary>Texture parameter names that usually hold the base colour, best first (compared case-insensitively, substring).</summary>
    public static IReadOnlyList<string> BaseColorTextureHints { get; } =
        ["Diffuse Map", "BaseColor", "Base Color", "Diffuse", "Albedo", "Color Map", "ColorMap", "Base_Color", "Color", "Texture"];

    /// <summary>
    /// Names that are the base colour only when they match exactly (SCUM's master shader calls it <c>Color</c>, while
    /// <c>Blend Color/Alpha</c> or <c>B Material Diffuse</c> are blend layers painted on top).
    /// </summary>
    public static IReadOnlyList<string> ExactBaseColorTextureNames { get; } = ["Color", "Main Color", "Base Map", "Albedo Map"];

    /// <summary>Vector parameter names that usually hold a tint or base colour, best first.</summary>
    public static IReadOnlyList<string> TintHints { get; } =
        ["BaseColor", "Base Color", "Diffuse Color", "DiffuseColor", "Color Tint", "Tint", "Albedo Color", "Color"];

    private const int MaxDepth = 16;
    private const string BrightnessName = "Base Material Brightness";
    private const string ScaleTintName = "Base Material Tint";
    private readonly AssetCatalog _catalog;

    /// <summary>Creates an inspector that resolves parents through <paramref name="catalog"/>.</summary>
    public MaterialInspector(AssetCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>Loads and inspects the material at <paramref name="objectPath"/>.</summary>
    public MaterialInfo Inspect(string objectPath) => Inspect(_catalog.LoadObject<UMaterialInterface>(objectPath));

    /// <summary>Inspects a loaded material or material instance.</summary>
    public MaterialInfo Inspect(UMaterialInterface material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var scalars = new Dictionary<string, ScalarParameter>(StringComparer.OrdinalIgnoreCase);
        var vectors = new Dictionary<string, VectorParameter>(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, TextureParameter>(StringComparer.OrdinalIgnoreCase);
        var chain = new List<string>();
        var referenced = new List<string>();

        UObject? current = material;
        Parameters master = new([], [], []);
        float? maskClip = null;
        float? clipOverride = null;
        var blend = EBlendMode.BLEND_Opaque;
        var blendKnown = false;
        var splineMeshes = false;
        var landscapeOutput = false;
        for (var depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            // The first instance that overrides the blend mode decides; otherwise the base material does.
            if (!blendKnown && current is UMaterialInstance { BasePropertyOverrides: { } overrides } && overrides.BlendMode != EBlendMode.BLEND_Opaque)
            {
                blendKnown = true;
                blend = overrides.BlendMode;
                maskClip = overrides.BlendMode == EBlendMode.BLEND_Masked ? ClipOrDefault(overrides.OpacityMaskClipValue) : null;
            }

            // An instance may change only the clip of its parent's masked blend (sparser leaves): the nearest one wins.
            if (clipOverride is null && current is UMaterialInstance && current.GetOrDefault<FStructFallback>("BasePropertyOverrides") is { } raw
                && raw.GetOrDefault<bool>("bOverride_OpacityMaskClipValue"))
            {
                clipOverride = ClipOrDefault(raw.GetOrDefault<float>("OpacityMaskClipValue"));
            }

            if (current is UMaterialInstanceConstant mic)
            {
                // Child values were added first, so TryAdd keeps the override.
                foreach (var s in mic.ScalarParameterValues ?? [])
                {
                    scalars.TryAdd(s.Name, new ScalarParameter(s.Name, s.ParameterValue, mic.Name));
                }

                foreach (var v in mic.VectorParameterValues ?? [])
                {
                    var c = v.ParameterValue;
                    var value = c is null ? Vector4.Zero : new Vector4(c.Value.R, c.Value.G, c.Value.B, c.Value.A);
                    vectors.TryAdd(v.Name, new VectorParameter(v.Name, value, mic.Name));
                }

                foreach (var t in mic.TextureParameterValues ?? [])
                {
                    textures.TryAdd(t.Name, new TextureParameter(t.Name, PathOf(t.ParameterValue, _catalog.ProjectName), mic.Name));
                }
            }

            if (current is UMaterial baseMaterial)
            {
                splineMeshes = baseMaterial.GetOrDefault<bool>("bUsedWithSplineMeshes");
                landscapeOutput = baseMaterial.TryGetValue(out FStructFallback cached, "CachedExpressionData") && cached.GetOrDefault<bool>("bHasRuntimeVirtualTextureOutput");
                if (!blendKnown)
                {
                    blend = baseMaterial.BlendMode;
                    maskClip = baseMaterial.BlendMode == EBlendMode.BLEND_Masked ? ClipOrDefault(baseMaterial.OpacityMaskClipValue) : null;
                }

                foreach (var tex in baseMaterial.ReferencedTextures ?? [])
                {
                    // Cooked masters may list textures whose package is not loadable (null entries): skip them.
                    if (tex is not null)
                    {
                        referenced.Add(AssetPaths.NormalizeObjectPath(tex.GetPathName(), _catalog.ProjectName));
                    }
                }

                master = MasterDefaults(baseMaterial);
                break;
            }

            var parentIndex = current.GetOrDefault<FPackageIndex?>("Parent");
            if (parentIndex is null || parentIndex.IsNull)
            {
                break;
            }

            var parentPath = PathOf(parentIndex, _catalog.ProjectName);
            chain.Add(parentPath);
            current = TryLoad(parentIndex, parentPath);
        }

        if (blend == EBlendMode.BLEND_Masked && clipOverride is { } clip)
        {
            maskClip = clip;
        }

        // The master's own values for what no instance sets (a SpeedTree pine's 'Color' texture, the masters' brightness
        // and roughness defaults), kept apart for the picks.
        var own = new Parameters(Sorted(scalars.Values), Sorted(vectors.Values), Sorted(textures.Values));
        AddDefaults(scalars, master.Scalars, p => p.Name);
        AddDefaults(vectors, master.Vectors, p => p.Name);
        AddDefaults(textures, master.Textures, p => p.Name);
        var left = new Parameters(Sorted(scalars.Values.Except(own.Scalars)), Sorted(vectors.Values.Except(own.Vectors)), Sorted(textures.Values.Except(own.Textures)));
        var picked = Pick(own, left);
        var texList = Sorted(textures.Values);
        var vecList = Sorted(vectors.Values);
        var baseColor = picked.BaseColor ?? referenced.FirstOrDefault(r => LooksLikeDiffuse(r) && !IsOverlay(r) && !IsEnginePlaceholder(r));
        return new MaterialInfo(material.Name, AssetPaths.NormalizeObjectPath(material.GetPathName(), _catalog.ProjectName), chain,
            Sorted(scalars.Values), vecList, texList, baseColor, picked.Tint, referenced)
        {
            OpacityMaskClip = maskClip,
            UsedWithSplineMeshes = splineMeshes,
            NormalTexture = picked.Normal,
            RoughnessRange = Roughness(scalars),
            BaseColorScale = picked.Scale,
            WritesLandscapeTexture = landscapeOutput,
            IsTranslucent = blend is EBlendMode.BLEND_Translucent or EBlendMode.BLEND_Additive or EBlendMode.BLEND_Modulate or EBlendMode.BLEND_AlphaComposite,
        };
    }

    /// <summary>
    /// Adds the base material's own parameter values under the instances' (<paramref name="values"/>, by name): a parameter
    /// an instance sets keeps the instance's value.
    /// </summary>
    internal static void AddDefaults<T>(Dictionary<string, T> values, IEnumerable<T> defaults, Func<T, string> nameOf)
    {
        foreach (var value in defaults)
        {
            values.TryAdd(nameOf(value), value);
        }
    }

    /// <summary>
    /// The base material's own parameter values, from its cooked <c>CachedExpressionData.Parameters</c>: the runtime entries
    /// (a static array: 0 scalar, 1 vector, 2 texture) name them in the order of <c>ScalarValues</c>, <c>VectorValues</c>
    /// and <c>TextureValues</c> (the layout <c>MaterialParameters.ExpressionGuids</c> reads). Empty when it has none.
    /// </summary>
    private Parameters MasterDefaults(UMaterial material)
    {
        var scalars = new List<ScalarParameter>();
        var vectors = new List<VectorParameter>();
        var textures = new List<TextureParameter>();
        if (!material.TryGetValue(out FStructFallback cached, "CachedExpressionData") || cached.GetOrDefault<FStructFallback>("Parameters") is not { } parameters)
        {
            return new Parameters(scalars, vectors, textures);
        }

        var names = new string[3][];
        foreach (var entry in parameters.Properties.Where(p => p.Name.Text == "RuntimeEntries" && p.ArrayIndex is >= 0 and < 3))
        {
            if (entry.Tag is StructProperty { Value.StructType: FStructFallback runtime })
            {
                names[entry.ArrayIndex] = (runtime.GetOrDefault<FStructFallback[]>("ParameterInfos") ?? []).Select(i => i?.GetOrDefault<FName>("Name").Text ?? string.Empty).ToArray();
            }
        }

        var scalarValues = parameters.GetOrDefault<float[]>("ScalarValues") ?? [];
        for (var i = 0; i < Math.Min(names[0]?.Length ?? 0, scalarValues.Length); i++)
        {
            scalars.Add(new ScalarParameter(names[0][i], scalarValues[i], material.Name));
        }

        var vectorValues = parameters.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FLinearColor[]>("VectorValues") ?? [];
        for (var i = 0; i < Math.Min(names[1]?.Length ?? 0, vectorValues.Length); i++)
        {
            vectors.Add(new VectorParameter(names[1][i], new Vector4(vectorValues[i].R, vectorValues[i].G, vectorValues[i].B, vectorValues[i].A), material.Name));
        }

        var textureValues = parameters.GetOrDefault<FPackageIndex[]>("TextureValues") ?? [];
        for (var i = 0; i < Math.Min(names[2]?.Length ?? 0, textureValues.Length); i++)
        {
            textures.Add(new TextureParameter(names[2][i], PathOf(textureValues[i], _catalog.ProjectName), material.Name));
        }

        return new Parameters([.. scalars.Where(p => p.Name.Length > 0)], [.. vectors.Where(p => p.Name.Length > 0)], [.. textures.Where(p => p.Name.Length > 0)]);
    }

    /// <summary>A material's scalar, vector and texture parameters.</summary>
    internal sealed record Parameters(IReadOnlyList<ScalarParameter> Scalars, IReadOnlyList<VectorParameter> Vectors, IReadOnlyList<TextureParameter> Textures);

    /// <summary>
    /// The base-colour texture, normal map, tint colour and colour scale among the parameters the instances set
    /// (<paramref name="own"/>), and only where those give none, among the base material's defaults for the ones they
    /// leave (<paramref name="master"/>): an instance's <c>Diffuse</c> beats a master's <c>Base Color</c> that sits behind a
    /// switch the instance turned off. The masters' white <c>Base Material Tint</c> and brightness 1 are a scale, not a
    /// colour: alone they leave an untextured surface grey, and they complete a scale an instance sets.
    /// </summary>
    internal static (string? BaseColor, string? Normal, Vector4? Tint, Vector3? Scale) Pick(Parameters own, Parameters master)
    {
        var colours = master.Vectors.Where(v => !string.Equals(v.Name, ScaleTintName, StringComparison.OrdinalIgnoreCase)).ToList();
        var scale = ColorScale(own.Scalars, own.Vectors) is null ? null : ColorScale([.. own.Scalars, .. master.Scalars], [.. own.Vectors, .. master.Vectors]);
        return (PickTexture(own.Textures) ?? PickTexture(master.Textures), PickNormal(own.Textures) ?? PickNormal(master.Textures),
            PickTint(own.Vectors) ?? PickTint(colours), scale);
    }

    private static List<ScalarParameter> Sorted(IEnumerable<ScalarParameter> values) => [.. values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    private static List<VectorParameter> Sorted(IEnumerable<VectorParameter> values) => [.. values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    private static List<TextureParameter> Sorted(IEnumerable<TextureParameter> values) => [.. values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The engine's stand-in textures (DefaultDiffuse, DefaultTexture …): never an object's real colour.</summary>
    private static bool IsEnginePlaceholder(string texturePath) => texturePath.StartsWith("/Engine/EngineMaterials/", StringComparison.OrdinalIgnoreCase);

    /// <summary>UE's default clip value (0.3333) when the cooked value is missing or zero.</summary>
    private static float ClipOrDefault(float value) => value > 0f && value < 1f ? value : 0.3333f;

    /// <summary>Picks the base-colour texture among texture parameters by <see cref="BaseColorTextureHints"/>, then by texture name.</summary>
    public static string? PickTexture(IReadOnlyList<TextureParameter> textures)
    {
        // Flat dummy textures (T_FlatWhiteColor_Dummy …) are placeholders: the colour then comes from a vector parameter.
        var withValue = textures.Where(t => t.TexturePath.Length > 0 && !t.TexturePath.Contains("Dummy", StringComparison.OrdinalIgnoreCase)
                                            && !IsEnginePlaceholder(t.TexturePath)).ToList();
        foreach (var hint in BaseColorTextureHints)
        {
            var exact = withValue.FirstOrDefault(t => string.Equals(t.Name, hint, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact.TexturePath;
            }
        }

        foreach (var name in ExactBaseColorTextureNames)
        {
            var exact = withValue.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact.TexturePath;
            }
        }

        foreach (var hint in BaseColorTextureHints)
        {
            // "Diffuse/Roughness": colour packed with roughness in alpha (SCUM's object master shader).
            var partial = withValue.FirstOrDefault(t => !IsBlendLayer(t.Name) && (t.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) && !IsNonColour(t.Name))
                                                        || t.Name.StartsWith(hint + "/", StringComparison.OrdinalIgnoreCase));
            if (partial is not null)
            {
                return partial.TexturePath;
            }
        }

        return withValue.FirstOrDefault(t => LooksLikeDiffuse(t.TexturePath) && !IsOverlay(t.Name) && !IsBlendLayer(t.Name))?.TexturePath;
    }

    /// <summary>
    /// Picks the object's own normal map: a parameter named <c>Normal</c> (or <c>Normal Map</c>, <c>NormalMap</c>), else
    /// one whose name contains "Normal" but is no detail, blend or overlay layer; flat dummies do not count.
    /// </summary>
    public static string? PickNormal(IReadOnlyList<TextureParameter> textures)
    {
        var real = textures.Where(t => t.TexturePath.Length > 0 && !t.TexturePath.Contains("Dummy", StringComparison.OrdinalIgnoreCase)
                                       && !t.TexturePath.Contains("FlatNormal", StringComparison.OrdinalIgnoreCase) && !IsEnginePlaceholder(t.TexturePath)).ToList();
        foreach (var name in (string[])["Normal", "Normal Map", "NormalMap", "Base Normal", "Base Material Normal"])
        {
            if (real.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) is { } exact)
            {
                return exact.TexturePath;
            }
        }

        return real.FirstOrDefault(t => t.Name.Contains("Normal", StringComparison.OrdinalIgnoreCase) && !IsOverlay(t.Name)
                                        && !t.Name.Contains("Detail", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("Blend", StringComparison.OrdinalIgnoreCase))?.TexturePath;
    }

    private static System.Numerics.Vector2? Roughness(Dictionary<string, ScalarParameter> scalars)
    {
        foreach (var prefix in (string[])["Base Material Roughness", "Roughness"])
        {
            var hasMin = scalars.TryGetValue(prefix + " Min", out var min);
            var hasMax = scalars.TryGetValue(prefix + " Max", out var max);
            if (hasMin || hasMax)
            {
                return new System.Numerics.Vector2(hasMin ? Math.Clamp(min!.Value, 0f, 1f) : 0f, hasMax ? Math.Clamp(max!.Value, 0f, 1f) : 1f);
            }
        }

        return null;
    }

    /// <summary>
    /// <c>Base Material Tint</c> x <c>Base Material Brightness</c> of SCUM's opaque master shaders, or null when neither is set.
    /// Their contrast and saturation work per texel and are not applied.
    /// </summary>
    private static Vector3? ColorScale(IReadOnlyList<ScalarParameter> scalars, IReadOnlyList<VectorParameter> vectors)
    {
        var brightness = scalars.FirstOrDefault(s => string.Equals(s.Name, BrightnessName, StringComparison.OrdinalIgnoreCase));
        var tint = vectors.FirstOrDefault(v => string.Equals(v.Name, ScaleTintName, StringComparison.OrdinalIgnoreCase));
        if (brightness is null && tint is null)
        {
            return null;
        }

        var rgb = tint is null ? Vector3.One : new Vector3(tint.Value.X, tint.Value.Y, tint.Value.Z);
        return Vector3.Max(rgb * (brightness?.Value ?? 1f), Vector3.Zero);
    }

    /// <summary>Layers painted over the base (moss, dirt, snow, puddles): never the object's own colour.</summary>
    private static bool IsOverlay(string name) =>
        name.Contains("Overlay", StringComparison.OrdinalIgnoreCase) || name.Contains("Moss", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Snow", StringComparison.OrdinalIgnoreCase) || name.Contains("Dirt", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Puddle", StringComparison.OrdinalIgnoreCase) || name.Contains("Dummy", StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks the tint colour among vector parameters by <see cref="TintHints"/>.</summary>
    public static Vector4? PickTint(IReadOnlyList<VectorParameter> vectors)
    {
        foreach (var hint in TintHints)
        {
            var exact = vectors.FirstOrDefault(v => string.Equals(v.Name, hint, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact.Value;
            }
        }

        foreach (var hint in TintHints)
        {
            var partial = vectors.FirstOrDefault(v => v.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) && !IsNonColour(v.Name));
            if (partial is not null)
            {
                return partial.Value;
            }
        }

        return null;
    }

    /// <summary>True when a texture name follows the diffuse naming convention (<c>T_X_D</c>, <c>_BaseColor</c>, <c>_Albedo</c>, SpeedTree's <c>_Color</c>).</summary>
    public static bool LooksLikeDiffuse(string texturePath)
    {
        var name = texturePath[(texturePath.LastIndexOfAny(['/', '.']) + 1)..];
        return name.EndsWith("_D", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("_BC", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("_DR", StringComparison.OrdinalIgnoreCase) // diffuse + roughness in alpha (base building)
               || name.Contains("_D_", StringComparison.OrdinalIgnoreCase)
               || name.Contains("BaseColor", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Albedo", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)
               || (name.EndsWith("_Color", StringComparison.OrdinalIgnoreCase) && !IsFoliageMap(name)); // SpeedTree: ScotsPine_01_Color
    }

    /// <summary>The maps a SpeedTree or bush texture set has beside its colour (masks, normals, subsurface, opacity).</summary>
    private static bool IsFoliageMap(string name) =>
        name.Contains("Mask", StringComparison.OrdinalIgnoreCase) || name.Contains("Normal", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Subsurface", StringComparison.OrdinalIgnoreCase) || name.Contains("SSS", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Opacity", StringComparison.OrdinalIgnoreCase);

    /// <summary>Blend layers painted over the base (<c>Blend Color/Alpha</c>, <c>B Material Diffuse</c>): never the object's own colour.</summary>
    private static bool IsBlendLayer(string name) =>
        name.Contains("Blend", StringComparison.OrdinalIgnoreCase) || name.StartsWith("B Material", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonColour(string name) =>
        name.Contains("Normal", StringComparison.OrdinalIgnoreCase) || name.Contains("Mask", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Rough", StringComparison.OrdinalIgnoreCase) || name.Contains("Emiss", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Detail", StringComparison.OrdinalIgnoreCase) || name.Contains("Decay", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Damage", StringComparison.OrdinalIgnoreCase) || name.Contains("RMA", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Aging", StringComparison.OrdinalIgnoreCase) || name.Contains("Dirt", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Rust", StringComparison.OrdinalIgnoreCase) || name.Contains("Grunge", StringComparison.OrdinalIgnoreCase);

    private UObject? TryLoad(FPackageIndex index, string path)
    {
        try
        {
            if (index.Load() is UObject loaded)
            {
                return loaded;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fall through to the catalog lookup (e.g. the import's package lives in another container).
        }

        return path.Length > 0 && _catalog.TryLoadObject<UObject>(path, out var obj) ? obj : null;
    }

    private static string PathOf(FPackageIndex? index, string projectName)
    {
        if (index is null || index.IsNull)
        {
            return string.Empty;
        }

        try
        {
            var path = index.ResolvedObject?.GetPathName();
            return path is null ? string.Empty : AssetPaths.NormalizeObjectPath(path, projectName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return index.Name;
        }
    }
}
