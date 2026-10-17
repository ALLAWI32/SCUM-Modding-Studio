using ScumStudio.App.Localization;
using ScumStudio.Level.Projects;

namespace ScumStudio.App.Services;

/// <summary>
/// What can happen when a project cuts terrain holes (owner, 2026-10-16: "show a clear warning list"), shown by the Map at
/// the hole tool's first use in each project and as a short reminder at export, until "Do not show again" for that project.
/// </summary>
public static class TerrainHoleWarning
{
    /// <summary>The string keys of the list, in order.</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "Map.Hole.Warn.Update", "Map.Hole.Warn.Conflict", "Map.Hole.Warn.Server", "Map.Hole.Warn.Ground", "Map.Hole.Warn.Edge",
        "Map.Hole.Warn.Far", "Map.Hole.Warn.Ai", "Map.Hole.Warn.Backup", "Map.Hole.Warn.Test",
    ];

    /// <summary>The list in the current language, one line per item.</summary>
    public static IReadOnlyList<string> Items() => Keys.Select(Loc.T).ToList();

    /// <summary>The key a project is remembered by: its folder, or an empty string without a project.</summary>
    public static string KeyOf(Project? project) => project is null ? string.Empty : Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.DirectoryPath));

    /// <summary>True when "Do not show again" was ticked for the project <paramref name="key"/>.</summary>
    public static bool IsDismissed(UiState state, string key)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.HoleWarningDismissed.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Remembers "Do not show again" for the project <paramref name="key"/>.</summary>
    public static void Dismiss(UiStateStore store, string key)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.Update(u => u with { HoleWarningDismissed = [.. u.HoleWarningDismissed.Where(k => !string.Equals(k, key, StringComparison.OrdinalIgnoreCase)), key] });
    }

    /// <summary>The short reminder an export of <paramref name="project"/> shows, or null (no holes, or not wanted for this project).</summary>
    public static string? ExportReminder(UiState state, Project? project) =>
        project?.State.TerrainHoles is { Count: > 0 } && !IsDismissed(state, KeyOf(project)) ? Loc.T("Map.Hole.Warn.Reminder") : null;
}
