using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using static ScumStudio.Tests.Level.EditOpTests;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Discord 2026-10-19/20: salvador.garcia asked for "a simple button to clone the current project" to manage versions, and
/// pheidian redid work in an old project. A copy is a new project of its own; a snapshot is a copy inside the project's
/// snapshots folder that opens as a new project or comes back in place of the project's files. Plain folders, all files.
/// </summary>
public sealed class ProjectCopiesTests
{
    private static readonly ActorRef House = new(Outpost, "House_1");
    private static readonly ActorRef Barrel = new(Port, "Barrel_3");

    [Fact]
    public async Task ADuplicateCopiesEveryFileOfTheOpenProjectButItsLockAndSnapshotsUnderTheNewName()
    {
        using var temp = new LevelTempDirectory();
        var clock = ManualClock.At2026();
        var source = temp.Combine("projects", "Outpost.ssproj");
        using var project = Project.Create(source, "Outpost", clock: clock);
        project.Apply(new DeleteActorOp(House));
        project.Apply(new DeleteActorOp(Barrel));
        project.Undo();
        project.Notes = "# Outpost\n\nbridge next\n";
        project.Save();

        // What belongs to the project: pictures, imported models, imported mods (read oldest first), the economy file.
        File.WriteAllBytes(Path.Combine(source, "textures", "ab12.png").Also(EnsureFolder), [1, 2, 3]);
        File.WriteAllText(Path.Combine(source, "imports", "Bench", "SM_Bench.uasset").Also(EnsureFolder), "bench");
        File.WriteAllText(Path.Combine(source, "EconomyOverride.json"), "{}");
        var modA = Directory.CreateDirectory(Path.Combine(source, ProjectMods.FolderName, "ZetaMap", "SCUM", "Content")).Parent!.Parent!;
        File.WriteAllText(Path.Combine(modA.FullName, "SCUM", "Content", "Map.umap"), "a");
        var modB = Directory.CreateDirectory(Path.Combine(source, ProjectMods.FolderName, "AlphaCars", "SCUM", "Content")).Parent!.Parent!;
        File.WriteAllText(Path.Combine(modB.FullName, "SCUM", "Content", "Car.uasset"), "b");
        modA.CreationTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        modB.CreationTimeUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(Path.Combine(source, ProjectMods.FolderName, "Half" + ProjectMods.ImportingSuffix, "SCUM", "Content"));
        File.WriteAllText(Path.Combine(source, ProjectCopies.SnapshotsFolderName, "old", Project.ManifestFileName).Also(EnsureFolder), "{}");
        var picture = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(source, "textures", "ab12.png"), picture);
        var modsOrder = ProjectMods.Folders(source).Select(Path.GetFileName).ToList();
        Assert.Equal(["ZetaMap", "AlphaCars"], modsOrder);

        // Copied while the project is open (its journal is held for appending, its lock for one writer).
        clock.Advance(TimeSpan.FromDays(1));
        var copy = await ProjectCopies.DuplicateAsync(source, temp.Combine("projects"), "Outpost v2", clock: clock);

