using ScumStudio.Level.World;

namespace ScumStudio.Level.Reading;

/// <summary>
/// Reads cooked level packages into parser-independent <see cref="LevelData"/>. The production implementation is
/// <see cref="Cue4ParseLevelReader"/>; tests use an in-memory fake, which keeps <see cref="Model.LevelDocument"/> and
/// <see cref="WorldIndex"/> testable without game files.
/// </summary>
/// <remarks>Paths may be package paths (<c>/Game/...</c>), object paths or virtual file paths (<c>SCUM/Content/...umap</c>).</remarks>
public interface ILevelReader
{
    /// <summary>Human-readable description of the source (for logs).</summary>
    string DisplayName { get; }

    /// <summary>True when a level package exists at <paramref name="packagePath"/>.</summary>
    bool LevelExists(string packagePath);

    /// <summary>Reads the actors and components of a level package.</summary>
    /// <exception cref="FileNotFoundException">No such package.</exception>
    /// <exception cref="InvalidDataException">The package is not a level (no <c>World</c>/<c>Level</c> export).</exception>
    LevelData ReadLevel(string packagePath, CancellationToken cancellationToken = default);

    /// <summary>Reads a persistent level's <c>UWorld::StreamingLevels</c> entries.</summary>
    /// <exception cref="FileNotFoundException">No such package.</exception>
    /// <exception cref="InvalidDataException">The package has no <c>World</c> export.</exception>
    IReadOnlyList<StreamingLevelInfo> ReadStreamingLevels(string packagePath, CancellationToken cancellationToken = default);
}
