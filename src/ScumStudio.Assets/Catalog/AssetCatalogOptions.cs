using Microsoft.Extensions.Logging;

namespace ScumStudio.Assets.Catalog;

/// <summary>Options for opening an <see cref="AssetCatalog"/>.</summary>
public sealed record AssetCatalogOptions
{
    /// <summary>
    /// AES-256 key as 64 hex digits (optional <c>0x</c> prefix) for encrypted stock paks. Entered by the user at runtime;
    /// never logged, printed or included in exception messages.
    /// </summary>
    public string? AesKey { get; init; }

    /// <summary>Virtual root / project name of the cooked content (default <c>SCUM</c>).</summary>
    public string ProjectName { get; init; } = AssetPaths.DefaultProjectName;

    /// <summary>Optional filter on pak file names (e.g. only <c>pakchunk0*</c>); null mounts every container.</summary>
    public Func<string, bool>? PakFileFilter { get; init; }

    /// <summary>
    /// Extra loose folders (project roots containing <c>Content/</c>, or their parents) layered on top of the main source;
    /// later entries win over earlier ones and over every pak.
    /// </summary>
    public IReadOnlyList<string> LooseOverlays { get; init; } = [];

    /// <summary>Logger for diagnostics (never receives key material).</summary>
    public ILogger? Logger { get; init; }
}
