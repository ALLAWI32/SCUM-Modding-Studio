using System.Numerics;

namespace ScumStudio.Rendering.Resources;

/// <summary>
/// Sharp ground up close: a terrain component's four main paint layers, tiled in world space and blended by their
/// weights, give the baked ground texture its fine structure near the camera (the bake alone is ~40 cm a texel and looks
/// blocky from a few metres). The ratio of the tiled sample to the layer's average colour multiplies the baked colour,
/// so colours, rock on slopes and wet shores stay as baked. The textures are owned by the caller.
/// </summary>
/// <param name="Weights">RGBA weights of the four layers over the component (linear, clamp-to-edge; the mesh's UVs).</param>
/// <param name="Layers">The four layers' diffuse textures (sRGB, repeating); null entries are unused.</param>
/// <param name="InvTilingCm">1 / each layer's repeat size in cm (0 = unused).</param>
/// <param name="Means">Each layer texture's average linear colour.</param>
public sealed record TerrainDetailTextures(GpuTexture Weights, IReadOnlyList<GpuTexture?> Layers, Vector4 InvTilingCm, IReadOnlyList<Vector3> Means);
