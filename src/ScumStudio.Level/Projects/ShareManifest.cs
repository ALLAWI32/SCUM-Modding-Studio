using ScumStudio.Level.Editing;
using ScumStudio.Level.World;

namespace ScumStudio.Level.Projects;

/// <summary>Why a shared mod cannot be written, read or applied (the app shows each in the user's language).</summary>
public enum ShareProblem
{
    /// <summary>Not a zip file, or a damaged one.</summary>
    BadZip,

    /// <summary>A file name that could point outside the package (<c>..</c>, a drive, a backslash) or appears twice.</summary>
    UnsafeEntry,

    /// <summary>Game files or data that is not the project's own (packages, paks, an imported 3D model).</summary>
    GameData,

    /// <summary>The package, a file in it or the number of files is over the limits.</summary>
    TooLarge,

    /// <summary>Made by a newer ScumStudio with a package format this one does not read.</summary>
    NewerFormat,

    /// <summary>Holds an edit type this version does not know: a newer ScumStudio is needed.</summary>
    UnknownEdit,

    /// <summary>The description file is missing, unreadable, or a field is empty or too long.</summary>
    BadManifest,

    /// <summary>The picture is missing or not a JPEG.</summary>
    BadPicture,

    /// <summary>The edits clash with the project's own (see the detail).</summary>
    Conflicts,

    /// <summary>No project is open.</summary>
    NoProject,
}

/// <summary>A shared mod refused for <see cref="Problem"/>; the message is the technical detail (shown under the localized text).</summary>
public sealed class ShareException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ShareException(ShareProblem problem, string detail)
        : base(detail) => Problem = problem;

    /// <summary>Why it was refused.</summary>
    public ShareProblem Problem { get; }
}

/// <summary>
/// <c>manifest.json</c> of a shared mod: what it is, who made it, which map cells it changes and with how many edits (the
/// gallery shows these without unpacking the edits).
/// </summary>
public sealed record ShareManifest
{
    /// <summary>Format identifier of this version.</summary>
    public const string FormatId = "scumstudio.share/1";

    private const string FormatPrefix = "scumstudio.share/";

    /// <summary>The format (see <see cref="FormatId"/>).</summary>
    public string Format { get; init; } = FormatId;

    /// <summary>The mod's name (at most <see cref="ShareFile.MaxName"/> characters).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The display name its maker chose (may be empty; at most <see cref="ShareFile.MaxAuthor"/> characters).</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>What it does (at most <see cref="ShareFile.MaxDescription"/> characters).</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Map cells it changes (<c>A_0</c> …), map order.</summary>
    public IReadOnlyList<string> Sectors { get; init; } = [];

    /// <summary>True when it changes the levels of the whole island.</summary>
    public bool IslandWide { get; init; }

    /// <summary>True when it changes vehicles or items.</summary>
    public bool Assets { get; init; }

    /// <summary>The project files it carries (<c>craftables.json</c>, <c>EconomyOverride.json</c>).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>ScumStudio version that wrote it.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Game build its edits were made against, or <c>unknown</c>.</summary>
    public string GameBuild { get; init; } = "unknown";

    /// <summary>When it was made (UTC).</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Edits per group (<c>A_0</c>, <c>island</c>, <c>assets</c>).</summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();

    /// <summary>Number of edits.</summary>
    public int Edits { get; init; }

    /// <summary>Why the manifest is not acceptable, or null when it is.</summary>
    public ShareProblem? Validate()
    {
        if (!string.Equals(Format, FormatId, StringComparison.Ordinal))
        {
            return Format is not null && Format.StartsWith(FormatPrefix, StringComparison.Ordinal)
                   && int.TryParse(Format[FormatPrefix.Length..], out var version) && version > 1
                ? ShareProblem.NewerFormat
                : ShareProblem.BadManifest;
        }

        var fieldsOk = Name is { } name && name.Trim().Length > 0 && name.Length <= ShareFile.MaxName && !HasControl(name)
            && Author is { } author && author.Length <= ShareFile.MaxAuthor && !HasControl(author)
            && Description is { } description && description.Length <= ShareFile.MaxDescription
            && Sectors is not null && Sectors.All(s => s is { Length: 3 } && MapCell.TryParse(s, out var cell) && cell.ToString() == s)
            && Files is not null && Files.All(f => ShareFile.CarriedFiles.Contains(f))
            && GameBuild is not null && Counts is not null && Counts.Values.All(c => c >= 0) && Edits >= 0
            && Counts.Keys.All(k => k is JournalSlice.IslandKey or JournalSlice.AssetsKey || IsCell(k));
        return fieldsOk ? null : ShareProblem.BadManifest;

        static bool HasControl(string text) => text.Any(char.IsControl);

        static bool IsCell(string key) => key.Length == 3 && MapCell.TryParse(key, out var cell) && cell.ToString() == key;
    }
}
