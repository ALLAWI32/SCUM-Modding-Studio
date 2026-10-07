using System.Text;
using System.Text.Json;
using ScumStudio.Core.IO;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Serialization;

namespace ScumStudio.Level.Projects;

/// <summary>
/// A ScumStudio project: a folder (conventionally <c>&lt;Name&gt;.ssproj</c>) holding <c>project.json</c> (name, game
/// build, sources), <c>journal.jsonl</c> (every edit, see <see cref="Journal"/>) and <c>notes.md</c>. The current world
/// edits (<see cref="State"/>) are the journal's applied operations; undo/redo move the journal's pointer and update the
/// state with inverse operations. The journal is written through on every change; <see cref="Save"/> writes the manifest
/// and notes.
/// </summary>
public sealed class Project : IDisposable
{
    /// <summary>Conventional extension of a project folder.</summary>
    public const string FolderExtension = ".ssproj";

    /// <summary>Manifest file name.</summary>
    public const string ManifestFileName = "project.json";

    /// <summary>Journal file name.</summary>
    public const string JournalFileName = "journal.jsonl";

    /// <summary>Notes file name.</summary>
    public const string NotesFileName = "notes.md";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TimeProvider _clock;
    private string _savedNotes;

    private Project(string directory, ProjectManifest manifest, Journal journal, EditState state, string notes, TimeProvider clock)
    {
        DirectoryPath = directory;
        Manifest = manifest;
        Journal = journal;
        State = state;
        Notes = notes;
        _savedNotes = notes;
        _clock = clock;
    }

    /// <summary>Full path of the project folder.</summary>
    public string DirectoryPath { get; }

    /// <summary>The manifest (update with <see cref="UpdateManifest"/>, persist with <see cref="Save"/>).</summary>
    public ProjectManifest Manifest { get; private set; }

    /// <summary>The edit journal.</summary>
    public Journal Journal { get; }

    /// <summary>Net effect of the applied edits.</summary>
    public EditState State { get; }

    /// <summary>Free-form notes (Markdown), saved to <c>notes.md</c> by <see cref="Save"/>.</summary>
    public string Notes { get; set; }

    /// <summary>
    /// Journal entries (or edits inside a bulk entry) that did not replay when the project was opened and were left out,
    /// one line each; empty when every edit applied. Undoing back past such an entry fails.
    /// </summary>
    public IReadOnlyList<string> ReplayProblems { get; init; } = [];

    /// <summary>Full path of <c>project.json</c>.</summary>
    public string ManifestPath => Path.Combine(DirectoryPath, ManifestFileName);

    /// <summary>Full path of <c>notes.md</c>.</summary>
    public string NotesPath => Path.Combine(DirectoryPath, NotesFileName);

    /// <summary>The history listing (every edit with timestamp, summary, target and status).</summary>
    public IReadOnlyList<HistoryItem> History => Journal.History;

    /// <summary>True when there is an edit to undo.</summary>
    public bool CanUndo => Journal.CanUndo;

    /// <summary>True when there is an edit to redo.</summary>
    public bool CanRedo => Journal.CanRedo;

