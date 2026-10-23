using System.Globalization;
using System.Text.Json;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.IO;
using ScumStudio.Level.Serialization;

namespace ScumStudio.Level.Projects;

/// <summary>A saved copy of a project inside its <c>snapshots</c> folder (see <see cref="ProjectCopies"/>).</summary>
/// <param name="Path">The snapshot's folder (a project folder of its own: <c>project.json</c>, <c>journal.jsonl</c> …).</param>
/// <param name="Name">The name given when it was saved (empty when none was given).</param>
/// <param name="Taken">When it was saved (local time, from the folder name).</param>
/// <param name="Edits">Edits in its journal (undone ones included, as on the project cards).</param>
/// <param name="Bytes">Size of every file in it.</param>
public sealed record ProjectSnapshot(string Path, string Name, DateTime Taken, int Edits, long Bytes);

/// <summary>
/// Copies of a project as plain folders (Discord, salvador.garcia: "a simple button to clone the current project, which
/// would allow us to manage versions"; pheidian redid his work in an old project): <see cref="DuplicateAsync"/> makes a new
/// project from a project or a snapshot, <see cref="SaveSnapshotAsync"/> keeps a named copy in
/// <c>&lt;project&gt;/snapshots/&lt;time&gt; &lt;name&gt;</c>, and <see cref="RestoreAsync"/> puts a snapshot back in place of
/// the project's files. Everything of the project is copied (journal, notes, pictures, imported models and mods, the
/// economy and craftables files), never its lock, its snapshots or a mod import that did not finish. Files keep their
/// times: the imported mods are read oldest first (<see cref="ProjectMods.Folders"/>) and a picture's time tells whether a
/// decal is applied.
/// </summary>
public static class ProjectCopies
{
    /// <summary>Folder of the snapshots inside a project.</summary>
    public const string SnapshotsFolderName = "snapshots";

    /// <summary>Format of the time at the start of a snapshot's folder name (no colons: Windows forbids them).</summary>
    public const string TimeFormat = "yyyy-MM-dd HH-mm-ss";

    private const string IncomingFolderName = ".restore-new";
    private const string OutgoingFolderName = ".restore-old";

    // In .restore-old while a swap runs and after one that could not be put back (never a name of a project entry).
    private const string UnfinishedMarkerName = ".restore-unfinished";

    // Tries of a rename in a restore (100, 200 … 500 ms between them).
    private const int MoveAttempts = 6;

    /// <summary>
    /// The project folder for <paramref name="name"/> in <paramref name="parentFolder"/> (<c>&lt;name&gt;.ssproj</c>, characters
    /// a file name cannot hold replaced), as <c>Projects → New project</c> makes it.
    /// </summary>
    public static string FolderFor(string parentFolder, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return System.IO.Path.Combine(System.IO.Path.GetFullPath(parentFolder), SafeName(name.Trim()) + Project.FolderExtension);
    }

    /// <summary>
    /// A name for a copy that no project in <paramref name="parentFolder"/> has: <paramref name="baseName"/>, else
    /// "<paramref name="baseName"/> 2", "… 3" and so on.
    /// </summary>
    public static string FreeName(string parentFolder, string baseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        var name = baseName.Trim();
        for (var n = 2; Directory.Exists(FolderFor(parentFolder, name)); n++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{baseName.Trim()} {n}");
        }

        return name;
    }

    /// <summary>
    /// Copies the project (or snapshot) in <paramref name="sourceDirectory"/> to a new project named <paramref name="newName"/>
    /// in <paramref name="parentFolder"/> and returns its folder. The copy is a project of its own: its <c>project.json</c>
    /// carries the new name (its mod pak is named after it) and today's date; its snapshots are not copied. A copy that fails
    /// or is cancelled is removed again.
    /// </summary>
    /// <exception cref="FileNotFoundException">The source holds no project.</exception>
    /// <exception cref="IOException">A project of that name is already there.</exception>
    public static async Task<string> DuplicateAsync(string sourceDirectory, string parentFolder, string newName, IProgressSink? progress = null,
        TimeProvider? clock = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var source = System.IO.Path.GetFullPath(sourceDirectory);
        if (!Project.Exists(source))
        {
            throw new FileNotFoundException($"No {Project.ManifestFileName} in {source}.", System.IO.Path.Combine(source, Project.ManifestFileName));
        }

        var target = FolderFor(parentFolder, newName);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new IOException($"{target} already exists: pick another name for the copy.");
        }

