using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Objects;
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
        float? maskClip = null;
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

        var texList = textures.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var vecList = vectors.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var baseColor = PickTexture(texList) ?? referenced.FirstOrDefault(r => LooksLikeDiffuse(r) && !IsOverlay(r) && !IsEnginePlaceholder(r));
        var tint = PickTint(vecList);
        return new MaterialInfo(material.Name, AssetPaths.NormalizeObjectPath(material.GetPathName(), _catalog.ProjectName), chain,
            scalars.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(), vecList, texList, baseColor, tint, referenced)
        {
            OpacityMaskClip = maskClip,
            UsedWithSplineMeshes = splineMeshes,
            NormalTexture = PickNormal(texList),
            RoughnessRange = Roughness(scalars),
            BaseColorScale = ColorScale(scalars, vectors),
            WritesLandscapeTexture = landscapeOutput,
            IsTranslucent = blend is EBlendMode.BLEND_Translucent or EBlendMode.BLEND_Additive or EBlendMode.BLEND_Modulate or EBlendMode.BLEND_AlphaComposite,
        };
    }

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
            var partial = withValue.FirstOrDefault(t => (t.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) && !IsNonColour(t.Name))
                                                        || t.Name.StartsWith(hint + "/", StringComparison.OrdinalIgnoreCase));
            if (partial is not null)
            {
                return partial.TexturePath;
            }
        }

        return withValue.FirstOrDefault(t => LooksLikeDiffuse(t.TexturePath) && !IsOverlay(t.Name))?.TexturePath;
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
    private static Vector3? ColorScale(Dictionary<string, ScalarParameter> scalars, Dictionary<string, VectorParameter> vectors)
    {
        var hasBrightness = scalars.TryGetValue("Base Material Brightness", out var brightness);
        var hasTint = vectors.TryGetValue("Base Material Tint", out var tint);
        if (!hasBrightness && !hasTint)
        {
            return null;
        }

        var rgb = hasTint ? new Vector3(tint!.Value.X, tint.Value.Y, tint.Value.Z) : Vector3.One;
        return Vector3.Max(rgb * (hasBrightness ? brightness!.Value : 1f), Vector3.Zero);
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

    /// <summary>True when a texture name follows the diffuse naming convention (<c>T_X_D</c>, <c>_BaseColor</c>, <c>_Albedo</c>).</summary>
    public static bool LooksLikeDiffuse(string texturePath)
    {
        var name = texturePath[(texturePath.LastIndexOfAny(['/', '.']) + 1)..];
        return name.EndsWith("_D", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("_BC", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("_DR", StringComparison.OrdinalIgnoreCase) // diffuse + roughness in alpha (base building)
               || name.Contains("_D_", StringComparison.OrdinalIgnoreCase)
               || name.Contains("BaseColor", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Albedo", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Diffuse", StringComparison.OrdinalIgnoreCase);
    }

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
