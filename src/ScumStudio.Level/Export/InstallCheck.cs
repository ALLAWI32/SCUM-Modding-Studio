using System.Globalization;
using System.Text.RegularExpressions;
using ScumStudio.Pak.Reading;

namespace ScumStudio.Level.Export;

/// <summary>Whose mods folder a finding is about.</summary>
public enum InstallSide
{
    /// <summary>The game on this PC (Settings → game folder): <c>SCUM\Content\Paks\~mods</c>.</summary>
    Game,

    /// <summary>The dedicated server (Settings → server folder): its <c>SCUM\Content\Paks\~mods</c>.</summary>
    Server,
}

/// <summary>How much a finding matters.</summary>
public enum InstallSeverity
{
    /// <summary>As it should be.</summary>
    Ok,

    /// <summary>Good to know (what was not checked, the fixed rules).</summary>
    Info,

    /// <summary>May hide or break something.</summary>
    Warning,

    /// <summary>Breaks the mod in the game: fix it.</summary>
    Problem,
}

/// <summary>What <see cref="InstallCheck"/> found; the app words each one.</summary>
public enum InstallIssue
{
    /// <summary>The Paks folder is not set or not found (Settings): nothing could be checked on that side.</summary>
    NoFolder,

    /// <summary>None of this project's paks is in <c>~mods</c> (<see cref="InstallFinding.Folder"/>: the export folder to copy).</summary>
    NotInstalled,

    /// <summary>This project's paks in <c>~mods</c> that match the last export (or that have no export to compare with).</summary>
    Installed,

    /// <summary>Installed paks older than (or not the same as) the last export's: copy them again.</summary>
    Stale,

    /// <summary>Paks of the last export that are not in <c>~mods</c>.</summary>
    Missing,

    /// <summary>Two or more page paks installed without the Registry pak: the game does not know their new assets.</summary>
    MissingRegistry,

    /// <summary>The whole project's pak and page paks of the same project installed together.</summary>
    BothModes,

    /// <summary>Paks of this mod under another chunk number (an older export): they stay mounted beside the new ones.</summary>
    OldName,

    /// <summary>Other paks that load before this mod's and carry their own <c>AssetRegistry.bin</c>: this mod's new assets are unknown.</summary>
    RegistryAbove,

    /// <summary>Other paks that load after this mod's and carry their own <c>AssetRegistry.bin</c>: their new assets are unknown while this mod is installed.</summary>
    RegistryBelow,

    /// <summary>Paks that could not be looked into (encrypted with another key, or damaged).</summary>
    Unreadable,

    /// <summary>Mod paks directly in <c>SCUM\Content\Paks</c> (not <c>~mods</c>): the game refuses them there.</summary>
    LooseInPaks,

    /// <summary>Server paks of this mod without their <c>.sig</c>.</summary>
    NoSig,

    /// <summary>A pak of the other cook: the client pak on the server, or the server pak in the game.</summary>
    WrongCook,

    /// <summary>
    /// Paks the export just wrote (<see cref="InstallCheckInput.Written"/>) that are not in <c>~mods</c>, or not the same
    /// file there (<see cref="InstallFinding.Folder"/>: the folder they were written to).
    /// </summary>
    NotCopied,

    /// <summary>
    /// The export just made is one install mode (the whole project, or pages) and only this project's paks of the other mode
    /// are in <c>~mods</c>: copying the new export there installs both.
    /// </summary>
    OtherMode,

    /// <summary>
    /// Paks of this mod in a subfolder of <c>~mods</c>: the engine mounts every pak under <c>Content\Paks</c>, subfolders
    /// too, so they load beside (and can hide) the copies at the top.
    /// </summary>
    InSubfolder,

    /// <summary>The fixed rule: a server starts with <c>-fileopenlog</c> or refuses every mod pak.</summary>
    NoteFileOpenLog,

    /// <summary>The fixed rule: every player needs every file of the Client folder.</summary>
    NoteFriends,
}

