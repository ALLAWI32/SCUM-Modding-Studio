namespace ScumStudio.Pak.Reading;

/// <summary>Summary of one pak (or IoStore container) known to a <see cref="PakFileSource"/>.</summary>
/// <param name="Name">File name, e.g. <c>pakchunk96-F16_P.pak</c>.</param>
/// <param name="FilePath">Full path of the pak file.</param>
/// <param name="MountPoint">Mount point as reported by CUE4Parse (already made relative, typically empty).</param>
/// <param name="FileCount">Number of entries (0 while unmounted).</param>
/// <param name="IsEncrypted">True when the pak's index or files are encrypted.</param>
/// <param name="IsMounted">False when it could not be mounted (usually: encrypted and no/incorrect AES key).</param>
/// <param name="Version">Pak file version number (11 for UE 4.27), or null for non-pak containers.</param>
/// <param name="ReadOrder">CUE4Parse priority; when two paks contain the same path the higher value wins.</param>
public sealed record PakArchiveInfo(
    string Name,
    string FilePath,
    string MountPoint,
    int FileCount,
    bool IsEncrypted,
    bool IsMounted,
    int? Version,
    long ReadOrder);
