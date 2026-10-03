using Microsoft.Extensions.Logging;

namespace ScumStudio.Pak.Reading;

/// <summary>Options for <see cref="PakFileSource"/>.</summary>
public sealed record PakFileSourceOptions
{
    /// <summary>
    /// AES-256 key (hex, see <see cref="AesKeyText"/>) for encrypted stock paks, or null for unencrypted mod paks only.
    /// Supplied by the user at runtime; never logged.
    /// </summary>
    public string? AesKey { get; init; }

    /// <summary>
    /// Optional filter on pak file names when opening a directory (e.g. exclude <c>server_*.pak</c>).
    /// Receives the file name only. Null = every <c>*.pak</c>/<c>*.utoc</c> in the directory.
    /// </summary>
    public Func<string, bool>? PakFileFilter { get; init; }

    /// <summary>Unreal project name used for path normalisation (default <c>SCUM</c>).</summary>
    public string ProjectName { get; init; } = PakPaths.DefaultProjectName;

    /// <summary>Optional logger.</summary>
    public ILogger? Logger { get; init; }
}