/// <summary>One thing <see cref="InstallCheck"/> found.</summary>
/// <param name="Side">The game or the server.</param>
/// <param name="Issue">What it is.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Files">The pak file names it is about (empty for a note).</param>
/// <param name="Folder">The folder it is about: the mods folder, or for <see cref="InstallIssue.NotInstalled"/> and
/// <see cref="InstallIssue.Missing"/> the export folder to copy from (null when there is none).</param>
public sealed record InstallFinding(InstallSide Side, InstallIssue Issue, InstallSeverity Severity, IReadOnlyList<string> Files, string? Folder = null);

/// <summary>What <see cref="InstallCheck.Check"/> looks at.</summary>
public sealed record InstallCheckInput
{
    /// <summary>The mod names of the project (its name and the export's mod name; see <see cref="OwnPaks.ProjectNames"/>).</summary>
    public required IReadOnlyList<string> ModNames { get; init; }

    /// <summary>The project's pak chunk (<see cref="ProjectExporter.DefaultPakChunkIndex"/>).</summary>
    public int PakChunkIndex { get; init; } = ProjectExporter.DefaultPakChunkIndex;

    /// <summary>The game's <c>SCUM\Content\Paks</c> (Settings → game folder), or null when not set.</summary>
    public string? GamePaks { get; init; }

    /// <summary>The dedicated server's <c>SCUM\Content\Paks</c> (Settings → server folder), or null when not set.</summary>
    public string? ServerPaks { get; init; }

    /// <summary>The export output folder (holding <c>&lt;Mod&gt;\Client</c> and <c>&lt;Mod&gt; - pages\Client</c>), or null.</summary>
    public string? ExportFolder { get; init; }

    /// <summary>
    /// Whether a pak carries <c>SCUM/AssetRegistry.bin</c>: true, false, or null when it could not be looked into. Null =
    /// <see cref="InstallCheck.HasRegistry"/> without a key.
    /// </summary>
    public Func<string, bool?>? HasRegistry { get; init; }

    /// <summary>
    /// The client paks the export just made (full paths; <c>ExportResult.PakPath</c>), or empty. The game's side then says
    /// which of them are not in its <c>~mods</c> with the same size (<see cref="InstallIssue.NotCopied"/>) and whether the
    /// other install mode's paks are there (<see cref="InstallIssue.OtherMode"/>), whatever is installed already.
    /// </summary>
    public IReadOnlyList<string> Written { get; init; } = [];

    /// <summary>
    /// Whether the project has new assets that need the game's asset list (item, weapon and vehicle clones, craftables):
    /// without any, another mod's list loading first hides nothing of this mod (<see cref="InstallIssue.RegistryAbove"/> is
    /// then a note). Null = not known (treated as yes).
    /// </summary>
    public bool? HasNewAssets { get; init; }
}

/// <summary>
/// "Check my install" (Discord 2026-10-18, hexter_98, youngpiegus; r39 diagnosis E2): looks into the game's and the
/// server's <c>SCUM\Content\Paks\~mods</c> and says what stands between the project's export and the game. The game reads
/// ONE <c>AssetRegistry.bin</c>, from the pak that comes first in its order (<see cref="LoadsBefore"/>): a missing Registry
/// pak, an older pak of the same mod or another mod's pak with its own registry above ours hides every new item, vehicle and
/// recipe; paks loose in <c>Paks</c> are refused; a server needs its own (server) pak with its <c>.sig</c>. <c>~mods</c> is read
/// with its subfolders: the engine finds paks by walking <c>Content\Paks</c> recursively and mounts those too.
/// </summary>
public static partial class InstallCheck
{
    /// <summary>Path of the asset registry inside a pak (as <see cref="PakFileSource"/> lists it).</summary>
    public const string RegistryPath = "SCUM/AssetRegistry.bin";

