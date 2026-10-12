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
            journal.Append(Delete("B"));
        }

        var good = File.ReadAllLines(path);
        File.WriteAllLines(path, [good[0], "not json", good[1]]);
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));

        File.WriteAllLines(path, [good[0], good[1], "{\"seq\":2,\"at\":\"2026-09-30T12:00:00+00:00\",\"type\":\"undo\",\"ref\":5}"]);
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));

        File.WriteAllLines(path, [good[0], good[1], "{\"seq\":2,\"at\":\"2026-09-30T12:00:00+00:00\",\"type\":\"redo\",\"ref\":1}"]);
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));

        // A sequence number that goes back (not a run of retries, which is repaired) is still corrupt.
        File.WriteAllLines(path, [good[0], good[1], good[2], good[1]]);
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));

        File.WriteAllLines(path, [good[0], "{\"seq\":1,\"at\":\"2026-09-30T12:00:00+00:00\",\"type\":\"edit\"}"]);
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));

        File.WriteAllLines(path, [good[0], good[1].Replace("\"op\":\"deleteActor\",", string.Empty, StringComparison.Ordinal)]);
        Assert.Throws<InvalidDataException>(() => Journal.Open(path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.bak")); // a journal that is refused is never rewritten
    }

    [Fact]
    public void AFailedWriteLeavesNoTraceAndTheNextEditContinues()
    {
        // F009: the disk fills during an edit. Before, the half record stayed in the file (or the stream's buffer) under a
        // sequence number the next edit reused, and the project no longer opened.
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        FlakyFileStream? disk = null;
        using (var journal = Journal.Open(path, ManualClock.At2026(), p => disk = new FlakyFileStream(p)))
        {
            journal.Append(Delete("A"));
            disk!.FailWrite = true;
            Assert.Throws<IOException>(() => journal.Append(Delete("B")));
            Assert.Equal(new[] { "A" }, Actors(journal.Applied));
            journal.Append(Delete("C"));
        }

        using var reopened = Journal.Open(path);
        Assert.Empty(reopened.Warnings);
        Assert.Equal(new[] { "A", "C" }, Actors(reopened.Applied));
        Assert.Equal(new long[] { 1, 2 }, reopened.History.Select(h => h.Seq));
    }

    [Fact]
    public void AFailedWriteThenCloseDoesNotBringTheEditBack()
    {
        // F009 case 2: the flush fails and the user closes. Before, the record the user saw fail reached the file anyway.
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        FlakyFileStream? disk = null;
        using (var journal = Journal.Open(path, ManualClock.At2026(), p => disk = new FlakyFileStream(p)))
        {
            journal.Append(Delete("A"));
            disk!.FailFlush = true;
            Assert.Throws<IOException>(() => journal.Append(Delete("B")));
        }

        using var reopened = Journal.Open(path);
        Assert.Empty(reopened.Warnings);
        Assert.Equal(new[] { "A" }, Actors(reopened.Applied));
    }

    [Fact]
    public void AFailedUndoThenAnEditReplays()
    {
        // F081: an undo that fails to write changes nothing, and the edit after it gets the next sequence number.
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        FlakyFileStream? disk = null;
        IReadOnlyList<JournalEntry> applied;
        IReadOnlyList<HistoryItem> history;
        using (var journal = Journal.Open(path, ManualClock.At2026(), p => disk = new FlakyFileStream(p)))
        {
            journal.Append(Delete("A"));
            journal.Append(Delete("B"));
            disk!.FailFlush = true;
            Assert.Throws<IOException>(() => journal.Undo());
            Assert.Equal(2, journal.UndoPointer);
            Assert.False(journal.CanRedo);
            journal.Append(Delete("C"));
            applied = journal.Applied;
            history = journal.History;
        }

        using var reopened = Journal.Open(path);
        Assert.Empty(reopened.Warnings);
        Assert.Equal(applied, reopened.Applied);
        Assert.Empty(reopened.RedoStack);
        Assert.Equal(history, reopened.History);
        Assert.Equal(new long[] { 1, 2, 3 }, reopened.Applied.Select(e => e.Seq));
    }

    [Fact]
    public void AWriteThatLosesBytesWithoutAnErrorIsRolledBack()
    {
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        FlakyFileStream? disk = null;
        using (var journal = Journal.Open(path, ManualClock.At2026(), p => disk = new FlakyFileStream(p)))
        {
            journal.Append(Delete("A"));
            disk!.ShortWrite = true;
            Assert.Throws<IOException>(() => journal.Append(Delete("B")));
            journal.Append(Delete("C"));
        }

        using var reopened = Journal.Open(path);
        Assert.Empty(reopened.Warnings);
        Assert.Equal(new[] { "A", "C" }, Actors(reopened.Applied));
    }

    [Fact]
    public void AWriteThatCannotBeRolledBackClosesTheJournal()
    {
        // The disk drops for good: the half record cannot be cut off. The journal refuses every later change instead of
        // writing after it, and the next open drops the half record as a torn last line.
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        FlakyFileStream? disk = null;
        using (var journal = Journal.Open(path, ManualClock.At2026(), p => disk = new FlakyFileStream(p)))
        {
            journal.Append(Delete("A"));
            disk!.FailWrite = true;
            disk.FailSetLength = true;
            Assert.Throws<IOException>(() => journal.Append(Delete("B")));
            var refused = Assert.Throws<IOException>(() => journal.Append(Delete("C")));
            Assert.Contains("reopen the project", refused.Message, StringComparison.Ordinal);
            Assert.Throws<IOException>(() => journal.Undo());
            Assert.Equal(new[] { "A" }, Actors(journal.Applied));
        }

        using var reopened = Journal.Open(path);
        Assert.Single(reopened.Warnings);
        Assert.Equal(new[] { "A" }, Actors(reopened.Applied));
    }

    [Fact]
    public void AJournalAnOlderVersionDamagedOpensWithAWarning()
    {
        // Before this fix a failed write was flushed later together with the next record, under the same sequence number:
        // [header, #1 A, #2 B (failed), #2 C]. The last record of such a run is the one the user's session went on with.
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        var clock = ManualClock.At2026();
        using (var journal = Journal.Open(path, clock))
        {
            journal.Append(Delete("A"));
            journal.Append(Delete("B"));
        }

        var lines = File.ReadAllLines(path);
        var retry = Journal.SerializeRecord(new JournalRecord { Seq = 2, At = clock.Now, Type = JournalRecordType.Edit, Edit = Delete("C") });
        var damaged = Encoding.UTF8.GetBytes(string.Join('\n', lines.Append(retry)) + "\n");
        File.WriteAllBytes(path, damaged);

        using (var journal = Journal.Open(path, clock))
        {
            var warning = Assert.Single(journal.Warnings);
            Assert.Contains("Line 3", warning, StringComparison.Ordinal);
            Assert.Contains("sequence 2", warning, StringComparison.Ordinal);
            Assert.Contains("journal.jsonl.bak", warning, StringComparison.Ordinal);
            Assert.Equal(new[] { "A", "C" }, Actors(journal.Applied));
            Assert.Equal(3, journal.Append(Delete("D")).Seq);
        }

        Assert.Equal(damaged, File.ReadAllBytes(path + ".bak"));
        using (var again = Journal.Open(path, clock))
        {
            Assert.Empty(again.Warnings);
            Assert.Equal(new[] { "A", "C", "D" }, Actors(again.Applied));
        }

        // A second repair keeps the first backup and saves under a timestamped name.
        File.WriteAllBytes(path, damaged);
        using (var journal = Journal.Open(path, clock))
        {
            Assert.Contains(".bak", Assert.Single(journal.Warnings), StringComparison.Ordinal);
        }

        Assert.Equal(damaged, File.ReadAllBytes(path + ".bak"));
        var stamped = Assert.Single(Directory.GetFiles(temp.Path, "journal.jsonl.*.bak"));
        Assert.Equal(damaged, File.ReadAllBytes(stamped));

        // The same record twice (formerly a rejected duplicate) is such a run too; a repair in the same second keeps every
        // earlier backup.
        var twice = Encoding.UTF8.GetBytes(string.Join('\n', lines[0], lines[1], lines[1]) + "\n");
        File.WriteAllBytes(path, twice);
        using (var journal = Journal.Open(path, clock))
        {
            Assert.Contains("Line 2", Assert.Single(journal.Warnings), StringComparison.Ordinal);
            Assert.Equal(new[] { "A" }, Actors(journal.Applied));
        }

        Assert.Equal(damaged, File.ReadAllBytes(stamped));
        var third = Assert.Single(Directory.GetFiles(temp.Path, "journal.jsonl.*.bak"), f => f != stamped);
        Assert.Equal(twice, File.ReadAllBytes(third));
    }

    [Fact]
    public void AReadOnlyOpenReadsWhileAWriterHasTheFile()
    {
        // F217: CLI export reads the project the app is editing.
        using var temp = new LevelTempDirectory();
        var path = temp.Combine("journal.jsonl");
        var clock = ManualClock.At2026();
        using (var writer = Journal.Open(path, clock))
        {
            writer.Append(Delete("A"));
            writer.Append(Delete("B"));
            writer.Undo();
            using (var reader = Journal.OpenReadOnly(path))
            {
                Assert.True(reader.IsReadOnly);
                Assert.False(writer.IsReadOnly);
                Assert.Equal(writer.Applied, reader.Applied);
                Assert.Equal(writer.RedoStack, reader.RedoStack);
                Assert.Throws<InvalidOperationException>(() => reader.Append(Delete("C")));
                Assert.Throws<InvalidOperationException>(() => reader.Undo());
                Assert.Throws<InvalidOperationException>(() => reader.Redo());
            }

            writer.Append(Delete("C"));
        }

        // Damage only the reader repairs, in memory: a retried sequence number and a torn last line.
        var lines = File.ReadAllLines(path);
        var retry = Journal.SerializeRecord(new JournalRecord { Seq = 4, At = clock.Now, Type = JournalRecordType.Edit, Edit = Delete("X") });
        File.WriteAllText(path, string.Join('\n', lines[..^1].Append(retry).Append(lines[^1])) + "\n{\"seq\":5,\"at\":\"2026-09-30T12:00:00+00:00\",\"type\":\"ed");
        var before = File.ReadAllBytes(path);
        using (var reader = Journal.OpenReadOnly(path))
        {
            Assert.Equal(2, reader.Warnings.Count);
            Assert.Equal(new[] { "A", "C" }, Actors(reader.Applied));
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.bak"));
    }

    private static IEnumerable<string> Actors(IEnumerable<JournalEntry> entries) =>
        entries.Select(e => ((DeleteActorOp)e.Op).Target.Actor);

    /// <summary>The journal file on a disk that fails on demand: each flag fails the next call of its kind once.</summary>
    private sealed class FlakyFileStream(string path)
        : FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSize: 0)
    {
        /// <summary>Writes half the bytes, then throws (disk full).</summary>
        public bool FailWrite { get; set; }

        /// <summary>Writes half the bytes and reports success.</summary>
        public bool ShortWrite { get; set; }

        /// <summary>Flush(true) throws.</summary>
        public bool FailFlush { get; set; }

        /// <summary>SetLength throws (the rollback fails).</summary>
        public bool FailSetLength { get; set; }

        // Only the span overload, which the journal writes through: overriding the array one too would recurse through
        // FileStream's strategy for derived types.
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!FailWrite && !ShortWrite)
            {
                base.Write(buffer);
                return;
            }

            base.Write(buffer[..(buffer.Length / 2)]);
            var fail = FailWrite;
            FailWrite = ShortWrite = false;
            if (fail)
            {
                throw DiskFull();
            }
        }

        public override void Flush(bool flushToDisk)
        {
            if (FailFlush)
            {
                FailFlush = false;
                throw DiskFull();
            }

            base.Flush(flushToDisk);
        }

        public override void SetLength(long value)
        {
            if (FailSetLength)
            {
                FailSetLength = false;
                throw DiskFull();
            }

            base.SetLength(value);
        }

        private static IOException DiskFull() => new("There is not enough space on the disk.", unchecked((int)0x80070070));
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
