using System.Numerics;

namespace ScumStudio.Rendering;

/// <summary>Frame appearance settings. Colours are linear RGB(A) (they are sRGB-encoded on output).</summary>
public sealed record RenderSettings
{
    /// <summary>Background colour: a warm olive-grey (the app's FIELD MANUAL palette, see App.axaml).</summary>
    public Vector4 ClearColor { get; init; } = new(0.043f, 0.047f, 0.039f, 1f);

    /// <summary>Hemispheric ambient colour for normals pointing up.</summary>
    public Vector3 SkyColor { get; init; } = new(0.42f, 0.46f, 0.52f);

    /// <summary>Hemispheric ambient colour for normals pointing down.</summary>
    public Vector3 GroundColor { get; init; } = new(0.12f, 0.1f, 0.09f);

    /// <summary>Direction the key light travels (normalised on use).</summary>
    public Vector3 LightDirection { get; init; } = new(-0.4f, -0.8f, -0.3f);

    /// <summary>Key light colour.</summary>
    public Vector3 LightColor { get; init; } = new(0.62f, 0.6f, 0.55f);

    /// <summary>Selection highlight colour (rgb) and tint strength (a): the UI accent, blaze orange #E87B2F.</summary>
    public Vector4 HighlightColor { get; init; } = new(0.91f, 0.48f, 0.18f, 0.45f);

    /// <summary>Draw the ground grid.</summary>
    public bool ShowGrid { get; init; } = true;

    /// <summary>Grid plane height (GL world Y).</summary>
    public float GridHeight { get; init; }

    /// <summary>Minor grid cell size in world units (100 = 1 m in centimetres).</summary>
    public float GridCellSize { get; init; } = 100f;

    /// <summary>Every n-th line is a major line.</summary>
    public float GridMajorEvery { get; init; } = 10f;

    /// <summary>Distance from the camera at which the grid has faded out.</summary>
    public float GridFadeDistance { get; init; } = 50_000f;

    /// <summary>Minor line colour (rgb) and opacity (a): a warm olive grey like the UI's Stroke token.</summary>
    public Vector4 GridMinorColor { get; init; } = new(0.33f, 0.33f, 0.27f, 0.35f);

    /// <summary>Major line colour (rgb) and opacity (a).</summary>
    public Vector4 GridMajorColor { get; init; } = new(0.48f, 0.48f, 0.4f, 0.6f);

    /// <summary>Colour of the line along GL X (= UE X): the UI's AxisX #E5534B (linear).</summary>
    public Vector4 GridAxisXColor { get; init; } = new(0.78f, 0.088f, 0.07f, 0.9f);

    /// <summary>Colour of the line along GL Z (= UE Y): the UI's AxisY #8DB84A (linear).</summary>
    public Vector4 GridAxisZColor { get; init; } = new(0.266f, 0.479f, 0.07f, 0.9f);

    /// <summary>Skip the instance clusters (see <see cref="SceneGraph.SceneBatcher.ClusterSize"/>) outside the view frustum.</summary>
    public bool FrustumCulling { get; init; } = true;

    /// <summary>
    /// Multiplier on the cull distances stored on the level's HISM/foliage components (<see cref="SceneGraph.SceneNode.MaxDrawDistance"/>)
    /// and on <see cref="CullPixelSize"/>: 1 draws what the game draws, 2 keeps things twice as far, 0.5 half as far.
    /// </summary>
    public float ViewDistanceScale { get; init; } = 1f;

    /// <summary>
    /// Instance clusters whose largest instance projects smaller than this many pixels (bounding sphere diameter) are
    /// not drawn; 0 draws everything in the frustum.
    /// </summary>
    public float CullPixelSize { get; init; } = 4f;

    /// <summary>
    /// Objects (nodes with a <see cref="SceneGraph.SceneNode.MaxDrawDistance"/>, i.e. not terrain or sea) farther than
    /// this (world units) are not drawn; 0 = as far as their own cull distance allows.
    /// </summary>
    public float ObjectDrawDistance { get; init; }

    /// <summary>Multiplier on the projected size used to pick LODs: below 1 switches to coarser LODs sooner (faster).</summary>
    public float LodBias { get; init; } = 1f;

    /// <summary>Use reverse-Z depth when the driver supports <c>glClipControl</c> (better precision on large levels).</summary>
    public bool ReverseZ { get; init; } = true;

    /// <summary>Cull back faces (off by default: cooked meshes are often open or two-sided).</summary>
    public bool BackfaceCulling { get; init; }

    /// <summary>Encode shader output to sRGB (the colour target stores display-ready values).</summary>
    public bool EncodeSrgb { get; init; } = true;