    /// <summary>Everything found on both sides, the game first; each side ends with nothing worse than what it says.</summary>
    public static IReadOnlyList<InstallFinding> Check(InstallCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var probe = input.HasRegistry ?? (path => HasRegistry(path, null));
        var cache = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        bool? Probe(string path) => cache.TryGetValue(path, out var known) ? known : cache[path] = probe(path);
        var findings = new List<InstallFinding>();
        findings.AddRange(CheckSide(input, InstallSide.Game, input.GamePaks, Probe));
        findings.AddRange(CheckSide(input, InstallSide.Server, input.ServerPaks, Probe));
        findings.Add(new InstallFinding(InstallSide.Server, InstallIssue.NoteFileOpenLog, InstallSeverity.Info, []));
        findings.Add(new InstallFinding(InstallSide.Game, InstallIssue.NoteFriends, InstallSeverity.Info, []));
        return findings;
    }

    /// <summary>
    /// True when the pak at <paramref name="pakPath"/> carries <c>SCUM/AssetRegistry.bin</c>, false when not, null when it
    /// could not be opened (encrypted with a key other than <paramref name="aesKey"/>, or not a pak).
    /// </summary>
    public static bool? HasRegistry(string pakPath, string? aesKey)
    {
        try
        {
            using var source = PakFileSource.OpenFile(pakPath, new PakFileSourceOptions { AesKey = string.IsNullOrWhiteSpace(aesKey) ? null : aesKey });
            return source.Archives.Count == 0 || !source.Archives[0].IsMounted ? null : source.Exists(RegistryPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The engine's read order of a pak in <c>SCUM\Content</c> (UE 4.27 <c>FPakPlatformFile::Mount</c>): 3, plus 100 for a
    /// name ending in <c>_P.pak</c>, or 100 × (n + 1) when the part between its last two <c>_</c> is a number n ≥ 1. A file in
    /// several paks is read from the one with the highest order; among equal orders, from the one mounted first.
    /// </summary>
    public static int ReadOrder(string pakFileName)
    {
        ArgumentNullException.ThrowIfNull(pakFileName);
        var name = Path.GetFileName(pakFileName);
        var order = 3;
        if (!name.EndsWith("_P.pak", StringComparison.OrdinalIgnoreCase))
        {
            return order;
        }

        var version = 1;
        var end = name.LastIndexOf('_');
        var start = end > 0 ? name.LastIndexOf('_', end - 1) : -1;
        if (start >= 0 && int.TryParse(name.AsSpan(start + 1, end - start - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1)
        {
            version = n + 1;
        }

        return order + (100 * version);
    }

    /// <summary>
    /// True when the game reads a file both paks hold from <paramref name="a"/>: its <see cref="ReadOrder"/> is higher, or
    /// equal and it mounts first. <paramref name="a"/> and <paramref name="b"/> are file names of one folder, or full paths
    /// under the same <c>Paks</c> folder: paks mount in descending order of their path, compared without case and with
    /// <c>/</c> between folders, as the engine sorts what it found (the owner's game logs: <c>pakchunk919-…-Registry_P.pak</c>
    /// before <c>pakchunk915-…</c> before <c>pakchunk912-…</c>; a subfolder of <c>~mods</c> sorts by its name like a file).
    /// </summary>
    public static bool LoadsBefore(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var (oa, ob) = (ReadOrder(a), ReadOrder(b));
        return oa != ob ? oa > ob : string.CompareOrdinal(MountKey(a), MountKey(b)) > 0;

        static string MountKey(string path) => path.Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>True for a pak of the game itself (<c>pakchunk12-WindowsNoEditor.pak</c>, <c>pakchunk0_s3-WindowsServer.pak</c> …).</summary>
    public static bool IsStockPak(string pakFileName) => StockPak().IsMatch(Path.GetFileName(pakFileName));

    /// <summary>The file names this project's exports give their paks (the whole project's, its Craftables pak, every page's and the Registry pak).</summary>
    public static IReadOnlyCollection<string> OwnPakNames(IEnumerable<string> modNames, int pakChunkIndex)
    {
        ArgumentNullException.ThrowIfNull(modNames);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in modNames.Where(m => !string.IsNullOrWhiteSpace(m)))
        {
            names.Add(ProjectExporter.PakFileName(mod, pakChunkIndex));
            names.Add(CraftablesExporter.PakFileName(mod, pakChunkIndex));
            names.Add(ModuleExport.RegistryPakFileName(mod, pakChunkIndex));
            foreach (var module in ModuleExport.PakModules)
            {
                names.Add(ModuleExport.PakFileName(mod, module, pakChunkIndex));
            }
        }

        return names;
    }

    private static IEnumerable<InstallFinding> CheckSide(InstallCheckInput input, InstallSide side, string? paks, Func<string, bool?> probe)
    {
        if (string.IsNullOrWhiteSpace(paks) || !Directory.Exists(paks))
        {
            yield return new InstallFinding(side, InstallIssue.NoFolder, InstallSeverity.Info, [], string.IsNullOrWhiteSpace(paks) ? null : paks);
            yield break;
        }

        var paksFolder = Path.GetFullPath(paks);
        var mods = Path.Combine(paksFolder, Core.Games.GameInstall.ModsFolderName);
        var role = side == InstallSide.Server ? Projects.ProjectSourceRole.Server : Projects.ProjectSourceRole.Client;
        var other = side == InstallSide.Server ? Projects.ProjectSourceRole.Client : Projects.ProjectSourceRole.Server;
        var own = OwnPakNames(input.ModNames, input.PakChunkIndex);

        // Mod paks directly in Paks: the game refuses them there.
        var loose = Paks(paksFolder).Where(p => !IsStockPak(p)).Select(Path.GetFileName).OfType<string>().ToList();
        if (loose.Count > 0)
        {
            yield return new InstallFinding(side, InstallIssue.LooseInPaks, InstallSeverity.Problem, loose, paksFolder);
        }

        // Every pak in ~mods and its subfolders (the engine mounts them all); a pak in a subfolder is shown with its folder.
        var installed = Paks(mods, recursive: true);
        string Shown(string pak) => Path.GetRelativePath(mods, pak);
        bool Nested(string pak) => Shown(pak).IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0;
        static bool SameName(string a, string b) => string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
        var ours = installed.Where(p => own.Contains(Path.GetFileName(p))).ToList();
        var oldNames = installed.Where(p => !own.Contains(Path.GetFileName(p)) && IsOwnModName(Path.GetFileName(p), input.ModNames)).ToList();
        var others = installed.Except(ours).Except(oldNames).ToList();

        // The export folders of this role and of the other one, whole project and pages, for every mod name.
        var exports = ExportFolders(input, role);
        var otherExports = ExportFolders(input, other);
        string? ExportOf(string pak, IEnumerable<string> folders) => folders.Select(f => Path.Combine(f, Path.GetFileName(pak))).FirstOrDefault(File.Exists);

        // The paks the export just wrote (the game's side): those the game does not have, by name and size.
        List<string> written = side == InstallSide.Game
            ? input.Written.Where(w => !string.IsNullOrWhiteSpace(w) && File.Exists(w)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        var notCopied = written.Where(w => !installed.Any(i => SameName(i, w) && new FileInfo(i).Length == new FileInfo(w).Length)).ToList();

        if (ours.Count == 0)
        {
            // Nothing of this project: the folder to copy is the one the export just wrote, else the export with the newest pak.
            var newest = written.Count > 0
                ? (Folder: Path.GetDirectoryName(written[0]), Paks: written)
                : exports.Select(f => (Folder: (string?)f, Paks: Paks(f))).Where(f => f.Paks.Count > 0)
                    .OrderByDescending(f => f.Paks.Max(File.GetLastWriteTimeUtc)).FirstOrDefault();
            yield return new InstallFinding(side, InstallIssue.NotInstalled, InstallSeverity.Warning,
                newest.Paks?.Select(Path.GetFileName).OfType<string>().ToList() ?? [], newest.Folder);
        }
        else
        {
            if (notCopied.Count > 0)
            {
                yield return new InstallFinding(side, InstallIssue.NotCopied, InstallSeverity.Problem,
                    notCopied.Select(Path.GetFileName).OfType<string>().ToList(), Path.GetDirectoryName(notCopied[0]));
            }

            var wholeNames = input.ModNames.SelectMany(m => new[] { ProjectExporter.PakFileName(m, input.PakChunkIndex), CraftablesExporter.PakFileName(m, input.PakChunkIndex) })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var whole = ours.Where(p => wholeNames.Contains(Path.GetFileName(p))).ToList();
            var pages = ours.Except(whole).ToList();
            if (whole.Count > 0 && pages.Count > 0)
            {
                yield return new InstallFinding(side, InstallIssue.BothModes, InstallSeverity.Problem, ours.Select(Shown).ToList(), mods);
            }
            else if (written.Count > 0 && (written.Any(w => wholeNames.Contains(Path.GetFileName(w))) ? pages : whole) is { Count: > 0 } otherMode)
            {
                // Only the other install mode is in ~mods: copying this export there would install both.
                yield return new InstallFinding(side, InstallIssue.OtherMode, InstallSeverity.Warning, otherMode.Select(Shown).ToList(), mods);
            }

            var registryNames = input.ModNames.Select(m => ModuleExport.RegistryPakFileName(m, input.PakChunkIndex)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pagePaks = pages.Where(p => !registryNames.Contains(Path.GetFileName(p))).ToList();
            var registryMissing = pagePaks.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2
                                  && !pages.Any(p => registryNames.Contains(Path.GetFileName(p)));
            if (registryMissing)
            {
                yield return new InstallFinding(side, InstallIssue.MissingRegistry, InstallSeverity.Problem, pagePaks.Select(Shown).ToList(), mods);
            }

            // Against the last export: what is older or other than it, and what of it is not installed.
            var stale = new List<string>();
            var current = new List<string>();
            var wrongCook = new List<string>();
            foreach (var pak in ours)
            {
                var exported = ExportOf(pak, exports);
                var otherCook = ExportOf(pak, otherExports);
                var size = new FileInfo(pak).Length;
                if (otherCook is not null && new FileInfo(otherCook).Length == size && (exported is null || new FileInfo(exported).Length != size))
                {
                    wrongCook.Add(Shown(pak));
                }
                else if (exported is not null && (new FileInfo(exported).Length != size || File.GetLastWriteTimeUtc(exported) > File.GetLastWriteTimeUtc(pak).AddSeconds(2)))
                {
                    stale.Add(Shown(pak));
                }
                else
                {
                    current.Add(Shown(pak));
                }
            }

            if (wrongCook.Count > 0)
            {
                yield return new InstallFinding(side, InstallIssue.WrongCook, InstallSeverity.Problem, wrongCook, mods);
            }

            if (stale.Count > 0)
            {
                yield return new InstallFinding(side, InstallIssue.Stale, InstallSeverity.Problem, stale, mods);
            }

            foreach (var mode in new[] { (Installed: whole, Pages: false), (Installed: pages, Pages: true) }.Where(m => m.Installed.Count > 0))
            {
                var folder = exports.Where(f => f.Contains(ProjectExporter.PagesFolderSuffix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == mode.Pages)
                    .FirstOrDefault(f => Paks(f).Any(p => mode.Installed.Any(i => SameName(i, p))));
                // The Registry pak is named once, by MissingRegistry, when that is what is missing.
                var missing = folder is null ? [] : Paks(folder).Select(Path.GetFileName).OfType<string>()
                    .Where(name => !ours.Any(o => string.Equals(Path.GetFileName(o), name, StringComparison.OrdinalIgnoreCase)))
                    .Where(name => !(registryMissing && registryNames.Contains(name))).ToList();
                if (missing.Count > 0)
                {
                    yield return new InstallFinding(side, InstallIssue.Missing, InstallSeverity.Problem, missing, folder);
                }
            }

            if (side == InstallSide.Server && ours.Where(p => !File.Exists(Path.ChangeExtension(p, ".sig"))).Select(Shown).ToList() is { Count: > 0 } unsigned)
            {
                yield return new InstallFinding(side, InstallIssue.NoSig, InstallSeverity.Problem, unsigned, mods);
            }

            if (current.Count > 0)
            {
                yield return new InstallFinding(side, InstallIssue.Installed, InstallSeverity.Ok, current, mods);
            }
        }

        if (ours.Concat(oldNames).Where(Nested).Select(Shown).ToList() is { Count: > 0 } nested)
        {
            yield return new InstallFinding(side, InstallIssue.InSubfolder, InstallSeverity.Warning, nested, mods);
        }

        if (oldNames.Count > 0)
        {
            yield return new InstallFinding(side, InstallIssue.OldName, InstallSeverity.Problem, oldNames.Select(Shown).ToList(), mods);
        }

        // Other mods' paks with an asset registry of their own: the one that loads first is the only registry the game reads.
        // Full paths: paks in subfolders of ~mods mount in the order of their whole path.
        var ourRegistry = ours.Where(p => probe(p) == true).OrderBy(p => p, Comparer<string>.Create((a, b) => LoadsBefore(a, b) ? -1 : LoadsBefore(b, a) ? 1 : 0)).FirstOrDefault();
        if (ourRegistry is null)
        {
            yield break; // nothing of ours carries a registry: no new asset of ours to hide
        }

        var above = new List<string>();
        var below = new List<string>();
        var unreadable = new List<string>();
        foreach (var pak in others.Concat(oldNames))
        {
            switch (probe(pak))
            {
                case true when LoadsBefore(pak, ourRegistry):
                    above.Add(Shown(pak));
                    break;
                case true:
                    below.Add(Shown(pak));
                    break;
                case null when LoadsBefore(pak, ourRegistry):
                    unreadable.Add(Shown(pak));
                    break;
            }
        }

        // A project without new items, vehicles or recipes has nothing another list could hide (its map changes do not use it).
        var hidesOurs = input.HasNewAssets != false;
        if (above.Count > 0)
        {
            yield return new InstallFinding(side, InstallIssue.RegistryAbove, hidesOurs ? InstallSeverity.Problem : InstallSeverity.Info, above, mods);
        }

        if (unreadable.Count > 0 && hidesOurs)
        {
            yield return new InstallFinding(side, InstallIssue.Unreadable, InstallSeverity.Warning, unreadable, mods);
        }

        if (below.Count > 0)
        {
            yield return new InstallFinding(side, InstallIssue.RegistryBelow, InstallSeverity.Warning, below, mods);
        }
    }

    /// <summary>
    /// True for a pak of the mod under another chunk number: its name part (<c>pakchunk&lt;n&gt;-&lt;name&gt;_P.pak</c>) is one
    /// this project's exports use (the mod, its Craftables, a page or the Registry pak) while the file name is not.
    /// </summary>
    private static bool IsOwnModName(string fileName, IEnumerable<string> modNames)
    {
        var match = ModPak().Match(fileName);
        if (!match.Success)
        {
            return false;
        }

        var name = match.Groups["name"].Value;
        return modNames.Where(m => !string.IsNullOrWhiteSpace(m)).Select(ProjectExporter.SanitizeModName).Any(m =>
            string.Equals(name, m, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, m + CraftablesExporter.Suffix, StringComparison.OrdinalIgnoreCase)
            || ModuleExport.PakModules.Select(p => p.ToString()).Append(ModuleExport.RegistryName).Any(part => string.Equals(name, m + "-" + part, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> ExportFolders(InstallCheckInput input, Projects.ProjectSourceRole role) =>
        string.IsNullOrWhiteSpace(input.ExportFolder)
            ? []
            : input.ModNames.Where(m => !string.IsNullOrWhiteSpace(m))
                .SelectMany(m => new[] { ProjectExporter.ExportFolder(input.ExportFolder, m, pages: false, role), ProjectExporter.ExportFolder(input.ExportFolder, m, pages: true, role) })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // The .pak files of a folder (with its subfolders when recursive; one that cannot be read is passed over), by path.
    private static List<string> Paks(string folder, bool recursive = false)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.pak", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = 0 })
                    .Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^pakchunk\d+(?:_s\d+)?(?:optional)?-Windows(?:NoEditor|Server)\.pak$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StockPak();

    [GeneratedRegex(@"^pakchunk(?<chunk>\d+)-(?<name>.+)_P\.pak$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModPak();
}
