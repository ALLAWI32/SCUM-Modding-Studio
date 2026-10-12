using System.Text;
using System.Text.Json;
using ScumStudio.Core.IO;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Serialization;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.Level.Projects;

/// <summary>What getting a shared mod would do to the open project, worked out before anything is written.</summary>
/// <param name="Batch">The mod's edits as one step, renamed where the project already uses a name.</param>
/// <param name="Counts">Edits per group (<c>A_0</c>, <c>island</c>, <c>assets</c>).</param>
/// <param name="Renamed">Objects, copied trees and traders given another name or index (old, new).</param>
/// <param name="ConflictDetail">Why the edits do not apply to the project (it already changed the same objects), or null.</param>
/// <param name="GameBuildDiffers">True when both game builds are known and differ.</param>
/// <param name="AuthorBuild">The game build the mod was made on.</param>
/// <param name="YourBuild">The project's game build.</param>
/// <param name="CraftablesAdded">Craftables the project gets.</param>
/// <param name="CraftablesSkipped">Craftables left out: the project has one of that name.</param>
/// <param name="EconomySections">Traders whose economy section the mod sets (new names).</param>
public sealed record ShareImportPlan(
    BatchOp Batch, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<(string From, string To)> Renamed, string? ConflictDetail,
    bool GameBuildDiffers, string AuthorBuild, string YourBuild, IReadOnlyList<string> CraftablesAdded, IReadOnlyList<string> CraftablesSkipped,
    IReadOnlyList<string> EconomySections)
{
    /// <summary>Placed traders' new names by their name in the mod (their economy sections follow).</summary>
    public IReadOnlyDictionary<string, string> TraderNames { get; init; } = new Dictionary<string, string>();
}

/// <summary>A shared mod's economy section as the project had it before the mod set it.</summary>
/// <param name="Trader">The section (trader name).</param>
/// <param name="Entries">Its entries before, or null when the project had no such section.</param>
public sealed record EconomySectionBefore(string Trader, IReadOnlyList<TradeableOverride>? Entries);

/// <summary>A shared mod got into a project (<c>&lt;project&gt;/shared/&lt;id&gt;.json</c>): what Remove takes back.</summary>
public sealed record ShareInstall
{
    /// <summary>The mod's id in the gallery.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The mod's name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Its maker.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Journal sequence of the batch that applied its edits.</summary>
    public long BatchSeq { get; init; }

    /// <summary>When it was got (UTC).</summary>
    public DateTimeOffset Installed { get; init; }

    /// <summary>Craftables it added to <c>craftables.json</c>.</summary>
    public IReadOnlyList<string> CraftablesAdded { get; init; } = [];

    /// <summary>The economy sections it set, as they were before.</summary>
    public IReadOnlyList<EconomySectionBefore> EconomyBefore { get; init; } = [];
}