    /// <summary>
    /// The pending export set: distinct level packages touched by the applied edits, sorted. Each is rewritten in full
    /// when the mod paks are exported.
    /// </summary>
    public IReadOnlyList<string> PendingExportSet =>
        Journal.Applied.SelectMany(e => e.Op.GetTouchedLevels())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Vehicle/item packages the mod will contain (net state: clones that still exist and packages with value
    /// overrides), sorted. Built by the exporter together with the levels.
    /// </summary>
    public IReadOnlyList<string> PendingAssetSet => State.ChangedAssets;

    /// <summary>
    /// Creates a new project in <paramref name="directory"/> (created when missing; must not already hold a project).
    /// </summary>
    /// <exception cref="IOException">The folder already contains a <c>project.json</c>.</exception>
    public static Project Create(string directory, string name, string? gameBuild = null, IEnumerable<ProjectSource>? sources = null, TimeProvider? clock = null)
    {
        var (full, manifest, notes, time) = PrepareCreate(directory, name, gameBuild, sources, clock);
        AtomicFile.WriteAllBytes(Path.Combine(full, ManifestFileName), SerializeManifest(manifest));
        File.WriteAllText(Path.Combine(full, NotesFileName), notes, Utf8NoBom);
        var journal = Journal.Open(Path.Combine(full, JournalFileName), time);
        return new Project(full, manifest, journal, new EditState(), notes, time);
    }

    /// <summary>Asynchronously creates a new project (see <see cref="Create"/>).</summary>
    /// <exception cref="IOException">The folder already contains a <c>project.json</c>.</exception>
    public static async Task<Project> CreateAsync(
        string directory, string name, string? gameBuild = null, IEnumerable<ProjectSource>? sources = null, TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        var (full, manifest, notes, time) = PrepareCreate(directory, name, gameBuild, sources, clock);
        await AtomicFile.WriteAllBytesAsync(Path.Combine(full, ManifestFileName), SerializeManifest(manifest), cancellationToken: cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(full, NotesFileName), notes, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        var journal = await Journal.OpenAsync(Path.Combine(full, JournalFileName), time, cancellationToken).ConfigureAwait(false);
        return new Project(full, manifest, journal, new EditState(), notes, time);
    }

    /// <summary>Opens the project in <paramref name="path"/> (anything <see cref="Find"/> accepts).</summary>
    /// <exception cref="FileNotFoundException">No <c>project.json</c>.</exception>
    /// <exception cref="InvalidDataException">The manifest or journal is invalid, or the journal does not replay.</exception>
    public static Project Open(string path, TimeProvider? clock = null)
    {
        var directory = ResolveDirectory(path);
        var manifest = DeserializeManifest(File.ReadAllBytes(Path.Combine(directory, ManifestFileName)), directory);
        var notesPath = Path.Combine(directory, NotesFileName);
        var notes = File.Exists(notesPath) ? File.ReadAllText(notesPath, Utf8NoBom) : string.Empty;
        var time = clock ?? TimeProvider.System;
        var journal = Journal.Open(Path.Combine(directory, JournalFileName), time);
        return Finish(directory, manifest, journal, notes, time);
    }

    /// <summary>Asynchronously opens a project (see <see cref="Open"/>).</summary>
    /// <exception cref="FileNotFoundException">No <c>project.json</c>.</exception>
    /// <exception cref="InvalidDataException">The manifest or journal is invalid, or the journal does not replay.</exception>
    public static async Task<Project> OpenAsync(string path, TimeProvider? clock = null, CancellationToken cancellationToken = default)
    {
        var directory = ResolveDirectory(path);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, ManifestFileName), cancellationToken).ConfigureAwait(false);
        var manifest = DeserializeManifest(bytes, directory);
        var notesPath = Path.Combine(directory, NotesFileName);
        var notes = File.Exists(notesPath) ? await File.ReadAllTextAsync(notesPath, Utf8NoBom, cancellationToken).ConfigureAwait(false) : string.Empty;
        var time = clock ?? TimeProvider.System;
        var journal = await Journal.OpenAsync(Path.Combine(directory, JournalFileName), time, cancellationToken).ConfigureAwait(false);
        return Finish(directory, manifest, journal, notes, time);
    }

