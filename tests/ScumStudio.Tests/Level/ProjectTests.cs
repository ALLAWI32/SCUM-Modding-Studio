using System.Text.Json;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using static ScumStudio.Tests.Level.EditOpTests;

namespace ScumStudio.Tests.Level;

public sealed class ProjectTests
{
    [Fact]
    public void CreatesTheProjectFolder()
    {
        using var temp = new LevelTempDirectory();
        var dir = temp.Combine("Outpost.ssproj");
        var clock = ManualClock.At2026();
        using (var project = Project.Create(dir, "Outpost", "1.3.3.4.149664",
                   [new ProjectSource(temp.Combine("Paks")), new ProjectSource(temp.Combine("ServerPaks"), ProjectSourceKind.Paks, ProjectSourceRole.Server)],
                   clock))
        {
            Assert.Equal(Path.GetFullPath(dir), project.DirectoryPath);
            Assert.Equal("Outpost", project.Manifest.Name);
            Assert.Equal(clock.Now, project.Manifest.Created);
            Assert.True(project.State.IsEmpty);
            Assert.Empty(project.History);
            Assert.Empty(project.PendingExportSet);
        }

        Assert.True(File.Exists(Path.Combine(dir, Project.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(dir, Project.JournalFileName)));
        Assert.True(File.Exists(Path.Combine(dir, Project.NotesFileName)));
        Assert.True(Project.Exists(dir));

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, Project.ManifestFileName)));
        var root = json.RootElement;
        Assert.Equal(ProjectManifest.FormatId, root.GetProperty("format").GetString());
        Assert.Equal("Outpost", root.GetProperty("name").GetString());
        Assert.Equal("1.3.3.4.149664", root.GetProperty("gameBuild").GetString());
        Assert.Equal(2, root.GetProperty("sources").GetArrayLength());
        Assert.Equal("server", root.GetProperty("sources")[1].GetProperty("role").GetString());
        Assert.Equal("paks", root.GetProperty("sources")[1].GetProperty("kind").GetString());

        Assert.Throws<IOException>(() => Project.Create(dir, "Again"));
    }

    [Fact]
    public void OpensWhatWasSaved()
    {
        using var temp = new LevelTempDirectory();
        var dir = temp.Combine("Mod.ssproj");
        var clock = ManualClock.At2026();
        using (var project = Project.Create(dir, "Mod", clock: clock))
        {
            Assert.Equal("unknown", project.Manifest.GameBuild);
            project.UpdateManifest(m => m with { GameBuild = "1.3.3.4", Sources = [new ProjectSource(temp.Combine("Loose"), ProjectSourceKind.Loose)] });
            project.Notes = "# Mod\n\nRemove the rocks near the trader.\n";
            clock.Advance(TimeSpan.FromHours(1));
            project.Save();
        }

        using var reopened = Project.Open(Path.Combine(dir, Project.ManifestFileName));
        Assert.Equal("Mod", reopened.Manifest.Name);
        Assert.Equal("1.3.3.4", reopened.Manifest.GameBuild);
        Assert.Equal(ProjectSourceKind.Loose, Assert.Single(reopened.Manifest.Sources).Kind);
        Assert.Equal(ManualClock.At2026().Now, reopened.Manifest.Created);
        Assert.Equal(ManualClock.At2026().Now.AddHours(1), reopened.Manifest.Modified);
        Assert.Contains("rocks near the trader", reopened.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void AppliesUndoesAndRedoesWithPersistence()
    {
        using var temp = new LevelTempDirectory();
        var dir = temp.Combine("Mod.ssproj");
        var crate = new ActorRef(Outpost, "Crate_1");
        var house = new ActorRef(Outpost, "House_1");
        var add = new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Somewhere);
        string stateAfterSession;
        using (var project = Project.Create(dir, "Mod", clock: ManualClock.At2026()))
        {
            project.Apply(new DeleteActorOp(crate));
            project.Apply(new SetTransformOp(house, TransformValue.Identity, Somewhere));
            project.Apply(add);
            Assert.Equal(new[] { Outpost, Port }, project.PendingExportSet);

            var undone = project.Undo();
            Assert.Equal(add, undone!.Op);
            Assert.False(project.State.IsAdded(add.Created));
            Assert.Equal(new[] { Outpost }, project.PendingExportSet);
            Assert.True(project.CanRedo);

            var redone = project.Redo();
            Assert.Equal(add, redone!.Op);
            Assert.True(project.State.IsAdded(add.Created));

            project.Undo();
            project.Undo(); // the move
            Assert.Null(project.State.GetTransformOverride(house));
            project.Apply(new DeleteActorOp(house)); // discards both redo entries
            Assert.False(project.CanRedo);
            stateAfterSession = project.State.Describe();

            var history = project.History;
            Assert.Equal(4, history.Count);
            Assert.Equal(
                new[] { HistoryStatus.Applied, HistoryStatus.Discarded, HistoryStatus.Discarded, HistoryStatus.Applied },
                history.Select(h => h.Status));
            Assert.Equal("Delete A_0_Outpost/House_1", history[3].Summary);
            Assert.Equal("House_1", history[3].Actor);
            Assert.Equal(Port, history[2].Level);
        }

        using var reopened = Project.Open(dir);
        Assert.Equal(stateAfterSession, reopened.State.Describe());
        Assert.True(reopened.State.IsDeleted(crate));
        Assert.True(reopened.State.IsDeleted(house));
        Assert.Equal(new[] { Outpost }, reopened.PendingExportSet);
        Assert.Equal(2, reopened.Journal.UndoPointer);

        // Undo survives a restart too.
        Assert.Equal(new DeleteActorOp(house), reopened.Undo()!.Op);
        Assert.False(reopened.State.IsDeleted(house));
    }

    [Fact]
    public void RejectsInvalidEditsWithoutWriting()
    {
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Mod.ssproj"), "Mod");
        var journalLength = new FileInfo(project.Journal.FilePath).Length;

        Assert.Throws<InvalidOperationException>(() => project.Apply(new RestoreActorOp(new ActorRef(Outpost, "Never_Deleted"))));
        Assert.Null(project.Undo());
        Assert.Null(project.Redo());
        Assert.Equal(journalLength, new FileInfo(project.Journal.FilePath).Length);
        Assert.Empty(project.History);
    }

    [Fact]
    public void OpensAJournalThatDoesNotReplayWithWhatStillAppliesAndListsTheRest()
    {
        using var temp = new LevelTempDirectory();
        var dir = temp.Combine("Mod.ssproj");
        using (var project = Project.Create(dir, "Mod"))
        {
            project.Apply(new DeleteActorOp(new ActorRef(Outpost, "A")));
        }

        // Hand-edited journal: the same actor deleted twice (an older version journaled bulk deletes it then applied only
        // halfway, so the edits after them were made against that half: the project must still open).
        var journalPath = Path.Combine(dir, Project.JournalFileName);
        var lines = File.ReadAllLines(journalPath).ToList();
        lines.Add(lines[1].Replace("\"seq\":1", "\"seq\":2", StringComparison.Ordinal));
        File.WriteAllLines(journalPath, lines);

        using (var opened = Project.Open(dir))
        {
            var problem = Assert.Single(opened.ReplayProblems);
            Assert.Contains("edit #2", problem, StringComparison.Ordinal);
            Assert.Contains("does not replay", problem, StringComparison.Ordinal);
            Assert.Equal(2, opened.History.Count);
            Assert.Single(opened.State.DeletedActors);
        }

        // The open released the journal file; a clean journal lists no problem.
        File.WriteAllLines(journalPath, lines.Take(2));
        using var ok = Project.Open(dir);
        Assert.Empty(ok.ReplayProblems);
        Assert.Single(ok.Journal.Applied);
    }

    /// <summary>Appends <paramref name="op"/> to a closed project's journal unchecked, as 0.2.7 could.</summary>
    internal static void AppendUnchecked(string projectDirectory, int seq, EditOp op) =>
        File.AppendAllText(Path.Combine(projectDirectory, Project.JournalFileName),
            $"{{\"seq\":{seq},\"at\":\"2026-10-06T18:02:30+00:00\",\"type\":\"edit\",\"edit\":{JsonSerializer.Serialize(op, global::ScumStudio.Level.Serialization.LevelJson.Compact)}}}\n");

    [Fact]
    public void ReportsMissingProjects()
    {
        using var temp = new LevelTempDirectory();
        Assert.False(Project.Exists(temp.Path));
        Assert.Throws<FileNotFoundException>(() => Project.Open(temp.Path));
        File.WriteAllText(temp.Combine(Project.ManifestFileName), "{ not json");
        Assert.Throws<InvalidDataException>(() => Project.Open(temp.Path));
        File.WriteAllText(temp.Combine(Project.ManifestFileName), "{\"format\":\"other/1\",\"name\":\"x\"}");
        Assert.Throws<InvalidDataException>(() => Project.Open(temp.Path));
    }

    [Fact]
    public void FindAcceptsAFileInsideOrTheOnlyProjectInAFolder()
    {
        // A folder dialog lets a user pick the project, a file in it, or its parent (Documents\ScumStudio Projects).
        using var temp = new LevelTempDirectory();
        var parent = temp.Combine("ScumStudio Projects");
        var first = System.IO.Path.Combine(parent, "MyMapMod.ssproj");
        Project.Create(first, "MyMapMod").Dispose();
        Assert.Equal(first, Project.Find(first));
        Assert.Equal(first, Project.Find(System.IO.Path.Combine(first, Project.JournalFileName)));
        Assert.Equal(first, Project.Find(System.IO.Path.Combine(first, Project.ManifestFileName)));
        Assert.Equal(first, Project.Find(parent));
        using (Project.Open(parent))
        {
        }

        Project.Create(System.IO.Path.Combine(parent, "Second.ssproj"), "Second").Dispose();
        Assert.Null(Project.Find(parent)); // two projects: which one is not ours to guess
        Assert.Null(Project.Find(temp.Combine("nowhere")));
        Assert.Null(Project.Find(" "));
    }

    [Fact]
    public async Task WorksAsynchronously()
    {
        using var temp = new LevelTempDirectory();
        var dir = temp.Combine("Async.ssproj");
        using (var project = await Project.CreateAsync(dir, "Async", "1.0"))
        {
            project.Apply(new DeleteInstanceOp(new InstanceRef(Outpost, "Foliage", "ISM", 3)));
            project.Notes = "async notes";
            await project.SaveAsync();
        }

        using var reopened = await Project.OpenAsync(dir);
        Assert.Equal("1.0", reopened.Manifest.GameBuild);
        Assert.Equal("async notes", reopened.Notes);
        Assert.True(reopened.State.IsDeleted(new InstanceRef(Outpost, "Foliage", "ISM", 3)));
    }

    [Fact]
    public void DetectsSourceKinds()
    {
        using var temp = new LevelTempDirectory();
        var paks = Directory.CreateDirectory(temp.Combine("Paks")).FullName;
        File.WriteAllBytes(Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"), [0]);
        var loose = Directory.CreateDirectory(temp.Combine("Loose", "SCUM", "Content")).Parent!.Parent!.FullName;

        Assert.Equal(ProjectSourceKind.Paks, ProjectSource.Detect(paks).Kind);
        Assert.Equal(ProjectSourceKind.Paks, ProjectSource.Detect(Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak")).Kind);
        Assert.Equal(ProjectSourceKind.Loose, ProjectSource.Detect(loose, ProjectSourceRole.Server).Kind);
        Assert.Equal(ProjectSourceRole.Server, ProjectSource.Detect(loose, ProjectSourceRole.Server).Role);
    }
}