/// <summary>
/// Gets a shared mod into the open project as one undoable step (owner: "press Get, test it, remove it"): created objects,
/// copied trees and placed traders whose names the project already uses are renamed, the edits are checked against the
/// project's own before anything is written, the craftables and economy sections are merged, and a record of what was
/// added lets <see cref="Remove"/> take all of it back.
/// </summary>
public static class ShareImport
{
    /// <summary>Folder of the install records inside a project.</summary>
    public const string FolderName = "shared";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Works out what getting <paramref name="content"/> would do; writes nothing.</summary>
    /// <param name="project">The open project.</param>
    /// <param name="content">The mod (see <see cref="ShareFile.Read"/>).</param>
    /// <param name="knownTraders">The game's traders (name, type), so a placed trader never takes a stock one's name.</param>
    /// <param name="economy">The project's economy as the app holds it: its section names count as taken.</param>
    /// <param name="title">The step's name in the history (default: the mod's name).</param>
    /// <exception cref="ShareException"><see cref="ShareProblem.UnknownEdit"/>: an edit type this version cannot rename.</exception>
    public static ShareImportPlan Plan(
        Project project, ShareContent content, IEnumerable<(string Name, string Type)>? knownTraders = null, EconomyOverride? economy = null, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(content);
        var state = project.State;
        var leaves = Leaves(content.Edits).ToList();
        var renamed = new List<(string From, string To)>();

        // Created actors: a name the project added (or another of the mod's) gets the next free number, as UniqueActorName does.
        var actors = new Dictionary<ActorRef, ActorRef>(ActorRef.Comparer);
        var created = leaves.Select(CreatedBy).OfType<ActorRef>().ToList();
        var inMod = created.ToHashSet(ActorRef.Comparer);
        var given = new HashSet<ActorRef>(ActorRef.Comparer);
        foreach (var actor in created)
        {
            var to = actor;
            if (state.IsAdded(actor) || given.Contains(actor))
            {
                var stem = actor.Actor.TrimEnd("0123456789".ToCharArray()) is { Length: > 0 } s ? s : actor.Actor;
                for (var i = 2; state.IsAdded(to) || given.Contains(to) || inMod.Contains(to); i++)
                {
                    to = actor with { Actor = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{stem}{i}") };
                }

                actors[actor] = to;
                renamed.Add((actor.ToString(), to.ToString()));
            }

            given.Add(to);
        }

        // Copied trees: a taken index moves to the next free one of the same component.
        InstanceRef Owner(InstanceRef i) => actors.TryGetValue(i.ActorRef, out var o) ? i with { Level = o.Level, Actor = o.Actor } : i;
        var instances = new Dictionary<InstanceRef, InstanceRef>(InstanceRef.Comparer);
        var adds = leaves.OfType<AddInstanceOp>().ToList();
        var addsInMod = adds.Select(a => Owner(a.Target)).ToHashSet(InstanceRef.Comparer);
        var givenInstances = new HashSet<InstanceRef>(InstanceRef.Comparer);
        foreach (var add in adds)
        {
            var to = Owner(add.Target);
            if (state.IsAdded(to) || givenInstances.Contains(to))
            {
                while (state.IsAdded(to) || givenInstances.Contains(to) || addsInMod.Contains(to))
                {
                    to = to with { Index = to.Index + 1 };
                }

                instances[add.Target] = to;
                renamed.Add((add.Target.ToString(), to.ToString()));
            }

            givenInstances.Add(to);
        }

        // Placed traders: a taken name becomes the next <cell>_<Suffix>_<n>, its economy section with it.
        var traders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var existing = state.AddedActors.Values.OfType<AddBlueprintActorOp>().Select(b => b.Trader).OfType<TraderPost>().Where(t => t.Name.Length > 0)
            .Select(t => (t.Name, t.Type)).Concat(knownTraders ?? []).Concat((economy?.Traders ?? []).Select(n => (Name: n, Type: string.Empty))).ToList();
        var placed = leaves.OfType<AddBlueprintActorOp>().Where(b => b.Trader is { Name.Length: > 0 }).ToList();
        var placedNames = placed.Select(b => (b.Trader!.Name, b.Trader.Type)).ToList();
        foreach (var bp in placed)
        {
            var trader = bp.Trader!;
            var to = trader.Name;
            if (existing.Any(e => string.Equals(e.Name, trader.Name, StringComparison.OrdinalIgnoreCase)))
            {
                // One entry per name, so a name both sides use is counted once in the cell's number.
                var all = existing.Concat(placedNames).GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(e => e.Type.Length).First()).ToList();
                var cell = TraderPosts.CellOf(trader.Name) ?? JournalSlice.SectorOf(bp.Level);
                to = cell is not null ? TraderPosts.DefaultName(cell, trader.Type, all) : Numbered(trader.Name, all.Select(e => e.Name));
                traders[trader.Name] = to;
                renamed.Add((trader.Name, to));
            }

            existing.Add((to, trader.Type));
        }

        BatchOp batch;
        try
        {
            batch = new BatchOp(string.IsNullOrWhiteSpace(title) ? content.Manifest.Name : title,
                content.Edits.Ops.Select(o => EditOpRemap.Rename(o, actors, instances, traders)).ToList());
        }
        catch (NotSupportedException ex)
        {
            throw new ShareException(ShareProblem.UnknownEdit, ex.Message);
        }

        var (craftablesAdded, craftablesSkipped) = MergeCraftables(project, content);
        var sections = SharedEconomy(content)?.Traders.Select(t => traders.GetValueOrDefault(t, t)).ToList() ?? [];
        var authorBuild = content.Manifest.GameBuild;
        var yourBuild = project.Manifest.GameBuild;
        return new ShareImportPlan(
            batch, JournalSlice.Count(batch.Ops), renamed, state.Validate(batch),
            IsKnown(authorBuild) && IsKnown(yourBuild) && !string.Equals(authorBuild.Trim(), yourBuild.Trim(), StringComparison.OrdinalIgnoreCase),
            authorBuild, yourBuild, craftablesAdded.Select(c => c.Name).ToList(), craftablesSkipped, sections)
        {
            TraderNames = traders,
        };
    }