    /// <summary>True when <paramref name="directory"/> contains a <c>project.json</c>.</summary>
    public static bool Exists(string directory) =>
        !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(Path.GetFullPath(directory), ManifestFileName));

    /// <summary>
    /// The project folder <paramref name="path"/> points at, or null: the folder itself, the folder of a file inside it
    /// (<c>project.json</c>, <c>journal.jsonl</c>, ...) or the only project directly inside it (a parent such as
    /// <c>Documents\ScumStudio Projects</c> picked in a folder dialog).
    /// </summary>
    public static string? Find(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        if (File.Exists(full))
        {
            full = Path.GetDirectoryName(full)!;
        }

        if (Exists(full))
        {
            return full;
        }

        return Directory.Exists(full) && Directory.GetDirectories(full, "*" + FolderExtension).Where(Exists).ToArray() is [var only] ? only : null;
    }

    /// <summary>Changes manifest fields (name, game build, sources); call <see cref="Save"/> to persist.</summary>
    public void UpdateManifest(Func<ProjectManifest, ProjectManifest> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var updated = update(Manifest) ?? throw new InvalidOperationException("The manifest update returned null.");
        Manifest = updated with { Format = ProjectManifest.FormatId, Created = Manifest.Created };
    }

    /// <summary>Writes <c>project.json</c> (atomically, with a new modification time) and <c>notes.md</c> when changed.</summary>
    public void Save()
    {
        Manifest = Manifest with { Modified = _clock.GetUtcNow() };
        AtomicFile.WriteAllBytes(ManifestPath, SerializeManifest(Manifest));
        if (!string.Equals(Notes, _savedNotes, StringComparison.Ordinal))
        {
            AtomicFile.WriteAllBytes(NotesPath, Utf8NoBom.GetBytes(Notes));
            _savedNotes = Notes;
        }
    }

    /// <summary>Asynchronously saves (see <see cref="Save"/>).</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Manifest = Manifest with { Modified = _clock.GetUtcNow() };
        await AtomicFile.WriteAllBytesAsync(ManifestPath, SerializeManifest(Manifest), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Notes, _savedNotes, StringComparison.Ordinal))
        {
            await AtomicFile.WriteAllBytesAsync(NotesPath, Utf8NoBom.GetBytes(Notes), cancellationToken: cancellationToken).ConfigureAwait(false);
            _savedNotes = Notes;
        }
    }

    /// <summary>Validates <paramref name="op"/> against <see cref="State"/>, journals it and applies it.</summary>
    /// <exception cref="InvalidOperationException">The operation is not valid now (nothing is written).</exception>
    public JournalEntry Apply(EditOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        if (State.Validate(op) is { } error)
        {
            throw new InvalidOperationException($"Cannot apply '{op.Describe()}': {error}");
        }

        var entry = Journal.Append(op);
        State.Apply(op);
        return entry;
    }

    /// <summary>Undoes the last applied edit; returns it (null when there is nothing to undo).</summary>
    /// <exception cref="InvalidOperationException">The in-memory state does not accept the inverse (nothing is written).</exception>
    public JournalEntry? Undo()
    {
        var applied = Journal.Applied;
        if (applied.Count == 0)
        {
            return null;
        }

        var inverse = applied[^1].Op.Inverse();
        if (State.Validate(inverse) is { } error)
        {
            throw new InvalidOperationException($"Cannot undo '{applied[^1].Op.Describe()}': {error}");
        }

        var entry = Journal.Undo()!;
        State.Apply(inverse);
        return entry;
    }

    /// <summary>Redoes the last undone edit; returns it (null when there is nothing to redo).</summary>
    /// <exception cref="InvalidOperationException">The in-memory state does not accept the edit (nothing is written).</exception>
    public JournalEntry? Redo()
    {
        var redo = Journal.RedoStack;
        if (redo.Count == 0)
        {
            return null;
        }

        var op = redo[^1].Op;
        if (State.Validate(op) is { } error)
        {
            throw new InvalidOperationException($"Cannot redo '{op.Describe()}': {error}");
        }

        var entry = Journal.Redo()!;
        State.Apply(op);
        return entry;
    }

    /// <inheritdoc />
    public void Dispose() => Journal.Dispose();

    /// <summary>Serialises a manifest as indented UTF-8 JSON.</summary>
    public static byte[] SerializeManifest(ProjectManifest manifest) =>
        Utf8NoBom.GetBytes(JsonSerializer.Serialize(manifest, LevelJson.Indented) + "\n");

    private static (string Directory, ProjectManifest Manifest, string Notes, TimeProvider Clock) PrepareCreate(
        string directory, string name, string? gameBuild, IEnumerable<ProjectSource>? sources, TimeProvider? clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var full = Path.GetFullPath(directory);
        if (File.Exists(Path.Combine(full, ManifestFileName)))
        {
            throw new IOException($"{full} already contains a project.");
        }

        Directory.CreateDirectory(full);
        var time = clock ?? TimeProvider.System;
        var now = time.GetUtcNow();
        var manifest = new ProjectManifest
        {
            Name = name.Trim(),
            GameBuild = string.IsNullOrWhiteSpace(gameBuild) ? "unknown" : gameBuild.Trim(),
            Created = now,
            Modified = now,
            Sources = sources?.ToList() ?? [],
        };
        var notes = $"# {manifest.Name}\n\nNotes for this ScumStudio project.\n";
        return (full, manifest, notes, time);
    }

    private static Project Finish(string directory, ProjectManifest manifest, Journal journal, string notes, TimeProvider clock)
    {
        try
        {
            var state = new EditState();
            var problems = new List<string>();
            foreach (var entry in journal.Applied)
            {
                if (state.Validate(entry.Op) is not { } error)
                {
                    state.Apply(entry.Op);
                    continue;
                }

                // An edit that no longer applies (an older version journaled a bulk delete that stopped halfway, and the
                // edits after it were made against that half) is left out, not the whole project: what still applies does.
                var label = $"edit #{entry.Seq} ('{entry.Op.Describe()}')";
                if (entry.Op is not BatchOp batch)
                {
                    problems.Add($"{label} does not replay: {error}");
                    continue;
                }

                var skipped = 0;
                foreach (var child in batch.Ops)
                {
                    if (state.Validate(child) is null)
                    {
                        state.Apply(child);
                    }
                    else
                    {
                        skipped++;
                    }
                }

                problems.Add($"{label}: {skipped} of {batch.Ops.Count} edits do not replay ({error})");
            }

            return new Project(directory, manifest, journal, state, notes, clock) { ReplayProblems = problems };
        }
        catch
        {
            journal.Dispose();
            throw;
        }
    }

    private static string ResolveDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        return Find(full) ?? throw new FileNotFoundException($"No {ManifestFileName} in {full}.", Path.Combine(full, ManifestFileName));
    }

    private static ProjectManifest DeserializeManifest(byte[] bytes, string directory)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(bytes, LevelJson.Indented)
                           ?? throw new InvalidDataException("project.json is empty.");
            if (!manifest.Format.StartsWith("scumstudio.project/", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unknown project format '{manifest.Format}'.");
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Path.Combine(directory, ManifestFileName)} is not valid: {ex.Message}", ex);
        }
    }
}
