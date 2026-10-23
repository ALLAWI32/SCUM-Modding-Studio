using ScumStudio.Level.Projects;

namespace ScumStudio.Level.Export;

/// <summary>
/// Where an export's files go (owner, 3.5.0: "it gives me many kinds, staging folders, two exports mixed; it should put it
/// in a folder for me, one mod, just the pak"): one folder per mod under the output folder, holding only what is copied into
/// the game, with the staged loose files and the exporter's own notes in a hidden work folder beside them.
/// <list type="bullet">
/// <item><c>&lt;output&gt;/&lt;Mod&gt;/Client</c> and <c>/Server</c>: the whole project's pak (and its Craftables pak), the
/// <c>.sig</c> files, <c>export-report.md</c>, and <c>EconomyOverride.json</c> when the project has one.</item>
/// <item><c>&lt;output&gt;/&lt;Mod&gt; - pages/Client</c> and <c>/Server</c>: the page paks (one page as its own mod) and, with two
/// or more of them, the Registry pak. Everything in that folder is installed together.</item>
/// <item><c>.work</c> inside each of them (hidden): <c>staging*</c> folders, <c>item-spawners.txt</c>.</item>
/// </list>
/// </summary>
public sealed partial class ProjectExporter
{
    /// <summary>Appended to the mod's folder name for the page paks' folder (<c>MyMod - pages</c>).</summary>
    public const string PagesFolderSuffix = " - pages";

    /// <summary>Hidden folder inside an export folder holding the staged loose files and the exporter's own notes.</summary>
    public const string WorkFolderName = ".work";

    /// <summary>The folder of <paramref name="modName"/>'s exports under <paramref name="outputDirectory"/>: its name, plus <see cref="PagesFolderSuffix"/> for the page paks.</summary>
    public static string ModFolder(string outputDirectory, string modName, bool pages)
    {
        ArgumentNullException.ThrowIfNull(outputDirectory);
        return Path.Combine(Path.GetFullPath(outputDirectory), SanitizeModName(modName) + (pages ? PagesFolderSuffix : string.Empty));
    }

    /// <summary>Where one cook's files go: <c>&lt;output&gt;/&lt;Mod&gt;/Client</c> or <c>/Server</c> (<c>&lt;Mod&gt; - pages</c> for a page's pak).</summary>
    public static string ExportFolder(string outputDirectory, string modName, bool pages, ProjectSourceRole role) =>
        Path.Combine(ModFolder(outputDirectory, modName, pages), RoleFolder(role));

