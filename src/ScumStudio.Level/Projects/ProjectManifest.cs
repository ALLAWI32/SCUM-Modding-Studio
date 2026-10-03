namespace ScumStudio.Level.Projects;

/// <summary>Kind of game-file source a project reads from.</summary>
public enum ProjectSourceKind
{
    /// <summary>A Paks folder or a single <c>.pak</c> (stock game or server install).</summary>
    Paks,

    /// <summary>A folder of loose extracted cooked files (<c>SCUM/Content/...</c>).</summary>
    Loose,
}

/// <summary>Which cook a source belongs to; client and server paks are exported separately.</summary>
public enum ProjectSourceRole
{
    /// <summary>WindowsNoEditor (client) content.</summary>
    Client,

    /// <summary>WindowsServer content.</summary>
    Server,
}

/// <summary>A game-file source of a project. Paths are the user's own install locations (never part of the repository).</summary>
/// <param name="Path">Folder or file path on the user's machine.</param>
/// <param name="Kind">Paks or loose files.</param>
/// <param name="Role">Client or server cook.</param>
public sealed record ProjectSource(string Path, ProjectSourceKind Kind = ProjectSourceKind.Paks, ProjectSourceRole Role = ProjectSourceRole.Client)
{
    /// <summary>
    /// Creates a source for <paramref name="path"/>, detecting the kind: a <c>.pak</c> file or a folder with
    /// <c>.pak</c>/<c>.utoc</c> files directly inside is <see cref="ProjectSourceKind.Paks"/>, anything else loose.
    /// </summary>
    public static ProjectSource Detect(string path, ProjectSourceRole role = ProjectSourceRole.Client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        var isPaks = File.Exists(full)
            ? full.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
            : Directory.Exists(full) && Directory.EnumerateFiles(full, "*", SearchOption.TopDirectoryOnly)
                .Any(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
        return new ProjectSource(full, isPaks ? ProjectSourceKind.Paks : ProjectSourceKind.Loose, role);
    }
}

/// <summary>Contents of <c>project.json</c>.</summary>
public sealed record ProjectManifest
{
    /// <summary>Format identifier of this file.</summary>
    public const string FormatId = "scumstudio.project/1";

    /// <summary>Format identifier (for future migrations).</summary>
    public string Format { get; init; } = FormatId;

    /// <summary>Project (mod) name.</summary>
    public required string Name { get; init; }

    /// <summary>Game build the edits were made against, e.g. <c>1.3.3.4.149664</c>; <c>unknown</c> when not given.</summary>
    public string GameBuild { get; init; } = "unknown";

    /// <summary>Creation time (UTC).</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Last save time (UTC).</summary>
    public DateTimeOffset Modified { get; init; }

    /// <summary>Game-file sources (client and server paks or loose folders).</summary>
    public IReadOnlyList<ProjectSource> Sources { get; init; } = [];
}
