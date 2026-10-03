namespace ScumStudio.Level.Model;

/// <summary>Mesh components the game never draws as solid geometry.</summary>
public static class HelperMeshes
{
    // ponytail: name-based list of helper meshes; add entries when other volumes show up as solid boxes.
    // Never drawn as solid geometry in game: environment-description volumes and the boxes that fake lit windows at
    // night (their material only glows behind the glass, scaled up to 12 x 21 m on the outpost bank). Drawn they hid
    // whole buildings as white blocks; measured, they made a building's far-model cut reach its neighbours.

    /// <summary>True for a helper mesh component of <paramref name="actor"/> (or one without a mesh).</summary>
    public static bool IsHelper(ActorRecord actor, ComponentRecord component)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(component);
        return component.StaticMeshPath is not { } mesh
            || component.ClassName == "EnvironmentDescriptionComponent"
            // Bunker weather masks (TV_Base_*_WM, Apex_Facility_WM): rain/sky blocker shells round the whole base.
            || actor.ClassName.EndsWith("_WM_C", StringComparison.OrdinalIgnoreCase)
            || mesh.Contains("/Materials/Light/WindowLights/", StringComparison.OrdinalIgnoreCase)
            // Editor-only shapes (the giant white EditorSphere of every volumetric fog Blueprint); the game never draws them.
            || mesh.StartsWith("/Engine/Editor", StringComparison.OrdinalIgnoreCase)
            // The game's far-sea plane (14 km): the viewport draws its own sea.
            || mesh.Contains("/Water/DistantWater/", StringComparison.OrdinalIgnoreCase);
    }
}