    /// <summary>
    /// The hidden work folder of <paramref name="exportFolder"/>, created (and hidden) when missing. Hiding it is cosmetic: on a
    /// share that lets the user write files but not change attributes (review 3.5.1) it stays visible and the export goes on.
    /// </summary>
    public static string WorkFolder(string exportFolder)
    {
        ArgumentNullException.ThrowIfNull(exportFolder);
        var work = Directory.CreateDirectory(Path.Combine(exportFolder, WorkFolderName));
        try
        {
            if (!work.Attributes.HasFlag(FileAttributes.Hidden))
            {
                work.Attributes |= FileAttributes.Hidden;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // shown, not hidden: everything in it still works
        }

        return work.FullName;
    }

    /// <summary>
    /// The exporter's notes of an export before 3.5.1 (<see cref="ItemSpawnersFileName"/> loose in <c>&lt;output&gt;/Client</c> or
    /// <c>/Server</c>) copied into <paramref name="workFolder"/>, so the next export still knows the item spawners the last one placed.
    /// Copied, not moved: a refused export leaves the old folder as it was (<see cref="RemoveOldLayout"/> takes the old file
    /// once an export of the mod succeeds).
    /// </summary>
    /// <returns>True when they were copied (<see cref="DropOldNotes"/> removes the old file once the export's pak is written).</returns>
    internal static bool CarryOldNotes(string outputDirectory, ProjectSourceRole role, string workFolder)
    {
        var old = Path.Combine(Path.GetFullPath(outputDirectory), RoleFolder(role), ItemSpawnersFileName);
        var target = Path.Combine(workFolder, ItemSpawnersFileName);
        if (File.Exists(old) && !File.Exists(target))
        {
            File.Copy(old, target);
            return true;
        }

        return false;
    }

    /// <summary>The old notes <see cref="CarryOldNotes"/> copied, deleted once the export that took them wrote its pak (best effort).</summary>
    internal static void DropOldNotes(string outputDirectory, ProjectSourceRole role)
    {
        try
        {
            File.Delete(Path.Combine(Path.GetFullPath(outputDirectory), RoleFolder(role), ItemSpawnersFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // they stay; the next export of this mod reads its own copy in the work folder
        }
    }

    /// <summary>
    /// Removes what an export of <paramref name="modName"/> before 3.5.1 left loose in <c>&lt;output&gt;/Client</c> or <c>/Server</c>:
    /// its paks and signatures, every staging folder, and the reports and check lists once no other mod's pak is left there.
    /// Another mod's paks stay. Returns the removed files and folders. Called at the end of a successful export that wrote its
    /// pak only. Best effort: a file it cannot delete (a pak the running game or server holds open, review 3.5.1) stays, and the
    /// export that just succeeded is not turned into a failure. With <paramref name="keepCraftables"/> the old Craftables pak
    /// stays for the Craftables pak of the same export to replace (a Craftables pak refused after the project's pak would
    /// otherwise leave no Craftables pak anywhere).
    /// </summary>
    public static IReadOnlyList<string> RemoveOldLayout(string outputDirectory, string modName, int pakChunkIndex, ProjectSourceRole role, bool keepCraftables = false)
    {
        ArgumentNullException.ThrowIfNull(outputDirectory);
        var old = Path.Combine(Path.GetFullPath(outputDirectory), RoleFolder(role));
        var removed = new List<string>();
        void Remove(string path, bool folder)
        {
            try
            {
                if (folder)
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }

                removed.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // held open or not ours to delete: it stays, the next export tries again
            }
        }

        try
        {
            if (!Directory.Exists(old))
            {
                return [];
            }

            foreach (var folder in Directory.EnumerateDirectories(old).ToList())
            {
                var name = Path.GetFileName(folder);
                if (name.Equals(StagingFolderName, StringComparison.OrdinalIgnoreCase) || name.StartsWith("staging-", StringComparison.OrdinalIgnoreCase))
                {
                    Remove(folder, folder: true);
                }
            }

            IEnumerable<string> paks =
            [
                PakFileName(modName, pakChunkIndex),
                .. (keepCraftables ? Array.Empty<string>() : new[] { CraftablesExporter.PakFileName(modName, pakChunkIndex) }),
                .. ModuleExport.PakModules.Select(m => ModuleExport.PakFileName(modName, m, pakChunkIndex)),
                ModuleExport.RegistryPakFileName(modName, pakChunkIndex),
            ];
            foreach (var pak in paks.Select(p => Path.Combine(old, p)))
            {
                foreach (var file in new[] { pak, Pak.Writing.SigCopier.GetSigPath(pak) }.Where(File.Exists))
                {
                    Remove(file, folder: false);
                }
            }

            // The notes belong to whichever mod was exported there last: they go only when no other mod's pak is left.
            if (!Directory.EnumerateFiles(old, "*.pak").Any())
            {
                foreach (var file in Directory.EnumerateFiles(old).Where(f => Path.GetFileName(f) is var n
                             && (n.Equals(ReportFileName, StringComparison.OrdinalIgnoreCase) || n.StartsWith("export-report-", StringComparison.OrdinalIgnoreCase)
                                 || n.Equals(ModPackageValidator.ListFileName, StringComparison.OrdinalIgnoreCase) || n.Equals(ItemSpawnersFileName, StringComparison.OrdinalIgnoreCase))).ToList())
                {
                    Remove(file, folder: false);
                }

                if (!Directory.EnumerateFileSystemEntries(old).Any())
                {
                    Remove(old, folder: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the old folder cannot be listed: nothing more to tidy now
        }

        return removed;
    }
}
