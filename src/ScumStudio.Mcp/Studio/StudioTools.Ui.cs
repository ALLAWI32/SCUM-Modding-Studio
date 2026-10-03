using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.World;
using ScumStudio.Mcp.Protocol;

namespace ScumStudio.Mcp.Studio;

/// <content>Desktop app UI tools.</content>
public sealed partial class StudioTools
{
    /// <summary>Pages of the app.</summary>
    public static IReadOnlyList<string> Pages { get; } = ["map", "vehicles", "weapons", "assets", "projects", "settings"];

    private IEnumerable<McpTool> UiTools(IStudioUi ui)
    {
        yield return new McpTool("navigate",
            "Shows a page of the ScumStudio window.",
            ToolSchema.Object().String("page", "Page to show.", required: true, choices: Pages).Build(),
            async (call, ct) =>
            {
                var page = call.RequireString("page").ToLowerInvariant();
                if (!Pages.Contains(page))
                {
                    throw new ToolArgumentException("Unknown page; use one of: " + string.Join(", ", Pages));
                }

                await ui.NavigateAsync(page, ct).ConfigureAwait(false);
                return McpToolResult.Text("Showing the " + page + " page.");
            })
        { Title = "Show page", Idempotent = true };

        yield return new McpTool("show_levels",
            "Loads levels into the Map page's 3D viewport (the user sees them; edits of the project are shown live). Give level " +
            "names or a cell (a whole cell loads its POI sublevels and terrain).",
            ToolSchema.Object()
                .Strings("levels", "Level names or paths.")
                .String("cell", "A whole map cell (A_0 …).")
                .String("terrainDetail", "Terrain detail: full or coarse (default: full for single levels, coarse for cells).", choices: ["full", "coarse"])
                .Build(),
            async (call, ct) =>
            {
                var levels = LevelsFrom(call);
                var coarse = call.GetString("terrainDetail") is { } detail ? detail.Equals("coarse", StringComparison.OrdinalIgnoreCase) : call.Has("cell");
                var text = await ui.ShowLevelsAsync(levels, coarse ? 4 : 1, ct).ConfigureAwait(false);
                return McpToolResult.Text(text);
            })
        { Title = "Show levels in 3D", Idempotent = true };

        yield return new McpTool("select_actor",
            "Selects an actor in the 3D viewport and entity list (its level must be shown, see show_levels) and frames the camera on it.",
            ToolSchema.Object()
                .String("level", "Level name or path.", required: true)
                .String("actor", "Actor name.", required: true)
                .Boolean("frame", "Move the camera to it (default true).")
                .Build(),
            async (call, ct) =>
            {
                var path = ResolveLevel(call.RequireString("level"));
                var text = await ui.SelectActorAsync(new ActorRef(path, call.RequireString("actor")), call.GetBool("frame", true), ct).ConfigureAwait(false);
                return McpToolResult.Text(text);
            })
        { Title = "Select actor", Idempotent = true };

        yield return new McpTool("set_camera",
            "Moves the 3D viewport camera: a location and/or a point to look at (UE cm), or yaw/pitch in degrees; frameAll frames the whole shown scene.",
            ToolSchema.Object()
                .Vector("location", "Camera location [x, y, z] (cm).")
                .Vector("lookAt", "Point to look at [x, y, z] (cm).")
                .Number("yaw", "Yaw in degrees (0 = +X, 90 = +Y).")
                .Number("pitch", "Pitch in degrees (negative looks down).")
                .Boolean("frameAll", "Frame the whole scene.")
                .Build(),
            async (call, ct) =>
            {
                var text = await ui.SetCameraAsync(
                    call.GetVector3("location") is { } l ? ToVector(l) : null,
                    call.GetVector3("lookAt") is { } a ? ToVector(a) : null,
                    call.GetNumber("yaw") is { } yaw ? (float)yaw : null,
                    call.GetNumber("pitch") is { } pitch ? (float)pitch : null,
                    call.GetBool("frameAll", false),
                    ct).ConfigureAwait(false);
                return McpToolResult.Text(text);
            })
        { Title = "Move camera", Idempotent = true };

        yield return new McpTool("screenshot",
            "Returns a PNG of what the user sees: 'viewport' = the 3D map view (levels shown with show_levels), 'window' = the whole " +
            "ScumStudio window (pages, lists, values). Use it to check your work.",
            ToolSchema.Object().String("target", "What to capture (default viewport when a scene is shown, else window).", choices: ["viewport", "window"]).Build(),
            async (call, ct) =>
            {
                var target = call.GetString("target");
                byte[]? png = null;
                if (target is null or "viewport")
                {
                    png = await ui.CaptureViewportAsync(ct).ConfigureAwait(false);
                    if (png is null && target == "viewport")
                    {
                        return McpToolResult.Error("The 3D viewport has no scene (use show_levels) or OpenGL is not available.");
                    }

                    target = png is null ? "window" : "viewport";
                }

                png ??= await ui.CaptureWindowAsync(ct).ConfigureAwait(false);
                if (png is null)
                {
                    return McpToolResult.Error("Could not capture the window.");
                }

                var state = await ui.GetStateAsync(ct).ConfigureAwait(false);
                var caption = $"{target} screenshot — page '{state.Page}'" + (state.LoadedLevels.Count > 0 ? ", levels: " + string.Join(", ", state.LoadedLevels.Select(ShortName).Take(6)) : string.Empty)
                              + (state.SelectedActor is { } s ? ", selected: " + s.Actor : string.Empty);
                return new McpToolResult().AddImage(png).AddText(caption);
            })
        { Title = "Screenshot", ReadOnly = true };

        yield return new McpTool("show_item",
            "Opens a vehicle or weapon (stock or clone) on the Vehicles/Weapons page so the user sees its values.",
            ToolSchema.Object().String("item", "Asset name or package path.", required: true).Build(),
            async (call, ct) =>
            {
                var catalog = RequireCatalog();
                var path = AssetEditing.ResolvePackage(catalog, _host.Project?.State, call.RequireString("item"))
                           ?? throw new ToolArgumentException("No such item; use list_items.");
                return McpToolResult.Text(await ui.ShowItemAsync(path, ct).ConfigureAwait(false));
            })
        { Title = "Show vehicle/weapon", Idempotent = true };
    }
}
