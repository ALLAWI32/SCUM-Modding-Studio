using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScumStudio.Pak.Writing;

/// <summary>
/// Places a copy of a stock <c>.sig</c> file next to a mod pak (<c>pakchunk96-F16_P.pak</c> gets
/// <c>pakchunk96-F16_P.sig</c>), as the game expects for paks in the official Paks folder
/// (port of make_pak.py <c>--deploy</c>: <c>shutil.copy2(SIG_SRC, dsig)</c>, SIG_SRC = pakchunk44-WindowsNoEditor.sig).
/// </summary>
/// <remarks>
/// This is a plain file copy of the user's own stock signature; the signature is not modified or forged.
/// SCUM mod paks are unsigned: the client loads them through the launcher's <c>~mods</c> with <c>-nobattleye</c>,
/// the dedicated server with <c>-fileopenlog</c>.
/// </remarks>
public static class SigCopier
{
    /// <summary>Name of the stock signature the user's toolchain copies.</summary>
    public const string DefaultStockSigName = "pakchunk44-WindowsNoEditor.sig";

    /// <summary>The <c>.sig</c> path that belongs to <paramref name="pakPath"/>.</summary>
    public static string GetSigPath(string pakPath) => Path.ChangeExtension(Path.GetFullPath(pakPath), ".sig");

    /// <summary>
    /// Copies <paramref name="sourceSig"/> to the <c>.sig</c> path of <paramref name="destinationPak"/> and returns that path.
    /// </summary>
    /// <exception cref="FileNotFoundException">The source signature does not exist.</exception>
    /// <exception cref="ArgumentException">The source is not a <c>.sig</c> file, or it is the destination itself.</exception>
    public static string Copy(string sourceSig, string destinationPak, bool overwrite = true, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSig);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPak);
        var source = Path.GetFullPath(sourceSig);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Signature file not found: {source}", source);
        }

        if (!string.Equals(Path.GetExtension(source), ".sig", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Expected a .sig file, got '{Path.GetFileName(source)}'.", nameof(sourceSig));
        }

        var destination = GetSigPath(destinationPak);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, destination, comparison))
        {
            throw new ArgumentException("The source signature is the destination signature.", nameof(sourceSig));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite);

        // The copy keeps the stock file's date (the day the game last updated), which looked like an old file left behind
        // next to a fresh pak (owner). It is copied anew with every export: give it the export's time.
        File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
        (logger ?? NullLogger.Instance).LogInformation("Copied signature {Source} -> {Destination}.", Path.GetFileName(source), destination);
        return destination;
    }
}
