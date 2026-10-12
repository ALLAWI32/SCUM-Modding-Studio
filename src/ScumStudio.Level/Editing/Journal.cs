using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScumStudio.Core.IO;
using ScumStudio.Level.Serialization;

namespace ScumStudio.Level.Editing;

/// <summary>An applied (or undone) edit in the journal.</summary>
/// <param name="Seq">Sequence number of the journal record that added the edit (1-based, unique).</param>
/// <param name="At">When the edit was made (UTC).</param>
/// <param name="Op">The operation.</param>
public sealed record JournalEntry(long Seq, DateTimeOffset At, EditOp Op);

/// <summary>State of an edit in the history.</summary>
public enum HistoryStatus
{
    /// <summary>Part of the current state.</summary>
    Applied,

    /// <summary>Undone; can be redone.</summary>
    Undone,

    /// <summary>Undone and then superseded by a newer edit (no longer redoable); kept for the record.</summary>
    Discarded,
}

/// <summary>One line of the history listing.</summary>
/// <param name="Seq">Sequence number of the edit.</param>
/// <param name="At">Timestamp (UTC).</param>
/// <param name="Op">The operation.</param>
/// <param name="Status">Whether it is applied, undone or discarded.</param>
public sealed record HistoryItem(long Seq, DateTimeOffset At, EditOp Op, HistoryStatus Status)
{
    /// <summary>Human-readable summary (<see cref="EditOp.Describe"/>).</summary>
    public string Summary => Op.Describe();

    /// <summary>Target level package (the primary target's, else the first touched level, else the first touched asset package).</summary>
    public string? Level => Op.GetPrimaryTarget()?.Level ?? Op.GetTouchedLevels().FirstOrDefault() ?? Op.GetTouchedAssets().FirstOrDefault();

    /// <summary>Target actor name, or null for bulk operations.</summary>
    public string? Actor => Op.GetPrimaryTarget()?.Actor;
}

/// <summary>Kind of a journal line.</summary>
public enum JournalRecordType
{
    /// <summary>First line: format identifier.</summary>
    Header,

    /// <summary>A new edit (<see cref="JournalRecord.Edit"/>).</summary>
    Edit,

    /// <summary>Undo of the edit <see cref="JournalRecord.Ref"/>.</summary>
    Undo,

    /// <summary>Redo of the edit <see cref="JournalRecord.Ref"/>.</summary>
    Redo,
}

/// <summary>One line of <c>journal.jsonl</c>.</summary>
public sealed record JournalRecord
{
    /// <summary>Sequence number (0 for the header, then 1, 2, ...).</summary>
    public long Seq { get; init; }

    /// <summary>Timestamp (UTC).</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Record kind.</summary>
    public JournalRecordType Type { get; init; }

    /// <summary>Header only: format identifier.</summary>
    public string? Format { get; init; }

    /// <summary>Undo/redo only: sequence number of the edit.</summary>
    public long? Ref { get; init; }

    /// <summary>Edit only: the operation (discriminated by <c>"op"</c>).</summary>
    public EditOp? Edit { get; init; }
}

/// <summary>
/// Append-only JSONL log of edits with an undo pointer. Every change is one line, flushed to disk immediately:
/// <c>edit</c> lines carry an <see cref="EditOp"/>, <c>undo</c>/<c>redo</c> lines reference an edit. Nothing is
/// rewritten in normal use, so the file is a complete audit trail; the current state (the applied stack and the redo
/// stack) is recomputed on open. A new edit after undos discards the redo stack (the undone edits stay in the file as
/// <see cref="HistoryStatus.Discarded"/>).
/// </summary>
/// <remarks>
/// One writer per project: the file stays open for appending (others may read, see <see cref="OpenReadOnly"/>), and the
/// project's lock keeps a second window out. A write that fails is rolled back (the file is cut back and the sequence
/// number stays free), so a failed change never reaches the file later; when even the rollback fails the journal closes.
/// On open, a line torn by a crash at the very end is truncated, and a run of records sharing one sequence number (a
/// failed write an older version flushed later) keeps only its last record: the file is then rewritten after the
/// original is saved as <c>journal.jsonl.bak</c>. Both are reported in <see cref="Warnings"/>; any other unreadable or
/// inconsistent line is an error.
/// </remarks>
public sealed class Journal : IDisposable
{
    /// <summary>Format identifier written in the header line.</summary>
    public const string FormatId = "scumstudio.journal/1";

    private const string FaultedMessage = "The journal could not be written and was closed; reopen the project.";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private readonly Func<string, FileStream> _openWriter;
    private readonly List<JournalEntry> _applied = [];
    private readonly List<JournalEntry> _redo = [];
    private readonly List<JournalEntry> _allEdits = [];
    private readonly HashSet<long> _discarded = [];
    private readonly List<string> _warnings = [];
    private FileStream? _stream;
    private long _lastSeq;
    private bool _faulted;