    /// <summary>
    /// Applies the plan as one step of the history, merges the mod's craftables into <c>craftables.json</c> and its economy
    /// sections into <paramref name="liveEconomy"/> (the app's copy: save it afterwards), and writes the install record.
    /// </summary>
    /// <param name="project">The project the plan was made for.</param>
    /// <param name="plan">See <see cref="Plan"/>.</param>
    /// <param name="id">The mod's gallery id (letters, digits, '-' and '_').</param>
    /// <param name="content">The mod.</param>
    /// <param name="liveEconomy">The project's economy as the app holds it.</param>
    /// <exception cref="ShareException"><see cref="ShareProblem.Conflicts"/>: the edits no longer apply (nothing is written).</exception>
    public static ShareInstall Apply(Project project, ShareImportPlan plan, string id, ShareContent content, EconomyOverride liveEconomy)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(liveEconomy);
        if (!IsSafeId(id))
        {
            throw new ArgumentException("A mod id is 1-64 letters, digits, '-' or '_'.", nameof(id));
        }

        if ((plan.ConflictDetail ?? project.State.Validate(plan.Batch)) is { } conflict)
        {
            throw new ShareException(ShareProblem.Conflicts, conflict);
        }

        var shared = SharedEconomy(content);
        var sections = shared?.Traders.Select(t => (From: t, To: plan.TraderNames.GetValueOrDefault(t, t))).ToList() ?? [];
        var before = sections.Select(s => s.To).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => new EconomySectionBefore(t, liveEconomy.HasSection(t) ? liveEconomy.Entries(t).ToList() : null)).ToList();

        // Applied as the author's journal has it, not through the app's grass default for new objects: it already carries
        // the author's grass choices.
        var entry = project.Apply(plan.Batch);
        try
        {
            var (craftables, _) = MergeCraftables(project, content);
            if (craftables.Count > 0)
            {
                var local = CraftablesFile.Load(project.DirectoryPath);
                (local with { Items = local.Items.Concat(craftables).ToList() }).Save(project.DirectoryPath);
            }

            foreach (var (from, to) in sections)
            {
                liveEconomy.EnsureSection(to);
                foreach (var value in shared!.Entries(from))
                {
                    liveEconomy.Set(to, value, keep: true);
                }
            }

            var install = new ShareInstall
            {
                Id = id,
                Name = content.Manifest.Name,
                Author = content.Manifest.Author,
                BatchSeq = entry.Seq,
                Installed = entry.At,
                CraftablesAdded = craftables.Select(c => c.Name).ToList(),
                EconomyBefore = before,
            };
            Directory.CreateDirectory(Path.Combine(project.DirectoryPath, FolderName));
            AtomicFile.WriteAllBytes(RecordPath(project, id), Utf8NoBom.GetBytes(JsonSerializer.Serialize(install, LevelJson.Indented) + "\n"));
            return install;
        }
        catch
        {
            Restore(liveEconomy, before);
            if (project.Journal.Applied is [.., var last] && last.Seq == entry.Seq)
            {
                project.Undo();
            }

            throw;
        }
    }

    /// <summary>The shared mods got into <paramref name="project"/>, oldest first (unreadable records are skipped).</summary>
    public static IReadOnlyList<ShareInstall> Installed(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var folder = Path.Combine(project.DirectoryPath, FolderName);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var result = new List<ShareInstall>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<ShareInstall>(File.ReadAllBytes(file), LevelJson.Indented) is { } install
                    && IsSafeId(install.Id) && string.Equals(install.Id, Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(install);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A damaged record lists nothing; its edits stay in the history, where Ctrl+Z still takes them back.
            }
        }

        return result.OrderBy(i => i.Installed).ThenBy(i => i.BatchSeq).ToList();
    }

    /// <summary>
    /// Takes a shared mod back: its edits by a new step that undoes them (when Ctrl+Z has not already), the craftables it
    /// added and the economy sections as they were; then deletes the record.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="install">See <see cref="Installed"/>.</param>
    /// <param name="liveEconomy">The project's economy as the app holds it (save it afterwards).</param>
    /// <param name="title">The step's name in the history (default: the mod's name).</param>
    /// <exception cref="ShareException"><see cref="ShareProblem.Conflicts"/>: a later edit changed what the mod made (nothing is changed).</exception>
    public static void Remove(Project project, ShareInstall install, EconomyOverride liveEconomy, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(liveEconomy);
        if (!IsSafeId(install.Id))
        {
            throw new ArgumentException("Not a shared mod's record.", nameof(install));
        }

        if (project.Journal.Applied.FirstOrDefault(e => e.Seq == install.BatchSeq) is { } applied)
        {
            var name = string.IsNullOrWhiteSpace(title) ? install.Name : title;
            var inverse = applied.Op.Inverse() is BatchOp batch ? batch with { Title = name } : new BatchOp(name, [applied.Op.Inverse()]);
            if (project.State.Validate(inverse) is { } conflict)
            {
                throw new ShareException(ShareProblem.Conflicts, conflict);
            }

            project.Apply(inverse);
        }

        if (install.CraftablesAdded.Count > 0)
        {
            var local = CraftablesFile.Load(project.DirectoryPath);
            var added = install.CraftablesAdded.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (local.Items.Any(c => added.Contains(c.Name)))
            {
                (local with { Items = local.Items.Where(c => !added.Contains(c.Name)).ToList() }).Save(project.DirectoryPath);
            }
        }

        Restore(liveEconomy, install.EconomyBefore);
        File.Delete(RecordPath(project, install.Id));
    }

    private static string RecordPath(Project project, string id) => Path.Combine(project.DirectoryPath, FolderName, id + ".json");

    private static bool IsSafeId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    private static bool IsKnown(string? build) =>
        !string.IsNullOrWhiteSpace(build) && !string.Equals(build.Trim(), "unknown", StringComparison.OrdinalIgnoreCase);

    private static void Restore(EconomyOverride economy, IEnumerable<EconomySectionBefore> before)
    {
        foreach (var section in before)
        {
            if (section.Entries is { } entries)
            {
                economy.SetSection(section.Trader, entries);
            }
            else
            {
                economy.RemoveSection(section.Trader);
            }
        }
    }

    /// <summary>The mod's craftables the project can take (name and asset token free) and the names it cannot.</summary>
    private static (List<Craftable> Added, List<string> Skipped) MergeCraftables(Project project, ShareContent content)
    {
        var added = new List<Craftable>();
        var skipped = new List<string>();
        if (!content.Files.TryGetValue(CraftablesFile.FileName, out var json))
        {
            return (added, skipped);
        }

        var local = CraftablesFile.Load(project.DirectoryPath);
        var names = local.Items.Select(c => c.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tokens = local.Items.Select(c => c.Token).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var craftable in CraftablesFile.Parse(Utf8NoBom.GetString(json)).Items)
        {
            // Two names with one token would export as the same assets.
            if (craftable.Imported is null && !names.Contains(craftable.Name.Trim()) && !tokens.Contains(craftable.Token))
            {
                names.Add(craftable.Name.Trim());
                tokens.Add(craftable.Token);
                added.Add(craftable);
            }
            else
            {
                skipped.Add(craftable.Name);
            }
        }

        return (added, skipped);
    }

    private static EconomyOverride? SharedEconomy(ShareContent content) =>
        content.Files.TryGetValue(EconomyOverride.FileName, out var json) ? EconomyOverride.Parse(Utf8NoBom.GetString(json)) : null;

    private static string Numbered(string name, IEnumerable<string> taken)
    {
        var names = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 2;
        while (names.Contains($"{name}_{n}"))
        {
            n++;
        }

        return $"{name}_{n}";
    }

    private static IEnumerable<EditOp> Leaves(EditOp op) => op is BatchOp batch ? batch.Ops.SelectMany(Leaves) : [op];

    private static ActorRef? CreatedBy(EditOp op) => op switch
    {
        AddStaticMeshActorOp add => add.Created,
        AddBlueprintActorOp bp => bp.Created,
        DuplicateActorOp dup => dup.Created,
        _ => null,
    };
}
