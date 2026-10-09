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

    /// <summary>
    /// Hour of the day (0-24) for a scene with a <see cref="Sky"/>: moves the sun along its arc and sets the sky, light,
    /// ambient, haze and exposure for dawn, day, dusk or a moonlit night (<see cref="AtHour"/>). Null keeps the settings
    /// as they are; 13.5 (13:30, the middle of SCUM's day) is the island's own sun (the look without a time of day).
    /// </summary>
    public float? TimeOfDay { get; init; }

    /// <summary>Light and shade surfaces (sun, sky light, shadows, reflections); off draws each in its own colour, without the shadow pass.</summary>
    public bool Lighting { get; init; } = true;

    /// <summary>Move the sea's waves with <see cref="SceneRenderer.Time"/>; off keeps the water still.</summary>
    public bool AnimateWater { get; init; } = true;

    /// <summary>Brightness of the stars in the sky (0 by day; <see cref="AtHour"/> raises it at night).</summary>
    public float Stars { get; init; }

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

    /// <summary>SCUM's clock (ServerSettings <c>SunriseTime</c> 06:00, <c>SunsetTime</c> 21:00, the defaults).</summary>
    public const float Sunrise = 6f, Sunset = 21f;

    /// <summary>
    /// These settings at <paramref name="hour"/> (0-24) on SCUM's clock: the sun rises at <see cref="Sunrise"/>, sets at
    /// <see cref="Sunset"/> and stands as high as the island's own sun at 13:30 (27°, so 13:30 looks exactly like
    /// <see cref="Outdoor"/>); near the horizon the light turns golden and the sky orange. The night is the game's
    /// (BP_WeatherController2): the day lit by a moon of 0.5 lux instead of a 10 lux sun, tinted (0.70, 0.76, 1) × #FFF6EE,
    /// plus a 0.05 lux night light, seen through the game's auto exposure and dark-tone lift (<see cref="GameExposure"/>).
    /// </summary>
    public RenderSettings AtHour(float hour)
    {
        hour = ((hour % 24f) + 24f) % 24f;
        var h = hour < Sunrise ? hour + 24f : hour;
        const float Noon = (Sunrise + Sunset) / 2f;
        var turn = h <= Sunset ? (h - Noon) * 90f / (Sunset - Noon) : 90f + ((h - Sunset) * 180f / (24f - Sunset + Sunrise));
        var elevation = 27f * MathF.Cos(turn * MathF.PI / 180f);
        var day = Smooth(4f, 18f, elevation);
        var golden = Smooth(-3f, 3f, elevation);
        var dusk = Smooth(-12f, -3f, elevation);
        Vector3 Blend(Vector3 night, Vector3 twilight, Vector3 sunset, Vector3 noon) =>
            Vector3.Lerp(Vector3.Lerp(Vector3.Lerp(night, twilight, dusk), sunset, golden), noon, day);

        // The game's night: the moon is 0.5 lux against the sun's 10 (UE's default), the night light 0.05 lux
        const float Moon = 0.05f, NightLight = 0.005f;
        var moonTint = new Vector3(0.698f, 0.704f, 0.855f); // _moonLightTint × the Moon light's #FFF6EE
        var nightTint = new Vector3(0.698f, 0.764f, 1f); // _nightLightColor
        var sunUp = elevation > -2f;
        var sun = Vector3.Lerp(new Vector3(1.7f, 0.95f, 0.5f), LightColor, day) * Smooth(-3f, 4f, elevation);
        var moon = moonTint * (Luma(LightColor) * Moon * (1f - Smooth(-12f, -2f, elevation)));
        var lit = this with
        {
            TimeOfDay = hour,
            LightDirection = sunUp ? SunDirection(-elevation, 72f + turn) : SunDirection(elevation, 252f + turn),
            LightColor = sunUp ? sun : moon,
            SkyZenithColor = Blend(SkyZenithColor * Moon, new(0.02f, 0.03f, 0.08f), new(0.09f, 0.13f, 0.32f), SkyZenithColor),
            SkyHorizonColor = Blend(SkyHorizonColor * Moon, new(0.2f, 0.14f, 0.2f), new(0.75f, 0.45f, 0.3f), SkyHorizonColor),
            SkyColor = Blend((SkyColor * moonTint * Moon) + (nightTint * (Luma(LightColor) * NightLight)), new(0.12f, 0.11f, 0.18f), new(0.42f, 0.38f, 0.42f), SkyColor),
            GroundColor = Blend(GroundColor * Moon, new(0.05f, 0.04f, 0.05f), new(0.16f, 0.12f, 0.1f), GroundColor),
            Stars = 1f - Smooth(-14f, -4f, elevation),
        };
        if (Exposure <= 0f)
        {
            return lit;
        }

        // Lux on flat ground (the sun's 10 lux = this light's brightness), at this hour and at the island's own 13:30
        var luxPerUnit = 10f / Luma(LightColor);
        var lux = ((Luma(lit.LightColor) * MathF.Max(0f, -Vector3.Normalize(lit.LightDirection).Y)) + Luma(lit.SkyColor)) * luxPerUnit;
        var noonLux = ((Luma(LightColor) * MathF.Max(0f, -Vector3.Normalize(IslandSunDirection).Y)) + Luma(SkyColor)) * luxPerUnit;
        return lit with { Exposure = Exposure * GameExposure(lux, noonLux, MathF.Sin(elevation * MathF.PI / 180f)) };
    }

    /// <summary>
    /// SCUM's exposure at <paramref name="lux"/> against its noon (<paramref name="noonLux"/>): UE's basic auto exposure
    /// adapts to the scene's luminance (here flat ground of the island's dark albedo, 0.08) within the limits the game's
    /// curves set by sun height (ExposureMinBrightness 0.18 at night to 0.16 by day, ExposureMaxBrightness 0.4 to 1), so
    /// a day of any brightness looks alike and a night stays as dark as its light; then the game's post process lifts dark
    /// tones (ColorGainShadows 1.8 and ColorGammaShadows 1.1 below UE's shadows limit, 0.09).
    /// </summary>
    internal static float GameExposure(float lux, float noonLux, float sunHeight)
    {
        const float CdPerLux = 0.08f / MathF.PI;
        var min = 0.18f - (0.02f * Math.Clamp((sunHeight + 0.1f) / 0.1f, 0f, 1f));
        var max = 0.4f + (0.6f * Math.Clamp((sunHeight + 0.2f) / 0.2f, 0f, 1f));
        var adapted = Math.Clamp(CdPerLux * lux, min, max);
        var key = MathF.Max(CdPerLux * lux / adapted, 1e-4f); // the frame against a well exposed one
        var shadows = 1f - Smooth(0f, 0.09f, 0.18f * key);
        var grade = 1f + (shadows * ((1.8f * MathF.Pow(key, -1f / 11f)) - 1f));
        return Math.Clamp(CdPerLux * noonLux, 0.16f, 1f) / adapted * grade;
    }

    private static float Luma(Vector3 c) => (0.2126f * c.X) + (0.7152f * c.Y) + (0.0722f * c.Z);

    private static float Smooth(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

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
