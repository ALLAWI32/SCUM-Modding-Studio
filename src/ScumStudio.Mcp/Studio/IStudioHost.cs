using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Level.World;

namespace ScumStudio.Mcp.Studio;

/// <summary>
/// What the MCP tools drive: the game files, the open project and its journal, and — in the desktop app — the live UI.
/// Implemented by the app (edits show up immediately in the open pages) and by the headless CLI host.
/// </summary>
public interface IStudioHost
{
    /// <summary><c>app</c> or <c>cli</c>.</summary>
    string Kind { get; }

    /// <summary>The connected game files (client cook), or null.</summary>
    AssetCatalog? Catalog { get; }

    /// <summary>The world index of <see cref="Catalog"/> (The_Island's sublevels), or null.</summary>
    WorldIndex? World { get; }

    /// <summary>The open project, or null.</summary>
    Project? Project { get; }

    /// <summary>Opens game files: a Paks folder (with the stored/configured key), a single .pak or an extracted folder; null = the configured game Paks folder.</summary>
    Task<string> OpenSourceAsync(string? path, CancellationToken cancellationToken);

    /// <summary>Creates <c>&lt;parentFolder&gt;/&lt;name&gt;.ssproj</c> and opens it.</summary>
    Task<Project> CreateProjectAsync(string parentFolder, string name, CancellationToken cancellationToken);

    /// <summary>Opens a project folder.</summary>
    Task<Project> OpenProjectAsync(string path, CancellationToken cancellationToken);

    /// <summary>Applies an edit to the open project's journal (the app refreshes its pages).</summary>
    /// <exception cref="InvalidOperationException">No project, or the edit is not valid now.</exception>
    JournalEntry Apply(EditOp op);

    /// <summary>Undoes the last edit; null when there is none.</summary>
    JournalEntry? Undo();

    /// <summary>Redoes the last undone edit; null when there is none.</summary>
    JournalEntry? Redo();

    /// <summary>Exports the project (client pak, plus the server pak when asked and a server source is known).</summary>
    Task<IReadOnlyList<ExportResult>> ExportAsync(ExportOptions options, bool includeServer, CancellationToken cancellationToken);

    /// <summary>Live UI of the desktop app, or null for headless hosts.</summary>
    IStudioUi? Ui { get; }

    /// <summary>Reports what an AI tool just did (app: log + toast).</summary>
    void ReportActivity(string tool, string summary, bool isError);
}

/// <summary>State of the app's UI for <c>get_status</c>.</summary>
/// <param name="Page">Current page key.</param>
/// <param name="LoadedLevels">Level packages shown in the map viewport.</param>
/// <param name="SelectedActor">Selected actor, or null.</param>
/// <param name="CameraLocation">Camera location (UE cm), or null without a viewport.</param>
/// <param name="CameraYaw">Camera yaw (UE degrees).</param>
/// <param name="CameraPitch">Camera pitch (degrees).</param>
public sealed record StudioUiState(string Page, IReadOnlyList<string> LoadedLevels, ActorRef? SelectedActor, FVector? CameraLocation, float CameraYaw, float CameraPitch);

/// <summary>The desktop app's UI actions available to the AI.</summary>
public interface IStudioUi
{
    /// <summary>Current UI state.</summary>
    Task<StudioUiState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Shows a page (map, vehicles, weapons, assets, projects, settings).</summary>
    Task NavigateAsync(string page, CancellationToken cancellationToken);

    /// <summary>Loads level packages into the map viewport (landscapeStep: 1 = full terrain detail, 4 = coarse).</summary>
    Task<string> ShowLevelsAsync(IReadOnlyList<string> packagePaths, int landscapeStep, CancellationToken cancellationToken);

    /// <summary>Selects an actor in the map (must be loaded) and optionally frames the camera on it.</summary>
    Task<string> SelectActorAsync(ActorRef actor, bool frame, CancellationToken cancellationToken);

    /// <summary>Moves the viewport camera: location and/or look-at point (UE cm), or yaw/pitch; "all" frames the scene.</summary>
    Task<string> SetCameraAsync(FVector? location, FVector? lookAt, float? yaw, float? pitch, bool frameAll, CancellationToken cancellationToken);

    /// <summary>PNG of the 3D viewport (null when no scene is shown / no OpenGL).</summary>
    Task<byte[]?> CaptureViewportAsync(CancellationToken cancellationToken);

    /// <summary>PNG of the whole window (the 3D viewport may appear empty in it; use <see cref="CaptureViewportAsync"/> for that).</summary>
    Task<byte[]?> CaptureWindowAsync(CancellationToken cancellationToken);

    /// <summary>Selects an asset on the Vehicles or Weapons page (navigates there).</summary>
    Task<string> ShowItemAsync(string packagePath, CancellationToken cancellationToken);
}
