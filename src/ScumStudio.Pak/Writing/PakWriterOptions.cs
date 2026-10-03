using System.IO.Compression;

namespace ScumStudio.Pak.Writing;

/// <summary>Options for <see cref="PakWriter"/>. Defaults reproduce the user's working SCUM mod paks.</summary>
public sealed record PakWriterOptions
{
    /// <summary>Mount point (default <c>../../../</c>, so entries resolve to <c>SCUM/Content/...</c>).</summary>
    public string MountPoint { get; init; } = PakPaths.DefaultMountPoint;

    /// <summary>Entry compression (default <see cref="PakCompression.None"/>).</summary>
    public PakCompression Compression { get; init; } = PakCompression.None;

    /// <summary>Zlib effort (only for <see cref="PakCompression.Zlib"/>).</summary>
    public CompressionLevel ZlibLevel { get; init; } = CompressionLevel.Optimal;

    /// <summary>Compression block size, default 64 KiB like UnrealPak.</summary>
    public int CompressionBlockSize { get; init; } = PakFormat.DefaultCompressionBlockSize;

    /// <summary>Seed of the path-hash index (UnrealPak and repak default: 0).</summary>
    public ulong PathHashSeed { get; init; }

    /// <summary>Unreal project name (default <c>SCUM</c>).</summary>
    public string ProjectName { get; init; } = PakPaths.DefaultProjectName;

    /// <summary>
    /// When true (default), only <c>SCUM/Content/**</c> and <c>SCUM/AssetRegistry.bin</c> from a staging directory
    /// are packed; anything else (reports, notes) is skipped with a warning.
    /// </summary>
    public bool OnlyGameContent { get; init; } = true;

    /// <summary>When false, <c>SCUM/AssetRegistry.bin</c> is left out even if staged.</summary>
    public bool IncludeAssetRegistry { get; init; } = true;
}