        Assert.Equal(temp.Combine("projects", "Outpost v2.ssproj"), copy);
        Assert.False(File.Exists(Path.Combine(copy, ".lock")));
        Assert.False(Directory.Exists(Path.Combine(copy, ProjectCopies.SnapshotsFolderName)));
        Assert.False(Directory.Exists(Path.Combine(copy, ProjectMods.FolderName, "Half" + ProjectMods.ImportingSuffix)));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(copy, "textures", "ab12.png")));
        Assert.Equal(picture, File.GetLastWriteTimeUtc(Path.Combine(copy, "textures", "ab12.png"))); // a decal's picture keeps its time
        Assert.Equal("bench", File.ReadAllText(Path.Combine(copy, "imports", "Bench", "SM_Bench.uasset")));
        Assert.True(File.Exists(Path.Combine(copy, "EconomyOverride.json")));
        Assert.Equal(modsOrder, ProjectMods.Folders(copy).Select(Path.GetFileName)); // a later import still wins

        using (var opened = Project.Open(copy, clock))
        {
            Assert.Equal("Outpost v2", opened.Manifest.Name);
            Assert.Equal(clock.GetUtcNow(), opened.Manifest.Created);
            Assert.Equal("# Outpost\n\nbridge next\n", opened.Notes);
            Assert.Equal(project.Journal.History.Select(h => (h.Seq, h.Status)), opened.Journal.History.Select(h => (h.Seq, h.Status)));
            Assert.True(opened.State.IsDeleted(House));
            Assert.False(opened.State.IsDeleted(Barrel));
            Assert.True(opened.CanRedo); // the undone edit can be redone in the copy as in the project
            opened.Apply(new DeleteActorOp(new ActorRef(Outpost, "Crate_1")));
        }

        // The project it came from is as it was, and still open.
        Assert.Equal("Outpost", project.Manifest.Name);
        Assert.False(project.State.IsDeleted(new ActorRef(Outpost, "Crate_1")));
        project.Apply(new DeleteActorOp(new ActorRef(Outpost, "Crate_2")));

        // The same name again is refused; FreeName finds the next free one.
        await Assert.ThrowsAsync<IOException>(() => ProjectCopies.DuplicateAsync(source, temp.Combine("projects"), "Outpost v2"));
        Assert.Equal("Outpost v2 2", ProjectCopies.FreeName(temp.Combine("projects"), "Outpost v2"));
        Assert.Equal("Outpost v3", ProjectCopies.FreeName(temp.Combine("projects"), "Outpost v3"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ProjectCopies.DuplicateAsync(temp.Combine("nothing"), temp.Combine("projects"), "X"));
    }

    [Fact]
    public async Task ASnapshotIsListedOpensAsANewProjectAndComesBackInPlaceOfTheProject()
    {
        using var temp = new LevelTempDirectory();
        var clock = ManualClock.At2026();
        var folder = temp.Combine("Bridge.ssproj");
        var project = Project.Create(folder, "Bridge", clock: clock);
        try
        {
            project.Apply(new DeleteActorOp(House));
            File.WriteAllText(Path.Combine(folder, "craftables.json"), "first");
            Assert.Empty(ProjectCopies.Snapshots(folder));

            var first = await ProjectCopies.SaveSnapshotAsync(folder, "before the bridge", clock: clock);
            Assert.Equal(Path.Combine(folder, ProjectCopies.SnapshotsFolderName, clock.GetLocalNow().ToString(ProjectCopies.TimeFormat, System.Globalization.CultureInfo.InvariantCulture) + " before the bridge"), first.Path);
            Assert.Equal("before the bridge", first.Name);
            Assert.Equal(1, first.Edits);
            Assert.True(first.Bytes > 0);
            Assert.False(Directory.Exists(Path.Combine(first.Path, ProjectCopies.SnapshotsFolderName)));

            // A name a folder cannot hold, and the same second twice: both are kept apart.
            clock.Advance(TimeSpan.FromMinutes(5));
            project.Apply(new DeleteActorOp(Barrel));
            File.WriteAllText(Path.Combine(folder, "craftables.json"), "second");
            var second = await ProjectCopies.SaveSnapshotAsync(folder, "a/b:c", clock: clock);
            var third = await ProjectCopies.SaveSnapshotAsync(folder, "a/b:c", clock: clock);
            Assert.Equal("a_b_c", second.Name);
            Assert.EndsWith("a_b_c (2)", third.Path, StringComparison.Ordinal);
            Assert.Equal([third.Path, second.Path, first.Path], ProjectCopies.Snapshots(folder).Select(s => s.Path)); // newest first
            Assert.Equal(2, ProjectCopies.Snapshots(folder)[1].Edits);

            // Open as new project: the first snapshot as a project of its own; the project is unchanged.
            var opened = await ProjectCopies.DuplicateAsync(first.Path, temp.Path, "Bridge before", clock: clock);
            using (var copy = Project.OpenReadOnly(opened))
            {
                Assert.Equal("Bridge before", copy.Manifest.Name);
                Assert.True(copy.State.IsDeleted(House));
                Assert.False(copy.State.IsDeleted(Barrel));
            }

            // Restore needs the project closed: open, it is locked.
            await Assert.ThrowsAsync<ProjectLockedException>(() => ProjectCopies.RestoreAsync(folder, first.Path));
            project.Dispose();
            await ProjectCopies.RestoreAsync(folder, first.Path);
            project = Project.Open(folder, clock);
            Assert.True(project.State.IsDeleted(House));
            Assert.False(project.State.IsDeleted(Barrel));
            Assert.Equal("first", File.ReadAllText(Path.Combine(folder, "craftables.json")));
            Assert.Equal(3, ProjectCopies.Snapshots(folder).Count); // the snapshots stay
            Assert.Equal(["Bridge.ssproj"], Directory.GetDirectories(temp.Path).Select(Path.GetFileName).Where(n => n!.StartsWith("Bridge.", StringComparison.Ordinal)));
            Assert.DoesNotContain(Directory.GetFileSystemEntries(folder), e => Path.GetFileName(e).StartsWith(".restore", StringComparison.Ordinal));

            // And forward again: the second snapshot brings both deletes back.
            project.Dispose();
            await ProjectCopies.RestoreAsync(folder, second.Path);
            project = Project.Open(folder, clock);
            Assert.True(project.State.IsDeleted(Barrel));
            Assert.Equal("second", File.ReadAllText(Path.Combine(folder, "craftables.json")));
        }
        finally
        {
            project.Dispose();
        }
    }

    [Fact]
    public async Task ARestoreThatCannotCopyTheSnapshotLeavesTheProjectAsItWas()
    {
        using var temp = new LevelTempDirectory();
        var folder = temp.Combine("Keep.ssproj");
        using (var project = Project.Create(folder, "Keep"))
        {
            project.Apply(new DeleteActorOp(House));
        }

        var snapshot = await ProjectCopies.SaveSnapshotAsync(folder, "one");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectCopies.RestoreAsync(folder, snapshot.Path, cancellationToken: cancelled.Token));
        using (var project = Project.Open(folder))
        {
            Assert.True(project.State.IsDeleted(House));
        }

        Assert.DoesNotContain(Directory.GetFileSystemEntries(folder), e => Path.GetFileName(e).StartsWith(".restore", StringComparison.Ordinal));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectCopies.SaveSnapshotAsync(folder, "two", cancellationToken: cancelled.Token));
        Assert.Single(ProjectCopies.Snapshots(folder)); // a cancelled snapshot leaves nothing behind
    }

    /// <summary>
    /// Review r40, finding 2 (probe 03b): a program (an antivirus scan) holds the snapshot's copied journal without
    /// FILE_SHARE_DELETE, so it cannot be moved in. The restore tries again for a moment, then puts the project back as it
    /// was: the snapshot's craftables.json that had already moved in goes back out, no work folder is left with the project's
    /// files, and the error is the rename's. Once the file is free, the restore works.
    /// </summary>
    [Fact]
    public async Task ARestoreThatCannotMoveAFileInPutsTheProjectBackAsItWas()
    {
        using var temp = new LevelTempDirectory();
        var folder = temp.Combine("Held.ssproj");
        using (var project = Project.Create(folder, "Held"))
        {
            project.Apply(new DeleteActorOp(House));
        }

        var snapshot = await ProjectCopies.SaveSnapshotAsync(folder, "with craftables");
        File.WriteAllText(Path.Combine(snapshot.Path, "craftables.json"), "from the snapshot"); // the project has none
        using (var project = Project.Open(folder))
        {
            project.Apply(new DeleteActorOp(Barrel));
        }

        var before = Entries(folder);
        var journal = File.ReadAllBytes(Path.Combine(folder, Project.JournalFileName));
        var holder = new HoldWhenCopied(Path.Combine(folder, ".restore-new", Project.JournalFileName));
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => ProjectCopies.RestoreAsync(folder, snapshot.Path, holder));
            Assert.True(holder.Held);
            Assert.DoesNotContain("could not be put back", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            holder.Dispose();
        }

        Assert.Equal(before, Entries(folder).Where(e => e != ".restore-new")); // the held copy may stay in .restore-new
        Assert.False(File.Exists(Path.Combine(folder, "craftables.json")));
        Assert.False(Directory.Exists(Path.Combine(folder, ".restore-old")));
        Assert.Equal(journal, File.ReadAllBytes(Path.Combine(folder, Project.JournalFileName)));
        using (var project = Project.Open(folder))
        {
            Assert.True(project.State.IsDeleted(Barrel));
        }

        // The file is free again: the restore works (the leftover copy is replaced).
        await ProjectCopies.RestoreAsync(folder, snapshot.Path);
        Assert.Equal("from the snapshot", File.ReadAllText(Path.Combine(folder, "craftables.json")));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(folder), e => Path.GetFileName(e).StartsWith(".restore", StringComparison.Ordinal));
        using (var project = Project.Open(folder))
        {
            Assert.False(project.State.IsDeleted(Barrel));
        }
    }

    /// <summary>A file held for a moment (an antivirus scan) is waited for: the rename is tried again and the restore goes through.</summary>
    [Fact]
    public async Task ARestoreWaitsForAFileHeldForAMoment()
    {
        using var temp = new LevelTempDirectory();
        var folder = temp.Combine("Moment.ssproj");
        using (var project = Project.Create(folder, "Moment"))
        {
            project.Apply(new DeleteActorOp(House));
        }

        var snapshot = await ProjectCopies.SaveSnapshotAsync(folder, "one");
        File.WriteAllText(Path.Combine(snapshot.Path, "craftables.json"), "from the snapshot");
        using var holder = new HoldWhenCopied(Path.Combine(folder, ".restore-new", Project.JournalFileName), release: TimeSpan.FromMilliseconds(200));

        await ProjectCopies.RestoreAsync(folder, snapshot.Path, holder);

        Assert.True(holder.Held);
        Assert.Equal("from the snapshot", File.ReadAllText(Path.Combine(folder, "craftables.json")));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(folder), e => Path.GetFileName(e).StartsWith(".restore", StringComparison.Ordinal));
    }

    /// <summary>
    /// The project's own files left in .restore-old by a restore that could not put them back are never deleted: the next
    /// restore refuses and names the folder. A .restore-old without the unfinished mark (a finished restore's leftovers) and
    /// one with nothing but the mark are cleared.
    /// </summary>
    [Fact]
    public async Task TheFilesOfARestoreThatDidNotFinishAreNeverDeleted()
    {
        using var temp = new LevelTempDirectory();
        var folder = temp.Combine("Left.ssproj");
        using (var project = Project.Create(folder, "Left"))
        {
            project.Apply(new DeleteActorOp(House));
        }

        var snapshot = await ProjectCopies.SaveSnapshotAsync(folder, "one");
        var outgoing = Directory.CreateDirectory(Path.Combine(folder, ".restore-old")).FullName;
        File.WriteAllText(Path.Combine(outgoing, ".restore-unfinished"), "running");
        File.WriteAllText(Path.Combine(outgoing, "EconomyOverride.json"), "the project's own");

        var error = await Assert.ThrowsAsync<IOException>(() => ProjectCopies.RestoreAsync(folder, snapshot.Path));
        Assert.Contains(outgoing, error.Message, StringComparison.Ordinal);
        Assert.Equal("the project's own", File.ReadAllText(Path.Combine(outgoing, "EconomyOverride.json")));
        Assert.False(Directory.Exists(Path.Combine(folder, ".restore-new")));

        // Only the mark left (everything was moved back by hand), or leftovers without it: cleared, and the restore runs.
        File.Delete(Path.Combine(outgoing, "EconomyOverride.json"));
        await ProjectCopies.RestoreAsync(folder, snapshot.Path);
        Directory.CreateDirectory(outgoing);
        File.WriteAllText(Path.Combine(outgoing, "journal.jsonl"), "replaced by a finished restore");
        await ProjectCopies.RestoreAsync(folder, snapshot.Path);
        Assert.DoesNotContain(Directory.GetFileSystemEntries(folder), e => Path.GetFileName(e).StartsWith(".restore", StringComparison.Ordinal));
        using var opened = Project.Open(folder);
        Assert.True(opened.State.IsDeleted(House));
    }

    private static List<string> Entries(string folder) =>
        Directory.GetFileSystemEntries(folder).Select(e => Path.GetFileName(e)!).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static void EnsureFolder(string file) => Directory.CreateDirectory(Path.GetDirectoryName(file)!);

    /// <summary>
    /// When the copy of the snapshot is complete (its last progress report), opens <paramref name="path"/> for reading without
    /// FILE_SHARE_DELETE, as an antivirus scan of a new file does, so it cannot be renamed; with <paramref name="release"/>
    /// it lets go after that time.
    /// </summary>
    private sealed class HoldWhenCopied(string path, TimeSpan? release = null) : ScumStudio.Core.Abstractions.IProgressSink, IDisposable
    {
        private FileStream? _held;

        public bool Held { get; private set; }

        public void Report(string step, long current, long total)
        {
            if (step != "Copying" || current != total || _held is not null || !File.Exists(path))
            {
                return;
            }

            _held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Held = true;
            if (release is { } after)
            {
                var held = _held;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(after);
                    held.Dispose();
                });
            }
        }

        public void Dispose() => _held?.Dispose();
    }
}

/// <summary>Small helper for tests: run an action on a value and return it.</summary>
internal static class AlsoExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
