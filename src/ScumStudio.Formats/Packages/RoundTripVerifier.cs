namespace ScumStudio.Formats.Packages;

/// <summary>Result of a byte-exact read → rebuild → compare check of one package.</summary>
public sealed record RoundTripResult
{
    /// <summary>Package path without extension.</summary>
    public required string Path { get; init; }

    /// <summary>True when the rebuilt .uasset equals the original.</summary>
    public bool UAssetIdentical { get; init; }

    /// <summary>True when the rebuilt .uexp equals the original.</summary>
    public bool UExpIdentical { get; init; }

    /// <summary>First differing .uasset offset (or the shorter length when one is a prefix), null when identical.</summary>
    public long? FirstUAssetDifference { get; init; }

    /// <summary>First differing .uexp offset, null when identical.</summary>
    public long? FirstUExpDifference { get; init; }

    /// <summary>Original .uasset length.</summary>
    public int OriginalUAssetLength { get; init; }

    /// <summary>Rebuilt .uasset length.</summary>
    public int RebuiltUAssetLength { get; init; }

    /// <summary>Original .uexp length.</summary>
    public int OriginalUExpLength { get; init; }

    /// <summary>Rebuilt .uexp length.</summary>
    public int RebuiltUExpLength { get; init; }

    /// <summary>Number of names in the package.</summary>
    public int NameCount { get; init; }

    /// <summary>Names whose stored hashes differ from the computed ones: (name, stored, computed).</summary>
    public IReadOnlyList<(string Name, (ushort, ushort) Stored, (ushort, ushort) Computed)> NameHashMismatches { get; init; } = [];

    /// <summary>
    /// How many of <see cref="NameHashMismatches"/> match the Python toolchain's hash instead
    /// (<see cref="NameHashes.ComputePythonToolchain"/>), i.e. names written by <c>ue4write.py</c>.
    /// </summary>
    public int PythonToolchainHashNames { get; init; }

    /// <summary>True when every stored name hash is either Unreal's or the Python toolchain's.</summary>
    public bool NameHashesExplained => PythonToolchainHashNames == NameHashMismatches.Count;

    /// <summary>Error message when the package could not be read or rebuilt.</summary>
    public string? Error { get; init; }

    /// <summary>True when both files are byte-identical.</summary>
    public bool IsIdentical => Error is null && UAssetIdentical && UExpIdentical;

    /// <summary>
    /// Set when the rebuild with recomputed name hashes differed only because of stored hashes: true when a second
    /// rebuild that reuses the stored hashes is byte-identical (typical for packages written by <c>ue4write.py</c>).
    /// </summary>
    public bool? IdenticalWithStoredHashes { get; init; }

    /// <summary>True when the layout round-trips: identical, or identical once the stored name hashes are kept.</summary>
    public bool IsLayoutIdentical => IsIdentical || IdenticalWithStoredHashes == true;

    /// <summary>One-line summary in the spirit of <c>roundtrip_check</c>'s output.</summary>
    public override string ToString()
    {
        if (Error is not null)
        {
            return $"ERROR {Path}: {Error}";
        }

        if (IsIdentical)
        {
            return $"OK   {Path} (names={NameCount}, uasset={OriginalUAssetLength}, uexp={OriginalUExpLength})";
        }

        if (IdenticalWithStoredHashes == true)
        {
            return $"OK   {Path} (identical with the stored name hashes; {NameHashMismatches.Count} non-Unreal hashes, {PythonToolchainHashNames} from the Python toolchain)";
        }

        var parts = new List<string>();
        if (!UAssetIdentical)
        {
            parts.Add($"uasset differs: len {RebuiltUAssetLength} vs {OriginalUAssetLength}, first diff @ {FirstUAssetDifference}");
        }

        if (!UExpIdentical)
        {
            parts.Add($"uexp differs: len {RebuiltUExpLength} vs {OriginalUExpLength}, first diff @ {FirstUExpDifference}");
        }

        if (NameHashMismatches.Count > 0)
        {
            parts.Add($"hash_mismatches={NameHashMismatches.Count} (python-toolchain hashes: {PythonToolchainHashNames}; e.g. '{NameHashMismatches[0].Name}')");
        }

        return $"DIFF {Path}: {string.Join("; ", parts)}";
    }
}

