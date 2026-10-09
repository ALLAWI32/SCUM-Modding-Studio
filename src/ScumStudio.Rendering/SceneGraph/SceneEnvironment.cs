using System.Numerics;

namespace ScumStudio.Rendering.SceneGraph;

/// <summary>Where the ground grid is drawn for a scene with a <see cref="SceneEnvironment"/>.</summary>
public enum GridPlacement
{
    /// <summary>Use the renderer's <see cref="RenderSettings.ShowGrid"/> / <see cref="RenderSettings.GridHeight"/>.</summary>
    Settings,

    /// <summary>Do not draw the grid (e.g. outdoor scenes with terrain).</summary>
    Hidden,

    /// <summary>Draw the grid (when <see cref="RenderSettings.ShowGrid"/> is on) at <see cref="SceneEnvironment.GridHeight"/>.</summary>
    AtHeight,
}

/// <summary>
/// Per-scene look overrides: a scene that knows what it shows (for example a level with terrain, which wants a sky, a
/// sun and no grid cutting through the ground) sets <see cref="Scene.Environment"/>, and the renderer applies it over its
/// own <see cref="RenderSettings"/> unless the host turned <see cref="RenderSettings.UseSceneEnvironment"/> off. Null
/// members keep the renderer's value.
/// </summary>
public sealed record SceneEnvironment
{
    /// <summary>Background colour (linear RGBA).</summary>
    public Vector4? ClearColor { get; init; }

    /// <summary>Hemispheric ambient colour for normals pointing up.</summary>
    public Vector3? SkyColor { get; init; }

    /// <summary>Hemispheric ambient colour for normals pointing down.</summary>
    public Vector3? GroundColor { get; init; }

    /// <summary>Direction the key light travels (GL axes).</summary>
    public Vector3? LightDirection { get; init; }

    /// <summary>Key light colour.</summary>
    public Vector3? LightColor { get; init; }

    /// <summary>Distance fog colour (linear RGB).</summary>
    public Vector3? FogColor { get; init; }

    /// <summary>Distance fog density per world unit (0 = no fog).</summary>
    public float? FogDensity { get; init; }

    /// <summary>Fog thinning with height (see <see cref="RenderSettings.FogHeightFalloff"/>).</summary>
    public float? FogHeightFalloff { get; init; }

    /// <summary>Draw the sky and horizon haze (see <see cref="RenderSettings.Sky"/>).</summary>
    public bool? Sky { get; init; }

    /// <summary>Sky colour straight up.</summary>
    public Vector3? SkyZenithColor { get; init; }

    /// <summary>Sky colour at the horizon.</summary>
    public Vector3? SkyHorizonColor { get; init; }

    /// <summary>Tone-mapping exposure (see <see cref="RenderSettings.Exposure"/>).</summary>
    public float? Exposure { get; init; }

    /// <summary>Grid handling for this scene.</summary>
    public GridPlacement Grid { get; init; } = GridPlacement.Settings;

    /// <summary>Grid plane height (GL world Y) used with <see cref="GridPlacement.AtHeight"/>.</summary>
    public float GridHeight { get; init; }

    /// <summary>The outdoor look of <see cref="RenderSettings.Outdoor"/> (sky, sun, fog) with the given grid handling.</summary>
    public static SceneEnvironment Outdoor(GridPlacement grid = GridPlacement.Hidden, float gridHeight = 0f)
    {
        var o = RenderSettings.Outdoor;
        return new SceneEnvironment
        {
            ClearColor = o.ClearColor,
            SkyColor = o.SkyColor,
            GroundColor = o.GroundColor,
            LightDirection = o.LightDirection,
            LightColor = o.LightColor,
            FogColor = o.FogColor,
            FogDensity = o.FogDensity,
            FogHeightFalloff = o.FogHeightFalloff,
            Sky = o.Sky,
            SkyZenithColor = o.SkyZenithColor,
            SkyHorizonColor = o.SkyHorizonColor,
            Exposure = o.Exposure,
            Grid = grid,
            GridHeight = gridHeight,
        };
    }

    /// <summary>Returns <paramref name="settings"/> with this environment's overrides applied.</summary>
    public RenderSettings ApplyTo(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            ClearColor = ClearColor ?? settings.ClearColor,
            SkyColor = SkyColor ?? settings.SkyColor,
            GroundColor = GroundColor ?? settings.GroundColor,
            LightDirection = LightDirection ?? settings.LightDirection,
            LightColor = LightColor ?? settings.LightColor,
            FogColor = FogColor ?? settings.FogColor,
            FogDensity = FogDensity ?? settings.FogDensity,
            FogHeightFalloff = FogHeightFalloff ?? settings.FogHeightFalloff,
            Sky = Sky ?? settings.Sky,
            SkyZenithColor = SkyZenithColor ?? settings.SkyZenithColor,
            SkyHorizonColor = SkyHorizonColor ?? settings.SkyHorizonColor,
            Exposure = Exposure ?? settings.Exposure,
            ShowGrid = Grid switch
            {
                GridPlacement.Hidden => false,
                _ => settings.ShowGrid,
            },
            GridHeight = Grid == GridPlacement.AtHeight ? GridHeight : settings.GridHeight,
        };
    }
}
