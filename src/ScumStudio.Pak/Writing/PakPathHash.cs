namespace ScumStudio.Pak.Writing;

/// <summary>
/// Path hash used by the pak path-hash index (FPakFile::HashPath, pak version 11 = Fnv64BugFix):
/// 64-bit FNV-1a over the lower-cased path relative to the mount point, as UTF-16LE (TCHAR on Windows),
/// with the offset basis increased by the pak's path hash seed.
/// </summary>
public static class PakPathHash
{
    private const ulong OffsetBasis = 0xCBF29CE484222325;
    private const ulong Prime = 0x00000100000001B3;

    /// <summary>Hashes <paramref name="relativePath"/> (e.g. <c>SCUM/Content/Foo.uasset</c>).</summary>
    public static ulong Compute(string relativePath, ulong seed = 0)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var hash = unchecked(OffsetBasis + seed);
        foreach (var c in relativePath)
        {
            var lower = char.ToLowerInvariant(c);
            hash = unchecked((hash ^ (byte)(lower & 0xFF)) * Prime);
            hash = unchecked((hash ^ (byte)(lower >> 8)) * Prime);
        }

        return hash;
    }
}
