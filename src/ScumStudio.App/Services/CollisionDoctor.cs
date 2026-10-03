using System.Text.Json;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;

namespace ScumStudio.App.Services;

/// <summary>
/// The app side of the collision check (owner: "if something has no collision the program should notice and replace it
/// itself, for everyone"): before an export it reads the game's logs (this PC's SCUM client and the configured server)
/// written since the project's last export, finds the shaped pieces the game left without collision and marks them to be
/// exported straight, which always collides (the game's own collision for the mesh). The marks live in the project folder
/// (<c>collision-check.json</c>); a mark is dropped when the piece's shape is changed again, so a new shape is tried bent.
/// </summary>
public static class CollisionDoctor
{
    /// <summary>File in the project folder holding the last export time and the marked pieces.</summary>
    public const string FileName = "collision-check.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// The pieces to export straight: those marked before (shape unchanged) plus those the game's logs report without
    /// collision since the last export (returned again in <c>Found</c> to tell the owner).
    /// </summary>
    public static (IReadOnlyList<ActorRef> Straight, IReadOnlyList<ActorRef> Found) Check(Project project, IEnumerable<string> logFiles)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(logFiles);
        var record = Load(project);
        var state = project.State;
        var marks = (record.Marks ?? []).Where(m => ShapeOf(state, new ActorRef(m.Level, m.Actor)) == m.Shape).ToList();
        var found = new List<ActorRef>();
        if (record.LastExportUtc is { } since)
        {
            foreach (var file in logFiles)
            {
                foreach (var failure in CollisionCheck.FromGameLog(ReadShared(file), since))
                {
                    if (CollisionCheck.ActorOf(failure, state) is { } actor
                        && !marks.Any(m => ActorRef.Comparer.Equals(new ActorRef(m.Level, m.Actor), actor)))
                    {
                        marks.Add(new Mark(actor.Level, actor.Actor, ShapeOf(state, actor)));
                        found.Add(actor);
                    }
                }
            }
        }

        Save(project, record with { Marks = marks });
        return (marks.Select(m => new ActorRef(m.Level, m.Actor)).ToList(), found);
    }

    /// <summary>Remembers that the project was exported now: later game log lines are about this export.</summary>
    public static void Exported(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Save(project, Load(project) with { LastExportUtc = DateTime.UtcNow });
    }

    /// <summary>
    /// The game logs to read: this PC's SCUM client log and its backups, and the dedicated server's log next to the
    /// configured server Paks folder (<c>SCUM\Content\Paks</c> → <c>SCUM\Saved\Logs</c>).
    /// </summary>
    public static IEnumerable<string> GameLogs(string? serverPaksFolder)
    {
        var client = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCUM", "Saved", "Logs");
        var folders = new List<string> { client };
        if (!string.IsNullOrWhiteSpace(serverPaksFolder))
        {
            folders.Add(Path.GetFullPath(Path.Combine(serverPaksFolder, "..", "..", "Saved", "Logs")));
        }

        return folders.Where(Directory.Exists).SelectMany(f => Directory.EnumerateFiles(f, "SCUM*.log"));
    }

    /// <summary>The piece's shape as text, to notice when it is changed after being marked.</summary>
    private static string ShapeOf(EditState state, ActorRef actor) => JsonSerializer.Serialize(state.GetBendValue(actor));

    private static IEnumerable<string> ReadShared(string file)
    {
        List<string> lines = [];
        try
        {
            // The game keeps its log open while it runs.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return lines;
    }

    private static Record Load(Project project)
    {
        var path = Path.Combine(project.DirectoryPath, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Record>(File.ReadAllText(path), Json) ?? new Record() : new Record();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Record();
        }
    }

    private static void Save(Project project, Record record)
    {
        try
        {
            File.WriteAllText(Path.Combine(project.DirectoryPath, FileName), JsonSerializer.Serialize(record, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only project folder: the check still works for this export, it just is not remembered.
        }
    }

    /// <summary>What is kept between exports.</summary>
    /// <param name="LastExportUtc">When the project was last exported.</param>
    /// <param name="Marks">Pieces to export straight.</param>
    public sealed record Record(DateTime? LastExportUtc = null, List<Mark>? Marks = null);

    /// <summary>A piece the game left without collision, with the shape it had then (a new shape is tried bent again).</summary>
    /// <param name="Level">Level package path.</param>
    /// <param name="Actor">Actor name.</param>
    /// <param name="Shape">The piece's shape when it was marked (JSON).</param>
    public sealed record Mark(string Level, string Actor, string Shape);
}
