using System.Text;
using System.Text.Json;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using static ScumStudio.Tests.Level.EditOpTests;

namespace ScumStudio.Tests.Level;

public sealed class JournalTests
{
    private static EditOp Delete(string actor) => new DeleteActorOp(new ActorRef(Outpost, actor));

    [Fact]
    public void CreatesFileWithHeaderAndAppendsOneLinePerChange()
    {
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("sub", "journal.jsonl");
        var clock = ManualClock.At2026();
        using (var journal = Journal.Open(path, clock))
        {
            Assert.Empty(journal.Applied);
            Assert.False(journal.CanUndo);
            Assert.False(journal.CanRedo);
            var entry = journal.Append(Delete("A"));
            Assert.Equal(1, entry.Seq);
            Assert.Equal(clock.Now, entry.At);
            clock.Advance(TimeSpan.FromMinutes(1));
            journal.Append(new SetTransformOp(new ActorRef(Outpost, "B"), TransformValue.Identity, Somewhere));
            Assert.Equal(2, journal.UndoPointer);
            Assert.Equal(2, journal.Undo()!.Seq);
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(4, lines.Length);
        Assert.Contains("\"type\":\"header\"", lines[0], StringComparison.Ordinal);
        Assert.Contains(Journal.FormatId, lines[0], StringComparison.Ordinal);
        Assert.Contains("\"type\":\"edit\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"edit\":{\"op\":\"deleteActor\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"type\":\"undo\",\"ref\":2", lines[3], StringComparison.Ordinal);
        Assert.All(lines, l => JsonDocument.Parse(l).Dispose());
        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble), "no BOM");
    }

    [Fact]
    public void UndoRedoAndNewEditsFollowStackSemantics()
    {
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        using var journal = Journal.Open(path, ManualClock.At2026());
        var a = journal.Append(Delete("A"));
        var b = journal.Append(Delete("B"));
        var c = journal.Append(Delete("C"));

        Assert.Equal(c, journal.Undo());
        Assert.Equal(b, journal.Undo());
        Assert.Equal(new[] { a }, journal.Applied);
        Assert.Equal(new[] { c, b }, journal.RedoStack);
        Assert.Equal(b, journal.Redo());
        Assert.Equal(new[] { a, b }, journal.Applied);
        Assert.True(journal.CanRedo);

        var d = journal.Append(Delete("D")); // discards C from the redo stack
        Assert.False(journal.CanRedo);
        Assert.Null(journal.Redo());
        Assert.Equal(new[] { a, b, d }, journal.Applied);

        var history = journal.History;
        Assert.Equal(new long[] { 1, 2, 3, 7 }, history.Select(h => h.Seq));
        Assert.Equal(
            new[] { HistoryStatus.Applied, HistoryStatus.Applied, HistoryStatus.Discarded, HistoryStatus.Applied },
            history.Select(h => h.Status));
        Assert.Equal("Delete A_0_Outpost/C", history[2].Summary);
        Assert.Equal(Outpost, history[2].Level);
        Assert.Equal("C", history[2].Actor);

        journal.Undo();
        journal.Undo();
        journal.Undo();
        Assert.Null(journal.Undo());
        Assert.Equal(0, journal.UndoPointer);
        Assert.All(journal.History.Where(h => h.Seq != 3), h => Assert.Equal(HistoryStatus.Undone, h.Status));
    }

    [Fact]
    public void ReopeningReplaysTheSameState()
    {
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        IReadOnlyList<JournalEntry> applied;
        IReadOnlyList<JournalEntry> redo;
        IReadOnlyList<HistoryItem> history;
        using (var journal = Journal.Open(path))
        {
            journal.Append(Delete("A"));
            journal.Append(new DeleteAllOfKindOp(new KindMatch(MatchBy.StaticMesh, Rock), EditScope.Island,
                [new ActorRef(Outpost, "R1"), new ActorRef(Port, "R2")], [new InstanceRef(Port, "F", "ISM", 9)]));
            journal.Append(Delete("B"));
            journal.Undo();
            journal.Undo();
            journal.Redo();
            journal.Append(new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Elsewhere));
            journal.Append(Delete("C"));
            journal.Undo();
            applied = journal.Applied;
            redo = journal.RedoStack;
            history = journal.History;
        }

        using var reopened = Journal.Open(path);
        Assert.Equal(applied, reopened.Applied);
        Assert.Equal(redo, reopened.RedoStack);
        Assert.Equal(history, reopened.History);
        Assert.Empty(reopened.Warnings);

        // Sequence numbers continue after the last record.
        var next = reopened.Append(Delete("E"));
        Assert.Equal(10, next.Seq);
    }

    [Fact]
    public void RecoversFromATornLastLine()
    {
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        using (var journal = Journal.Open(path))
        {
            journal.Append(Delete("A"));
        }

        File.AppendAllText(path, "{\"seq\":2,\"at\":\"2026-09-30T12:00:00+00:00\",\"type\":\"edit\",\"edit\":{\"op\":\"delete");
        using (var journal = Journal.Open(path))
        {
            Assert.Single(journal.Warnings);
            Assert.Single(journal.Applied);
            journal.Append(Delete("B"));
        }

        using (var again = Journal.Open(path))
        {
            Assert.Empty(again.Warnings);
            Assert.Equal(new[] { "A", "B" }, again.Applied.Select(e => ((DeleteActorOp)e.Op).Target.Actor));
        }

        // Read only after the journal released the file: on Windows an open writer blocks File.ReadAllLines.
        Assert.Equal(3, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void TerminatesACompleteLastLineWithoutNewline()
    {
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        using (var journal = Journal.Open(path))
        {
            journal.Append(Delete("A"));
        }

        var text = File.ReadAllText(path).TrimEnd('\n');
        File.WriteAllText(path, text);
        using (var journal = Journal.Open(path))
        {
            Assert.Empty(journal.Warnings);
            journal.Append(Delete("B"));
        }

        using var again = Journal.Open(path);
        Assert.Equal(2, again.Applied.Count);
    }

    [Fact]
    public void RejectsCorruptJournals()
    {
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        using (var journal = Journal.Open(path))
        {
            journal.Append(Delete("A"));
        }

        var good = File.ReadAllLines(path);
        File.WriteAllLines(path, [good[0], "not json", good[1]]);
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));

        File.WriteAllLines(path, [good[0], good[1], "{\"seq\":2,\"at\":\"2026-09-30T12:00:00+00:00\",\"type\":\"undo\",\"ref\":5}"]);
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));

        File.WriteAllLines(path, [good[0], good[1], "{\"seq\":2,\"at\":\"2026-09-30T12:00:00+00:00\",\"type\":\"redo\",\"ref\":1}"]);
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));

        File.WriteAllLines(path, [good[0], good[1], good[1]]); // duplicate sequence number
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));

        File.WriteAllLines(path, [good[0], "{\"seq\":1,\"at\":\"2026-09-30T12:00:00+00:00\",\"type\":\"edit\"}"]);
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));

        File.WriteAllLines(path, [good[0], good[1].Replace("\"op\":\"deleteActor\",", string.Empty, StringComparison.Ordinal)]);
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));
    }

    [Fact]
    public async Task OpensAsync()
    {
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        using (var journal = await Journal.OpenAsync(path))
        {
            journal.Append(Delete("A"));
        }

        using var reopened = await Journal.OpenAsync(path);
        Assert.Single(reopened.Applied);
    }

    [Fact]
    public void KeepsTheFileOpenForOneWriter()
    {
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        using var journal = Journal.Open(path);
        journal.Append(Delete("A"));

        // Readers are allowed while the journal is open.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.True(reader.Length > 0);
        }

        journal.Dispose();
        Assert.Throws<ObjectDisposedException>(() => journal.Append(Delete("B")));
    }
}