    private Journal(string filePath, TimeProvider clock, Func<string, FileStream> openWriter)
    {
        FilePath = filePath;
        _clock = clock;
        _openWriter = openWriter;
    }

    /// <summary>Full path of the journal file.</summary>
    public string FilePath { get; }

    /// <summary>True when opened with <see cref="OpenReadOnly"/>: the state is readable, every change is refused.</summary>
    public bool IsReadOnly { get; private init; }

    /// <summary>Edits currently applied, oldest first (the effective operation list).</summary>
    public IReadOnlyList<JournalEntry> Applied
    {
        get
        {
            lock (_lock)
            {
                return _applied.ToArray();
            }
        }
    }

    /// <summary>Undone edits that can be redone; the last element is redone first.</summary>
    public IReadOnlyList<JournalEntry> RedoStack
    {
        get
        {
            lock (_lock)
            {
                return _redo.ToArray();
            }
        }
    }

    /// <summary>The undo pointer: number of applied edits.</summary>
    public int UndoPointer
    {
        get
        {
            lock (_lock)
            {
                return _applied.Count;
            }
        }
    }

    /// <summary>True when there is an edit to undo.</summary>
    public bool CanUndo => UndoPointer > 0;

    /// <summary>True when there is an undone edit to redo.</summary>
    public bool CanRedo
    {
        get
        {
            lock (_lock)
            {
                return _redo.Count > 0;
            }
        }
    }

    /// <summary>Every edit ever recorded, oldest first, with its status.</summary>
    public IReadOnlyList<HistoryItem> History
    {
        get
        {
            lock (_lock)
            {
                var applied = _applied.Select(e => e.Seq).ToHashSet();
                return _allEdits.Select(e => new HistoryItem(e.Seq, e.At, e.Op,
                    applied.Contains(e.Seq) ? HistoryStatus.Applied
                    : _discarded.Contains(e.Seq) ? HistoryStatus.Discarded
                    : HistoryStatus.Undone)).ToList();
            }
        }
    }

    /// <summary>Problems repaired while opening (e.g. a torn last line).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Adds a problem found while replaying the edits (see <see cref="Warnings"/>).</summary>
    internal void AddWarning(string warning) => _warnings.Add(warning);

    /// <summary>Opens (or creates, with a header line) the journal at <paramref name="filePath"/>.</summary>
    /// <exception cref="InvalidDataException">A line is unreadable or the undo/redo records are inconsistent.</exception>
    public static Journal Open(string filePath, TimeProvider? clock = null) => Open(filePath, clock, OpenWriter);

