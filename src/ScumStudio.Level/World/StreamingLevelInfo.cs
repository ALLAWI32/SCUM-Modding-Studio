using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.World;

/// <summary>
/// One entry of a persistent level's <c>UWorld::StreamingLevels</c> (a <c>ULevelStreaming</c> subobject such as
/// <c>LevelStreamingAlwaysLoaded</c> or <c>LevelStreamingDynamic</c>).
/// </summary>
/// <param name="ExportName">Name of the streaming object in the persistent level package.</param>
/// <param name="ClassName">Its class, e.g. <c>LevelStreamingDynamic</c>.</param>
/// <param name="WorldAsset">The <c>WorldAsset</c> soft object path, e.g. <c>/Game/ConZ_Files/Maps/The_Island/A_0_Outpost.A_0_Outpost</c>;
/// empty when the property is missing.</param>
/// <param name="PackagePath">Package part of <paramref name="WorldAsset"/> (e.g. <c>/Game/ConZ_Files/Maps/The_Island/A_0_Outpost</c>),
/// or <c>PackageNameToLoad</c> when that is set.</param>
/// <param name="LevelTransform">The <c>LevelTransform</c> applied to the sublevel (identity when absent).</param>
/// <param name="InitiallyLoaded"><c>bInitiallyLoaded</c>.</param>
/// <param name="InitiallyVisible"><c>bInitiallyVisible</c>.</param>
public sealed record StreamingLevelInfo(
    string ExportName,
    string ClassName,
    string WorldAsset,
    string PackagePath,
    FTransform LevelTransform,
    bool InitiallyLoaded = false,
    bool InitiallyVisible = false)
{
    /// <summary>Last segment of <see cref="PackagePath"/>, e.g. <c>A_0_Outpost</c>.</summary>
    public string LevelName => PackagePath[(PackagePath.LastIndexOf('/') + 1)..];
}

/// <summary>
/// Result of comparing the sublevel packages found in the sources with the persistent level's StreamingLevels.
/// </summary>
/// <param name="StreamingLevelCount">Number of StreamingLevels entries in the persistent level.</param>
/// <param name="Matched">Entries whose package exists in the index.</param>
/// <param name="MissingPackages">Entries referencing a package that is not in the index (e.g. a pak not mounted).</param>
/// <param name="NotStreamed">Sublevel packages in the folder that the persistent level does not stream
/// (e.g. levels streamed by Blueprints or plugins, or leftovers).</param>
public sealed record StreamingCrossCheck(
    int StreamingLevelCount,
    int Matched,
    IReadOnlyList<StreamingLevelInfo> MissingPackages,
    IReadOnlyList<WorldPackage> NotStreamed)
{
    /// <summary>True when every streamed level exists and every sublevel is streamed.</summary>
    public bool IsConsistent => MissingPackages.Count == 0 && NotStreamed.Count == 0;
}
