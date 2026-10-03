using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// <c>edit</c> lines carry an <see cref="EditOp"/>, <c>undo</c>/<c>redo</c> lines reference an edit. Nothing is ever
/// rewritten, so the file is a complete audit trail; the current state (the applied stack and the redo stack) is
/// recomputed on open. A new edit after undos discards the redo stack (the undone edits stay in the file as
/// <see cref="HistoryStatus.Discarded"/>).
/// </summary>
/// <remarks>
/// A line torn by a crash at the very end of the file is truncated on open (and reported in <see cref="Warnings"/>); an
/// unreadable line elsewhere is an error. One process writes at a time: the file stays open for appending (others may read).
/// </remarks>
public sealed class Journal : IDisposable
{
    /// <summary>Format identifier written in the header line.</summary>
    public const string FormatId = "scumstudio.journal/1";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private readonly List<JournalEntry> _applied = [];
    private readonly List<JournalEntry> _redo = [];
    private readonly List<JournalEntry> _allEdits = [];
    private readonly HashSet<long> _discarded = [];
    private readonly List<string> _warnings = [];
    private FileStream? _stream;
    private long _lastSeq;

    private Journal(string filePath, TimeProvider clock)
    {
        FilePath = filePath;
        _clock = clock;
    }

    /// <summary>Full path of the journal file.</summary>
    public string FilePath { get; }

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

    /// <summary>Opens (or creates, with a header line) the journal at <paramref name="filePath"/>.</summary>
    /// <exception cref="InvalidDataException">A line is unreadable or the undo/redo records are inconsistent.</exception>
    public static Journal Open(string filePath, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var journal = new Journal(Path.GetFullPath(filePath), clock ?? TimeProvider.System);
        var bytes = File.Exists(journal.FilePath) ? File.ReadAllBytes(journal.FilePath) : null;
        journal.Initialize(bytes);
        return journal;
    }

    /// <summary>Asynchronously opens (or creates) the journal.</summary>
    /// <exception cref="InvalidDataException">A line is unreadable or the undo/redo records are inconsistent.</exception>
    public static async Task<Journal> OpenAsync(string filePath, TimeProvider? clock = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var journal = new Journal(Path.GetFullPath(filePath), clock ?? TimeProvider.System);
        var bytes = File.Exists(journal.FilePath)
            ? await File.ReadAllBytesAsync(journal.FilePath, cancellationToken).ConfigureAwait(false)
            : null;
        journal.Initialize(bytes);
        return journal;
    }

    /// <summary>Records a new edit (discarding the redo stack) and returns its entry.</summary>
    public JournalEntry Append(EditOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
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

    private void Initialize(byte[]? bytes)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        if (bytes is null || bytes.Length == 0)
        {
            OpenStream(truncateTo: null);
            Write(new JournalRecord { Seq = 0, At = Now(), Type = JournalRecordType.Header, Format = FormatId });
            return;
        }

        var text = Utf8NoBom.GetString(bytes);
        var offset = 0L;
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
            offset = 3;
        }

        var lines = text.Split('\n');
        long? truncateTo = null;
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

            JournalRecord record;
            try
            {
                record = DeserializeRecord(line);
            }
            catch (JsonException ex) when (isLast)
            {
                // A crash while appending leaves a partial last line without its newline: drop it.
                _warnings.Add($"Line {i + 1} was incomplete and has been removed ({ex.Message}).");
                truncateTo = offset;
                break;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{FilePath}: line {i + 1} is not a valid journal record: {ex.Message}", ex);
            }

            Replay(record, i + 1);
            offset += lineBytes;
        }

        OpenStream(truncateTo);
        if (truncateTo is null && bytes[^1] != (byte)'\n')
        {
            // The last record is complete but lacks its newline: terminate it before appending.
            _stream!.Write("\n"u8);
            _stream.Flush(flushToDisk: true);
        }
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

    private void OpenStream(long? truncateTo)
    {
        _stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (truncateTo is { } length)
        {
            _stream.SetLength(length);
        }

        _stream.Seek(0, SeekOrigin.End);
    }

    private void Write(JournalRecord record)
    {
        var stream = _stream ?? throw new ObjectDisposedException(nameof(Journal));
        var bytes = Utf8NoBom.GetBytes(SerializeRecord(record) + "\n");
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        _lastSeq = record.Seq;
    }

    private DateTimeOffset Now() => _clock.GetUtcNow();
}