        try
        {
            await CopyTreeAsync(source, target, progress, cancellationToken).ConfigureAwait(false);
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var manifest = ReadManifest(target) with { Name = newName.Trim(), Created = now, Modified = now };
            AtomicFile.WriteAllBytes(System.IO.Path.Combine(target, Project.ManifestFileName), Project.SerializeManifest(manifest));
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// Saves a copy of the project in <paramref name="projectDirectory"/> as
    /// <c>snapshots/&lt;time&gt; &lt;name&gt;</c> (<paramref name="name"/> may be empty) and returns it. Works while the project is
    /// open (its journal is read as it is on disk). A copy that fails or is cancelled is removed again.
    /// </summary>
    /// <exception cref="FileNotFoundException">The folder holds no project.</exception>
    public static async Task<ProjectSnapshot> SaveSnapshotAsync(string projectDirectory, string? name, IProgressSink? progress = null,
        TimeProvider? clock = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var project = System.IO.Path.GetFullPath(projectDirectory);
        if (!Project.Exists(project))
        {
            throw new FileNotFoundException($"No {Project.ManifestFileName} in {project}.", System.IO.Path.Combine(project, Project.ManifestFileName));
        }

        var taken = (clock ?? TimeProvider.System).GetLocalNow().DateTime;
        var label = SafeName(string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        if (label.Length > 60)
        {
            label = label[..60].TrimEnd();
        }

        var folderName = taken.ToString(TimeFormat, CultureInfo.InvariantCulture) + (label.Length > 0 ? " " + label : string.Empty);
        var root = System.IO.Path.Combine(project, SnapshotsFolderName);
        var target = System.IO.Path.Combine(root, folderName);
        for (var n = 2; Directory.Exists(target); n++)
        {
            target = System.IO.Path.Combine(root, string.Create(CultureInfo.InvariantCulture, $"{folderName} ({n})"));
        }

        try
        {
            await CopyTreeAsync(project, target, progress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        return Read(target) ?? throw new IOException($"The snapshot {target} could not be read back.");
    }

    /// <summary>The snapshots of the project in <paramref name="projectDirectory"/>, newest first; empty without any.</summary>
    public static IReadOnlyList<ProjectSnapshot> Snapshots(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var root = System.IO.Path.Combine(System.IO.Path.GetFullPath(projectDirectory), SnapshotsFolderName);
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root).Select(Read).OfType<ProjectSnapshot>()
                    .OrderByDescending(s => s.Taken).ThenByDescending(s => Directory.GetCreationTimeUtc(s.Path)) // two in one second: the later first
                    .ThenByDescending(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // an unreadable folder lists nothing
        }
    }

    /// <summary>The snapshot in <paramref name="folder"/>, or null when it holds no project.</summary>
    public static ProjectSnapshot? Read(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var full = System.IO.Path.GetFullPath(folder);
        if (!Project.Exists(full))
        {
            return null;
        }

        var leaf = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(full));
        var (taken, name) = leaf.Length >= TimeFormat.Length
                            && DateTime.TryParseExact(leaf[..TimeFormat.Length], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time)
            ? (time, leaf[TimeFormat.Length..].Trim())
            : (Directory.GetCreationTime(full), leaf);
        long bytes = 0;
        try
        {
            bytes = new DirectoryInfo(full).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the size stays unknown (0)
        }

        return new ProjectSnapshot(full, name, taken, CountEdits(System.IO.Path.Combine(full, Project.JournalFileName)), bytes);
    }

    /// <summary>
    /// Puts the snapshot <paramref name="snapshotDirectory"/> back as the project in <paramref name="projectDirectory"/>: every
    /// file of the project (but its snapshots) is replaced by the snapshot's. The project must be closed (its lock is taken
    /// for the time of the swap). Save a snapshot of the current state first: what is replaced is gone. The snapshot is first
    /// copied next to the project's files (<c>.restore-new</c>), so a copy that fails leaves the project as it was; then the
    /// project's entries are moved aside (<c>.restore-old</c>) and the snapshot's moved in, each rename tried again for a
    /// moment while another program (an antivirus, the indexer) holds a file. When a rename still fails, what came in goes
    /// back out and the project's own entries go back, and the error is thrown: the project is as it was. If even that is
    /// not possible, <c>.restore-old</c> keeps the project's entries, marked as unfinished: the error names it, and every
    /// later restore refuses to start until its files are moved back.
    /// </summary>
    /// <exception cref="FileNotFoundException">The project or the snapshot holds no project.</exception>
    /// <exception cref="ProjectLockedException">The project is open (in this window or another).</exception>
    /// <exception cref="IOException">A rename failed (the project is as it was), or an unfinished restore's files are waiting.</exception>
    public static async Task RestoreAsync(string projectDirectory, string snapshotDirectory, IProgressSink? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        var project = System.IO.Path.GetFullPath(projectDirectory);
        var snapshot = System.IO.Path.GetFullPath(snapshotDirectory);
        if (!Project.Exists(project))
        {
            throw new FileNotFoundException($"No {Project.ManifestFileName} in {project}.", System.IO.Path.Combine(project, Project.ManifestFileName));
        }

        if (!Project.Exists(snapshot))
        {
            throw new FileNotFoundException($"No {Project.ManifestFileName} in {snapshot}.", System.IO.Path.Combine(snapshot, Project.ManifestFileName));
        }

        using var held = ProjectLock.Acquire(project);
        var incoming = System.IO.Path.Combine(project, IncomingFolderName);
        var outgoing = System.IO.Path.Combine(project, OutgoingFolderName);
        var unfinished = System.IO.Path.Combine(outgoing, UnfinishedMarkerName);

        // A restore that could not put the project back left its entries in .restore-old: never delete them.
        if (File.Exists(unfinished) && Directory.EnumerateFileSystemEntries(outgoing).Any(e => !string.Equals(e, unfinished, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException($"An earlier restore did not finish: the project's own files are in {outgoing}. Move every file and folder "
                                  + $"from there back into {project} (replace what is there), delete {outgoing}, then try again.");
        }

        TryDelete(outgoing); // a finished restore's replaced entries that could not be deleted then
        TryDelete(incoming); // only ever a copy of a snapshot
        if (new[] { incoming, outgoing }.FirstOrDefault(Directory.Exists) is { } leftover)
        {
            throw new IOException($"{leftover} (left by an earlier restore) cannot be deleted: a program holds a file in it. Close that program, or restart the PC, and try again.");
        }

        try
        {
            await CopyTreeAsync(snapshot, incoming, progress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(incoming);
            throw;
        }

        // From here on nothing is cancelled: the swap is a few renames in the same folder. The marker says it is running until
        // the project is complete again (restored, or put back).
        var movedOut = new List<string>();
        var movedIn = new List<string>();
        try
        {
            Directory.CreateDirectory(outgoing);
            await File.WriteAllTextAsync(unfinished, $"Restoring {snapshot} into {project}. If this file is still here, move every other entry of this folder back into the project.",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(outgoing);
            TryDelete(incoming);
            throw;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(project).Where(e => !Kept(System.IO.Path.GetFileName(e))).ToList())
            {
                await MoveAsync(entry, System.IO.Path.Combine(outgoing, System.IO.Path.GetFileName(entry))).ConfigureAwait(false);
                movedOut.Add(System.IO.Path.GetFileName(entry));
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(incoming).ToList())
            {
                await MoveAsync(entry, System.IO.Path.Combine(project, System.IO.Path.GetFileName(entry))).ConfigureAwait(false);
                movedIn.Add(System.IO.Path.GetFileName(entry));
            }
        }
        catch (Exception error)
        {
            // Put the project back: what came in from the snapshot goes back out first (or is deleted; it is a copy), then the
            // project's own entries come back. Whatever cannot be put back is named, and the marker stays.
            var stuck = new List<string>();
            foreach (var name in Enumerable.Reverse(movedIn))
            {
                var inProject = System.IO.Path.Combine(project, name);
                if (!await TryMoveAsync(inProject, System.IO.Path.Combine(incoming, name)).ConfigureAwait(false) && !await TryDeleteAsync(inProject).ConfigureAwait(false))
                {
                    stuck.Add(inProject);
                }
            }

            foreach (var name in Enumerable.Reverse(movedOut))
            {
                if (!await TryMoveAsync(System.IO.Path.Combine(outgoing, name), System.IO.Path.Combine(project, name)).ConfigureAwait(false))
                {
                    stuck.Add(System.IO.Path.Combine(outgoing, name));
                }
            }

            if (stuck.Count > 0)
            {
                throw new IOException($"Restoring {snapshot} failed ({error.Message}) and the project could not be put back as it was. Its own files are in "
                                      + $"{outgoing}: move every file and folder from there back into {project} (replace what is there), then delete "
                                      + $"{outgoing}. Could not be moved: {string.Join(", ", stuck)}.", error);
            }

            await TryDeleteAsync(unfinished).ConfigureAwait(false); // first: the folder may keep a file a program holds
            TryDelete(outgoing);
            TryDelete(incoming);
            throw;
        }

        await TryDeleteAsync(unfinished).ConfigureAwait(false);
        TryDelete(incoming);
        TryDelete(outgoing);

        static bool Kept(string name) =>
            name.Equals(SnapshotsFolderName, StringComparison.OrdinalIgnoreCase) || name.Equals(ProjectLock.FileName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(IncomingFolderName, StringComparison.OrdinalIgnoreCase) || name.Equals(OutgoingFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Renames a file or folder within a folder, tried again a few times (about 1.5 s in all) while another program holds a
    /// file in it; the last failure is thrown.
    /// </summary>
    internal static async Task MoveAsync(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(from))
                {
                    Directory.Move(from, to);
                }
                else
                {
                    File.Move(from, to);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not (FileNotFoundException or DirectoryNotFoundException) && attempt < MoveAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt)).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> TryMoveAsync(string from, string to)
    {
        try
        {
            await MoveAsync(from, to).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Deletes a file or folder, tried again like MoveAsync; true when it is gone.
    private static async Task<bool> TryDeleteAsync(string path)
    {
        for (var attempt = 1; attempt <= MoveAttempts; attempt++)
        {
            TryDelete(path);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt)).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Copies every file and folder of the project <paramref name="from"/> into <paramref name="to"/> (created), keeping the
    /// files' and folders' times, but never the lock, the snapshots, a restore's work folders or a mod import that did not
    /// finish. Files are read with every share mode, so the journal of a project open in this window copies too.
    /// </summary>
    internal static async Task CopyTreeAsync(string from, string to, IProgressSink? progress, CancellationToken cancellationToken)
    {
        var files = new List<(FileInfo File, string Target)>();
        var folders = new List<(DirectoryInfo Folder, string Target)>();
        void Walk(DirectoryInfo folder, string target, bool top)
        {
            folders.Add((folder, target));
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                var name = entry.Name;
                if (top && (name.Equals(ProjectLock.FileName, StringComparison.OrdinalIgnoreCase) || name.Equals(SnapshotsFolderName, StringComparison.OrdinalIgnoreCase)
                            || name.Equals(IncomingFolderName, StringComparison.OrdinalIgnoreCase) || name.Equals(OutgoingFolderName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                switch (entry)
                {
                    case DirectoryInfo sub when !name.EndsWith(ProjectMods.ImportingSuffix, StringComparison.OrdinalIgnoreCase):
                        Walk(sub, System.IO.Path.Combine(target, name), top: false);
                        break;
                    case FileInfo file:
                        files.Add((file, System.IO.Path.Combine(target, name)));
                        break;
                }
            }
        }

        Walk(new DirectoryInfo(from), to, top: true);
        var total = files.Sum(f => f.File.Length);
        long done = 0;
        foreach (var (_, target) in folders)
        {
            Directory.CreateDirectory(target);
        }

        var buffer = new byte[1 << 20];
        foreach (var (file, target) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Copying " + file.Name, done, total);
            await using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                }
            }

            File.SetCreationTimeUtc(target, file.CreationTimeUtc);
            File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
            if (file.Attributes.HasFlag(FileAttributes.Hidden))
            {
                File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.Hidden);
            }
        }

        // Folders last (writing their files changed their times), deepest first; the copy's own folder is new (a snapshot's
        // creation time is when it was taken).
        foreach (var (folder, target) in Enumerable.Reverse(folders).SkipLast(1))
        {
            Directory.SetCreationTimeUtc(target, folder.CreationTimeUtc);
            Directory.SetLastWriteTimeUtc(target, folder.LastWriteTimeUtc);
        }

        progress?.Report("Copying", total, total);
    }

    /// <summary>Edits in a journal file (undone ones included), read as it is on disk; 0 when it cannot be read.</summary>
    public static int CountEdits(string journalPath)
    {
        try
        {
            // An open project keeps its journal open for appending: share write access to read it.
            using var reader = new StreamReader(new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var count = 0;
            while (reader.ReadLine() is { } line)
            {
                count += line.Contains("\"type\":\"edit\"", StringComparison.Ordinal) ? 1 : 0;
            }

            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static ProjectManifest ReadManifest(string directory)
    {
        var path = System.IO.Path.Combine(directory, Project.ManifestFileName);
        try
        {
            return JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(path), LevelJson.Indented)
                   ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not valid: {ex.Message}", ex);
        }
    }

    private static string SafeName(string name) => string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: a leftover work folder is never read (Kept / CopyTreeAsync skip it)
        }
    }
}
