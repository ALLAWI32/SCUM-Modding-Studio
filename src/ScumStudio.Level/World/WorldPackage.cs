namespace ScumStudio.Level.World;

/// <summary>What a package under <c>Maps/The_Island</c> is, derived from its name and folder.</summary>
public enum WorldPackageKind
{
    /// <summary>Anything else: special-purpose sublevels (<c>AquaticVolumes</c>, <c>Biomes</c>, <c>B3_Castle_Ruins</c>, ...)
    /// and non-level assets such as landscape layer infos.</summary>
    Misc,

    /// <summary>The persistent level <c>The_Island.umap</c> (it streams every sublevel).</summary>
    Persistent,

    /// <summary>Point-of-interest / grid sublevel <c>&lt;Cell&gt;_&lt;Row&gt;_&lt;Name&gt;...</c>, e.g. <c>A_0_Outpost_Exterior</c>.</summary>
    Poi,

    /// <summary>Landscape tile <c>Landscape_&lt;Cell&gt;_&lt;Row&gt;_&lt;Quadrant&gt;[b|c|d]</c> (heightmaps live inside).</summary>
    Landscape,

    /// <summary>TV bunker sublevel <c>TV_Base_&lt;Cell&gt;_&lt;Row&gt;[_...]</c>.</summary>
    TvBase,

    /// <summary>Abandoned city sublevel in <c>AbandonedCity_PripyatLike/</c>.</summary>
    Pripyat,

    /// <summary>Lightmap/build data package <c>&lt;Level&gt;_BuiltData</c> (MapBuildDataRegistry).</summary>
    BuiltData,

    /// <summary>Hierarchical LOD proxy package in <c>HLOD/</c>.</summary>
    Hlod,
}

/// <summary>A package found under the The_Island maps folder, classified by <see cref="WorldNameParser"/>.</summary>
/// <param name="FilePath">Virtual file path including extension, e.g. <c>SCUM/Content/ConZ_Files/Maps/The_Island/A_0_Outpost.umap</c>.</param>
/// <param name="PackagePath">UE package path, e.g. <c>/Game/ConZ_Files/Maps/The_Island/A_0_Outpost</c>.</param>
/// <param name="Name">Package name, e.g. <c>A_0_Outpost</c>.</param>
/// <param name="Folder">Sub-folder relative to the world root (<c>""</c>, <c>AbandonedCity_PripyatLike</c>, <c>HLOD</c>, ...).</param>
/// <param name="Kind">Classification.</param>
/// <param name="Cell">Map cell parsed from the name, or null.</param>
/// <param name="IsMap">True for a level package (<c>.umap</c>).</param>
public sealed record WorldPackage(
    string FilePath,
    string PackagePath,
    string Name,
    string Folder,
    WorldPackageKind Kind,
    MapCell? Cell,
    bool IsMap)
{
    /// <summary>Landscape quadrant 1-4 (landscape tiles only).</summary>
    public int? LandscapeQuadrant { get; init; }

    /// <summary>Landscape variant letter <c>b</c>, <c>c</c> or <c>d</c>; null for the base tile or other kinds.</summary>
    public char? LandscapeVariant { get; init; }

    /// <summary>For <see cref="WorldPackageKind.BuiltData"/> and <see cref="WorldPackageKind.Hlod"/>: name of the level
    /// the package belongs to (e.g. <c>A_0_Outpost_Exterior</c>).</summary>
    public string? Owner { get; init; }

    /// <summary>For levels: package path of the level's <c>_BuiltData</c> package when one exists in the index.</summary>
    public string? BuiltDataPackage { get; init; }

    /// <summary>
    /// For levels: whether the persistent level's StreamingLevels list references this package. Null when the
    /// persistent level was not read (names-only index).
    /// </summary>
    public bool? IsStreamed { get; init; }

    /// <summary>
    /// For sublevels: the cooked World Composition tile info (position, bounds, layer, parent tile) read from the package
    /// header by <see cref="WorldIndex.WithTileInfo"/>; null when not read or when the package carries none.
    /// </summary>
    public Formats.Packages.WorldTileInfo? Tile { get; init; }

    /// <summary>Package path of the parent tile (from <see cref="Tile"/>), or null for a root tile / unknown.</summary>
    public string? ParentPackagePath { get; init; }

    /// <summary>True for a sublevel: a level package other than the persistent level.</summary>
    public bool IsSublevel => IsMap && Kind != WorldPackageKind.Persistent;
}