    /// <summary>Distance fog colour (linear RGB), blended in by <see cref="FogDensity"/>.</summary>
    public Vector3 FogColor { get; init; } = new(0.5f, 0.6f, 0.72f);

    /// <summary>Exponential distance fog density per world unit (0 = no fog, the default).</summary>
    public float FogDensity { get; init; }

    /// <summary>
    /// How fast the fog thins with height above Y = 0 (sea level), per world unit: the density is
    /// <see cref="FogDensity"/> × e^(-falloff × y). 0 = the same density at every height.
    /// </summary>
    public float FogHeightFalloff { get; init; }

    /// <summary>
    /// Draw a sky behind the scene (a gradient from <see cref="SkyHorizonColor"/> to <see cref="SkyZenithColor"/> with a
    /// sun in the key light's direction) instead of <see cref="ClearColor"/>, and fade distant things into that horizon
    /// haze, brighter toward the sun, instead of <see cref="FogColor"/>.
    /// </summary>
    public bool Sky { get; init; }

    /// <summary>Sky colour straight up (linear RGB, before tone mapping).</summary>
    public Vector3 SkyZenithColor { get; init; } = new(0.07f, 0.15f, 0.45f);

    /// <summary>Sky colour at the horizon (linear RGB, before tone mapping): also the colour distant ground fades to.</summary>
    public Vector3 SkyHorizonColor { get; init; } = new(0.37f, 0.49f, 0.7f);

    /// <summary>
    /// Sun shadows (a 2048² shadow map with soft edges) within <see cref="ShadowDistance"/> around the camera. Off by
    /// default (thumbnails and previews); the Map view and <c>render level</c> turn it on.
    /// </summary>
    public bool Shadows { get; init; }

    /// <summary>Half the width of the square the sun's shadows cover, in world units (15 000 = 150 m).</summary>
    public float ShadowDistance { get; init; } = 15_000f;

    /// <summary>Exposure before an ACES-fit tone curve; 0 = no tone mapping (the default: shaded colours are clipped).</summary>
    public float Exposure { get; init; }

    /// <summary>
    /// Apply the look overrides a scene carries in <see cref="SceneGraph.Scene.Environment"/> (sky, sun, grid handling of
    /// a level with terrain). Turn off to always draw with these settings as they are.
    /// </summary>
    public bool UseSceneEnvironment { get; init; } = true;

    /// <summary>Sun of SCUM's The_Island (<c>DirectionalLightComponent</c> rotation pitch -27°, yaw 72°).</summary>
    public static Vector3 IslandSunDirection { get; } = SunDirection(-27f, 72f);

    /// <summary>
    /// Outdoor preset: a sky with a sun disc, warm sun from <see cref="IslandSunDirection"/>, sky-tinted ambient, height
    /// haze that fades distant ground into the horizon, ACES tone mapping, no grid. Level scenes with terrain use it through <see cref="SceneGraph.SceneEnvironment.Outdoor"/>.
    /// Sun and sky are strong enough that flat ground receives about 1.3× its albedo: the game's landscape textures are
    /// dark PBR albedos (mean sRGB #39..#60) that UE shows under a far brighter sun with auto exposure.
    /// </summary>
    public static RenderSettings Outdoor { get; } = new()
    {
        ClearColor = new Vector4(0.36f, 0.5f, 0.7f, 1f),
        SkyColor = new Vector3(0.46f, 0.55f, 0.72f),
        GroundColor = new Vector3(0.2f, 0.17f, 0.13f),
        LightDirection = IslandSunDirection,
        LightColor = new Vector3(1.8f, 1.6f, 1.3f),
        FogColor = new Vector3(0.5f, 0.6f, 0.74f),
        FogDensity = 5e-7f, // ~40 % haze 10 km out at sea level; thinner higher up, so the island stays readable from above
        FogHeightFalloff = 3.3e-6f, // a scale height of 3 km: distant mountains still fade to blue-grey
        Sky = true,
        Exposure = 1f,
        ShowGrid = false,
    };

    /// <summary>
    /// GL-space direction a directional light travels for a UE rotation (degrees): UE forward
    /// <c>(cos p cos y, cos p sin y, sin p)</c> mapped to GL axes (x, z, y).
    /// </summary>
    public static Vector3 SunDirection(float pitchDegrees, float yawDegrees)
    {
        var p = pitchDegrees * MathF.PI / 180f;
        var y = yawDegrees * MathF.PI / 180f;
        var ue = new Vector3(MathF.Cos(p) * MathF.Cos(y), MathF.Cos(p) * MathF.Sin(y), MathF.Sin(p));
        return Vector3.Normalize(new Vector3(ue.X, ue.Z, ue.Y));
    }
}
