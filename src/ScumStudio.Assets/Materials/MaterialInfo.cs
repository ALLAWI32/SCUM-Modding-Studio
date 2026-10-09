using System.Numerics;

namespace ScumStudio.Assets.Materials;

/// <summary>A scalar material parameter.</summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Value">Value.</param>
/// <param name="Source">Name of the material (instance) in the parent chain that defines the value.</param>
public sealed record ScalarParameter(string Name, float Value, string Source);

/// <summary>A vector (linear colour) material parameter.</summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Value">RGBA value (linear).</param>
/// <param name="Source">Name of the material (instance) that defines the value.</param>
public sealed record VectorParameter(string Name, Vector4 Value, string Source);

/// <summary>A texture material parameter.</summary>
/// <param name="Name">Parameter name.</param>
/// <param name="TexturePath">Object path of the texture (<c>/Game/.../T_X.T_X</c>), or empty when unset.</param>
/// <param name="Source">Name of the material (instance) that defines the value.</param>
public sealed record TextureParameter(string Name, string TexturePath, string Source);

/// <summary>
/// Flattened view of a material instance: parameters merged along the parent chain (a child's value overrides its parent's),
/// the chain itself, and best guesses for the base-colour texture and tint used by the viewport.
/// </summary>
/// <param name="Name">Material name.</param>
/// <param name="ObjectPath">Material object path.</param>
/// <param name="ParentChain">
/// Object paths of the parents, nearest first, ending at the base <c>UMaterial</c> when it could be resolved. A parent that is not in the
/// catalog (e.g. a base material in an unmounted pak) is listed but ends the chain.
/// </param>
/// <param name="Scalars">Scalar parameters (merged).</param>
/// <param name="Vectors">Vector parameters (merged).</param>
/// <param name="Textures">Texture parameters (merged).</param>
/// <param name="BaseColorTexture">Object path of the most likely base-colour (diffuse/albedo) texture, or null.</param>
/// <param name="TintColor">Most likely tint/base colour parameter (linear RGBA), or null.</param>
/// <param name="ReferencedTextures">
/// For base materials (<c>UMaterial</c>): the textures the compiled material references, in order; empty for instances whose base
/// could not be loaded.
/// </param>
public sealed record MaterialInfo(
    string Name,
    string ObjectPath,
    IReadOnlyList<string> ParentChain,
    IReadOnlyList<ScalarParameter> Scalars,
    IReadOnlyList<VectorParameter> Vectors,
    IReadOnlyList<TextureParameter> Textures,
    string? BaseColorTexture,
    Vector4? TintColor,
    IReadOnlyList<string> ReferencedTextures)
{
    /// <summary>
    /// For a masked material (blend mode <c>BLEND_Masked</c>: leaves, grass, fences with holes), the opacity clip value:
    /// pixels whose base-colour alpha is below it are not drawn. Null for opaque and translucent materials.
    /// </summary>
    public float? OpacityMaskClip { get; init; }

    /// <summary>True for a translucent material (water, glass, smoke): drawn blended, never as a solid surface.</summary>
    public bool IsTranslucent { get; init; }

    /// <summary>
    /// True when the base material was compiled for spline meshes (<c>bUsedWithSplineMeshes</c>): only then can the game
    /// draw a bent copy of a mesh with it; otherwise a cooked game falls back to the engine's default material.
    /// </summary>
    public bool UsedWithSplineMeshes { get; init; }

    /// <summary>The tangent-space normal map (SCUM's master shaders call it <c>Normal</c>), or null.</summary>
    public string? NormalTexture { get; init; }

    /// <summary>
    /// Roughness range (min, max) of SCUM's opaque master shader, which scales the base colour's alpha between the two
    /// (<c>Base Material Roughness Min/Max</c> or <c>Roughness Min/Max</c>); null when the material has neither.
    /// </summary>
    public System.Numerics.Vector2? RoughnessRange { get; init; }

    /// <summary>
    /// True when the base material can draw into a runtime virtual texture (<c>CachedExpressionData.bHasRuntimeVirtualTextureOutput</c>).
    /// Most SCUM masters can (buildings, rocks, roads); the game asks only its road pieces to, see <c>BendMesh.DrawsIntoLandscape</c>.
    /// </summary>
    public bool WritesLandscapeTexture { get; init; }
}