/// <summary>
/// Byte-exact round-trip verification: read a package, rebuild it with <see cref="PackageWriter"/> and compare
/// (port of <c>ue4write.py roundtrip_check</c>, which rebuilds with freshly computed name hashes).
/// </summary>
public static class RoundTripVerifier
{
    /// <summary>Verifies one package given by path (with or without extension).</summary>
    /// <param name="path">Package path.</param>
    /// <param name="useStoredNameHashes">Reuse the stored name hashes instead of recomputing them.</param>
    public static RoundTripResult Verify(string path, bool useStoredNameHashes = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            return Verify(CookedPackage.Load(path), useStoredNameHashes);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new RoundTripResult { Path = StripExtension(path), Error = ex.Message };
        }
    }

    /// <summary>Verifies an already loaded package.</summary>
    public static RoundTripResult Verify(CookedPackage package, bool useStoredNameHashes = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        var mismatches = new List<(string, (ushort, ushort), (ushort, ushort))>();
        var python = 0;
        foreach (var n in package.NameEntries)
        {
            var computed = NameHashes.Compute(n.Value, n.IsWide);
            var stored = (n.NonCasePreservingHash, n.CasePreservingHash);
            if (stored != computed)
            {
                mismatches.Add((n.Value, stored, computed));
                if (stored == NameHashes.ComputePythonToolchain(n.Value))
                {
                    python++;
                }
            }
        }

        PackageBytes rebuilt;
        try
        {
            rebuilt = PackageWriter.Rebuild(package, useStoredNameHashes);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            return new RoundTripResult { Path = package.BasePath ?? "<memory>", Error = "rebuild failed: " + ex.Message, NameHashMismatches = mismatches, PythonToolchainHashNames = python };
        }

        var d1 = FirstDifference(rebuilt.UAsset, package.UAsset);
        var d2 = FirstDifference(rebuilt.UExp, package.UExp);
        bool? withStored = null;
        if (!useStoredNameHashes && mismatches.Count > 0 && (d1 is not null || d2 is not null))
        {
            var again = PackageWriter.Rebuild(package, useStoredNameHashes: true);
            withStored = FirstDifference(again.UAsset, package.UAsset) is null && FirstDifference(again.UExp, package.UExp) is null;
        }

        return new RoundTripResult
        {
            Path = package.BasePath ?? "<memory>",
            UAssetIdentical = d1 is null,
            UExpIdentical = d2 is null,
            FirstUAssetDifference = d1,
            FirstUExpDifference = d2,
            OriginalUAssetLength = package.UAsset.Length,
            RebuiltUAssetLength = rebuilt.UAsset.Length,
            OriginalUExpLength = package.UExp.Length,
            RebuiltUExpLength = rebuilt.UExp.Length,
            NameCount = package.Names.Count,
            NameHashMismatches = mismatches,
            PythonToolchainHashNames = python,
            IdenticalWithStoredHashes = withStored,
        };
    }

    /// <summary>
    /// Enumerates package base paths (files ending in .uasset or .umap) under a directory, sorted ordinally.
    /// </summary>
    public static IReadOnlyList<string> FindPackages(string directory, bool recursive = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directory, "*.*", option)
            .Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Verifies every package under a directory (in parallel).</summary>
    public static IReadOnlyList<RoundTripResult> VerifyDirectory(string directory, bool recursive = true, bool useStoredNameHashes = false)
    {
        var files = FindPackages(directory, recursive);
        var results = new RoundTripResult[files.Count];
        Parallel.For(0, files.Count, i => results[i] = Verify(files[i], useStoredNameHashes));
        return results;
    }

    /// <summary>Index of the first differing byte; the shorter length when one is a prefix of the other; null when equal.</summary>
    public static long? FirstDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var common = a.CommonPrefixLength(b);
        if (common == a.Length && common == b.Length)
        {
            return null;
        }

        return common;
    }

    private static string StripExtension(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext is ".uasset" or ".umap" or ".uexp" ? path[..^ext.Length] : path;
    }
}