    /// <summary>Opens the journal, writing through the stream <paramref name="openWriter"/> returns (tests: a failing disk).</summary>
    internal static Journal Open(string filePath, TimeProvider? clock, Func<string, FileStream> openWriter)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var journal = new Journal(Path.GetFullPath(filePath), clock ?? TimeProvider.System, openWriter);
        var bytes = File.Exists(journal.FilePath) ? File.ReadAllBytes(journal.FilePath) : null;
        return journal.Initialize(bytes);
    }

    /// <summary>
    /// Opens the journal for reading only, also while another process writes it. Repairs (a torn last line, failed
    /// writes an older version left) are made in memory and reported in <see cref="Warnings"/>; the file is never written.
    /// </summary>
    /// <exception cref="InvalidDataException">A line is unreadable or the undo/redo records are inconsistent.</exception>
    public static Journal OpenReadOnly(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var journal = new Journal(Path.GetFullPath(filePath), TimeProvider.System, OpenWriter) { IsReadOnly = true };
        if (!File.Exists(journal.FilePath))
        {
            return journal;
        }

        using var stream = new FileStream(journal.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return journal.Initialize(bytes.ToArray());
    }

    /// <summary>Asynchronously opens (or creates) the journal.</summary>
    /// <exception cref="InvalidDataException">A line is unreadable or the undo/redo records are inconsistent.</exception>
    public static async Task<Journal> OpenAsync(string filePath, TimeProvider? clock = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var journal = new Journal(Path.GetFullPath(filePath), clock ?? TimeProvider.System, OpenWriter);
        var bytes = File.Exists(journal.FilePath)
            ? await File.ReadAllBytesAsync(journal.FilePath, cancellationToken).ConfigureAwait(false)
            : null;
        return journal.Initialize(bytes);
    }

    /// <summary>Records a new edit (discarding the redo stack) and returns its entry.</summary>
    /// <exception cref="IOException">The record could not be written (nothing changed).</exception>
    /// <exception cref="InvalidOperationException">The journal is read-only.</exception>
    public JournalEntry Append(EditOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        ThrowIfReadOnly();
        lock (_lock)
        {
            var record = new JournalRecord { Seq = _lastSeq + 1, At = Now(), Type = JournalRecordType.Edit, Edit = op };
            Write(record);
            var entry = new JournalEntry(record.Seq, record.At, op);
            ApplyEdit(entry);
            return entry;
        }
    }

    /// <summary>Undoes the last applied edit; returns it (apply <c>entry.Op.Inverse()</c> to in-memory state), or null.</summary>
    public JournalEntry? Undo()
    {
        ThrowIfReadOnly();
        lock (_lock)
        {
            if (_applied.Count == 0)
            {
                return null;
            }

            var entry = _applied[^1];
            Write(new JournalRecord { Seq = _lastSeq + 1, At = Now(), Type = JournalRecordType.Undo, Ref = entry.Seq });
            _applied.RemoveAt(_applied.Count - 1);
            _redo.Add(entry);
            return entry;
        }
    }

    /// <summary>Redoes the last undone edit; returns it (apply <c>entry.Op</c>), or null.</summary>
    public JournalEntry? Redo()
    {
        ThrowIfReadOnly();
        lock (_lock)
        {
            if (_redo.Count == 0)
            {
                return null;
            }

            var entry = _redo[^1];
            Write(new JournalRecord { Seq = _lastSeq + 1, At = Now(), Type = JournalRecordType.Redo, Ref = entry.Seq });
            _redo.RemoveAt(_redo.Count - 1);
            _applied.Add(entry);
            return entry;
        }
    }

    /// <summary>Serialises one record as a single JSON line (no newline).</summary>
    public static string SerializeRecord(JournalRecord record) => JsonSerializer.Serialize(record, LevelJson.Compact);

    /// <summary>Parses one JSON line.</summary>
    /// <exception cref="JsonException">Invalid JSON or unknown operation.</exception>
    public static JournalRecord DeserializeRecord(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<JournalRecord>(line, LevelJson.Compact) ?? throw new JsonException("Empty journal record.");
        }
        catch (NotSupportedException ex)
        {
            // System.Text.Json reports an edit without its "op" discriminator this way.
            throw new JsonException(ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private Journal Initialize(byte[]? bytes)
    {
        try
        {
            if (bytes is null || bytes.Length == 0)
            {
                if (!IsReadOnly)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    OpenStream(truncateTo: null);
                    Write(new JournalRecord { Seq = 0, At = Now(), Type = JournalRecordType.Header, Format = FormatId });
                }

                return this;
            }

            var (lines, truncateTo) = Parse(bytes);
            var (kept, backup) = DropFailedWrites(lines);
            foreach (var line in kept)
            {
                Replay(line.Record, line.Number);
            }

            if (IsReadOnly)
            {
                return this;
            }

            if (backup is not null)
            {
                // Only once the kept records replayed: a journal that is refused anyway stays as it is.
                AtomicFile.WriteAllBytes(backup, bytes);
                AtomicFile.WriteAllBytes(FilePath, Utf8NoBom.GetBytes(string.Concat(kept.Select(l => l.Text + "\n"))));
                OpenStream(truncateTo: null);
            }
            else
            {
                OpenStream(truncateTo);
                if (truncateTo is null && bytes[^1] != (byte)'\n')
                {
                    // The last record is complete but lacks its newline: terminate it before appending.
                    WriteBytes("\n"u8);
                }
            }

            return this;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private (List<ParsedLine> Lines, long? TruncateTo) Parse(byte[] bytes)
    {
        var text = Utf8NoBom.GetString(bytes);
        var offset = 0L;
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
            offset = 3;
        }

        var parsed = new List<ParsedLine>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var isLast = i == lines.Length - 1;
            var lineBytes = Utf8NoBom.GetByteCount(raw) + (isLast ? 0 : 1);
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                offset += lineBytes;
                continue;
            }

            try
            {
                parsed.Add(new ParsedLine(i + 1, line, DeserializeRecord(line)));
            }
            catch (JsonException ex) when (isLast)
            {
                // A crash while appending leaves a partial last line without its newline: drop it.
                _warnings.Add($"Line {i + 1} was incomplete and has been {(IsReadOnly ? "skipped" : "removed")} ({ex.Message}).");
                return (parsed, offset);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{FilePath}: line {i + 1} is not a valid journal record: {ex.Message}", ex);
            }

            offset += lineBytes;
        }

        return (parsed, null);
    }

    /// <summary>
    /// Drops every record followed by one with the same sequence number: before writes were rolled back, a record whose
    /// write failed stayed in the stream and reached the file together with the next one, which reused its number. The
    /// last record of such a run is the one the session went on with. Returns the backup the repair will be saved to.
    /// </summary>
    private (List<ParsedLine> Kept, string? Backup) DropFailedWrites(List<ParsedLine> lines)
    {
        var kept = new List<ParsedLine>(lines.Count);
        string? backup = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Record.Seq == 0 || i + 1 == lines.Count || lines[i + 1].Record.Seq != line.Record.Seq)
            {
                kept.Add(line);
                continue;
            }

            var problem = $"Line {line.Number} repeats sequence {line.Record.Seq} (a write that failed in an older version)";
            if (IsReadOnly)
            {
                _warnings.Add($"{problem} and has been skipped.");
                continue;
            }

            backup ??= BackupPath();
            _warnings.Add($"{problem} and has been removed; the original journal is saved as {Path.GetFileName(backup)}.");
        }

        return (kept, backup);
    }

    // journal.jsonl.bak, else journal.jsonl.<time>.bak, else journal.jsonl.<time>-2.bak, ...: an earlier backup is never replaced.
    private string BackupPath()
    {
        var backup = FilePath + ".bak";
        var stamped = $"{FilePath}.{Now().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        for (var n = 1; File.Exists(backup); n++)
        {
            backup = n == 1 ? stamped + ".bak" : $"{stamped}-{n}.bak";
        }

        return backup;
    }

    private void Replay(JournalRecord record, int lineNumber)
    {
        if (record.Seq <= _lastSeq && !(record.Seq == 0 && _lastSeq == 0))
        {
            throw new InvalidDataException($"{FilePath}: line {lineNumber} has sequence {record.Seq} after {_lastSeq}.");
        }

        _lastSeq = Math.Max(_lastSeq, record.Seq);
        switch (record.Type)
        {
            case JournalRecordType.Header:
                if (record.Format is { } format && !format.StartsWith("scumstudio.journal/", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"{FilePath}: unknown journal format '{format}'.");
                }

                break;
            case JournalRecordType.Edit:
                if (record.Edit is null)
                {
                    throw new InvalidDataException($"{FilePath}: line {lineNumber} is an edit without an operation.");
                }

                ApplyEdit(new JournalEntry(record.Seq, record.At, record.Edit));
                break;
            case JournalRecordType.Undo:
                if (_applied.Count == 0 || _applied[^1].Seq != record.Ref)
                {
                    throw new InvalidDataException($"{FilePath}: line {lineNumber} undoes #{record.Ref}, which is not the last applied edit.");
                }

                _redo.Add(_applied[^1]);
                _applied.RemoveAt(_applied.Count - 1);
                break;
            case JournalRecordType.Redo:
                if (_redo.Count == 0 || _redo[^1].Seq != record.Ref)
                {
                    throw new InvalidDataException($"{FilePath}: line {lineNumber} redoes #{record.Ref}, which is not the last undone edit.");
                }

                _applied.Add(_redo[^1]);
                _redo.RemoveAt(_redo.Count - 1);
                break;
            default:
                throw new InvalidDataException($"{FilePath}: line {lineNumber} has an unknown record type.");
        }
    }

    private void ApplyEdit(JournalEntry entry)
    {
        foreach (var undone in _redo)
        {
            _discarded.Add(undone.Seq);
        }

        _redo.Clear();
        _applied.Add(entry);
        _allEdits.Add(entry);
    }

    // Unbuffered: a record that failed is never held in memory, so neither a later write nor Dispose can put it on disk.
    private static FileStream OpenWriter(string path) => new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSize: 0);

    private void OpenStream(long? truncateTo)
    {
        _stream = _openWriter(FilePath);
        if (truncateTo is { } length)
        {
            _stream.SetLength(length);
        }

        _stream.Seek(0, SeekOrigin.End);
    }

    private void Write(JournalRecord record)
    {
        WriteBytes(Utf8NoBom.GetBytes(SerializeRecord(record) + "\n"));
        _lastSeq = record.Seq;
    }

    /// <summary>Appends <paramref name="bytes"/> durably, or leaves the file as it was and throws.</summary>
    private void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (_faulted)
        {
            throw new IOException(FaultedMessage);
        }

        var stream = _stream ?? throw new ObjectDisposedException(nameof(Journal));
        var start = stream.Length;
        try
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            if (stream.Length != start + bytes.Length)
            {
                throw new IOException($"{FilePath}: a write of {bytes.Length} bytes at {start} left the file at {stream.Length} bytes.");
            }
        }
        catch
        {
            RollBack(stream, start);
            throw;
        }
    }

    private void RollBack(FileStream stream, long start)
    {
        try
        {
            stream.SetLength(start);
            stream.Position = start;
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The disk is gone: appending after what is left of the failed record would corrupt the journal, so close it.
            // The next open drops a torn last line.
            _faulted = true;
            _stream = null;
            stream.Dispose();
        }
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException($"{FilePath} is open read-only.");
        }
    }

    private DateTimeOffset Now() => _clock.GetUtcNow();

    private readonly record struct ParsedLine(int Number, string Text, JournalRecord Record);
}
