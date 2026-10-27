using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;

namespace ScumStudio.App.ViewModels;

/// <summary>One kind of armed NPC in the wizard's list: what the user reads instead of the game's encounter names.</summary>
/// <param name="Type">The NPC kind (its encounter, characters and zone settings).</param>
/// <param name="Title">The place it belongs to ("Military Base").</param>
/// <param name="Detail">Its threat level and characters ("Medium threat · Guard Lvl 3, Guard Lvl 4").</param>
public sealed record NpcKindOption(NpcGuardType Type, string Title, string Detail);

/// <summary>One character of a kind, for the "own copy" choice.</summary>
/// <param name="Name">The character Blueprint's name (<c>BP_Guard_Lvl_3</c>).</param>
/// <param name="Title">What the user reads (<c>Guard Lvl 3</c>).</param>
public sealed record NpcCharacterOption(string Name, string Title);

/// <summary>
/// "Place NPCs" (owner: NPC spawning is hard to follow): three plain questions instead of the zone, encounter, preset and
/// character weight lists. Who (a kind of armed NPC), how many posts, where (a click on the island map); the page then
/// writes what <see cref="NpcGuards"/> writes for a robot's route: a group of posts, an area over them with its own copy of
/// the zone settings that spawn only the chosen NPCs. One undo step, exported with Export mod. No weight is shown or asked.
/// </summary>
public sealed partial class SpawnsPageViewModel
{
    private AssetCatalog? _npcKindsFor;
    private int _npcKindsLoad;

    /// <summary>The kinds of armed NPC the game spawns (read in the background, once per game).</summary>
    [ObservableProperty]
    private IReadOnlyList<NpcKindOption> _npcKinds = [];

    /// <summary>The chosen kind.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NpcSummary), nameof(HasNpcKind))]
    [NotifyCanExecuteChangedFor(nameof(PickNpcPlaceCommand))]
    private NpcKindOption? _selectedNpcKind;

    /// <summary>On: the NPC is placed as an own copy of one character of the kind (<see cref="NpcCopies"/>): the stock NPCs stay as the game has them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NpcSummary))]
    [NotifyCanExecuteChangedFor(nameof(PickNpcPlaceCommand))]
    private bool _npcOwnCopy;

    /// <summary>The character to copy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NpcSummary))]
    [NotifyCanExecuteChangedFor(nameof(PickNpcPlaceCommand))]
    private NpcCharacterOption? _selectedNpcCharacter;

    /// <summary>The characters of the chosen kind (one list per choice: the box finds its selected item in it).</summary>
    [ObservableProperty]
    private IReadOnlyList<NpcCharacterOption> _npcCharacters = [];

    partial void OnSelectedNpcKindChanged(NpcKindOption? value)
    {
        NpcCharacters = value is null ? [] : value.Type.Characters.Select(c => new NpcCharacterOption(c, NpcKindNames.CharacterOf(c))).ToList();
        SelectedNpcCharacter = NpcCharacters.FirstOrDefault();
    }

    /// <summary>Posts around the clicked point.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NpcSummary))]
    private int _npcPosts = 3;

    /// <summary>The NPC kinds are being read.</summary>
    [ObservableProperty]
    private bool _isLoadingNpcKinds;

    /// <summary>The next click on the map places the NPCs.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PickNpcPlaceCommand))]
    private bool _isNpcPlacing;

    /// <summary>The post counts offered.</summary>
    public IReadOnlyList<int> NpcPostChoices { get; } = Enumerable.Range(1, NpcPlacement.MaxPosts).ToList();

    /// <summary>A kind is chosen.</summary>
    public bool HasNpcKind => SelectedNpcKind is not null;

    /// <summary>What the choice does, in plain words (and what the game does with it).</summary>
    public string NpcSummary => SelectedNpcKind is not { } kind ? string.Empty
        : NpcOwnCopy && SelectedNpcCharacter is { } who ? Loc.F("Spawns.NpcWizard.OwnSummary", NpcPosts, who.Title)
        : Loc.F("Spawns.NpcWizard.Summary", NpcPosts, kind.Title);

    private bool CanPickNpcPlace() => SelectedNpcKind is not null && !IsNpcPlacing && (!NpcOwnCopy || SelectedNpcCharacter is not null);

    partial void OnMapToolChanged(SpawnMapTool value)
    {
        if (value != SpawnMapTool.Place)
        {
            IsNpcPlacing = false; // another tool picked: the click no longer places NPCs
        }
    }

    /// <summary>Reads the NPC kinds the first time the NPC category is shown for a game.</summary>
    private void ShowNpcWizard()
    {
        if (Tab != SpawnTab.Npcs)
        {
            IsNpcPlacing = false;
            return;
        }

        if (_fixedSource is not null || _services.Workspace.Catalog is not { } catalog || ReferenceEquals(_npcKindsFor, catalog))
        {
            return;
        }

        _npcKindsFor = catalog;
        var load = ++_npcKindsLoad;
        IsLoadingNpcKinds = true;
        _ = Task.Run(() => NpcGuards.Types(catalog)).ContinueWith(t => _services.Dispatcher.Post(() =>
        {
            if (load != _npcKindsLoad)
            {
                return;
            }

            IsLoadingNpcKinds = false;
            NpcKinds = t.IsCompletedSuccessfully ? Options(t.Result) : [];
            SelectedNpcKind = NpcKinds.FirstOrDefault(k => k.Type.Name.Contains("Military_Base", StringComparison.OrdinalIgnoreCase)) ?? NpcKinds.FirstOrDefault();
            if (!t.IsCompletedSuccessfully)
            {
                _npcKindsFor = null; // read again the next time the category is shown
                _services.Notifications.Error(Loc.T("Spawns.NpcWizard.Title"), t.Exception?.GetBaseException().Message ?? Loc.T("Spawns.NpcWizard.NoTypes"));
            }
        }), TaskScheduler.Default);
    }

    /// <summary>A new game: the kinds are read again when the NPC category is shown.</summary>
    private void ResetNpcKinds()
    {
        _npcKindsFor = null;
        _npcKindsLoad++;
        IsLoadingNpcKinds = false;
        IsNpcPlacing = false;
        NpcKinds = [];
        SelectedNpcKind = null;
        ShowNpcWizard();
    }

    /// <summary>The kinds as the wizard lists them: by threat level, then by place.</summary>
    internal IReadOnlyList<NpcKindOption> Options(IEnumerable<NpcGuardType> types) => types
        .Select(t => (Type: t, Threat: NpcKindNames.ThreatOf(t.Name)))
        .OrderBy(t => t.Threat == NpcThreat.Unknown ? int.MaxValue : (int)t.Threat)
        .ThenBy(t => NpcKindNames.PlaceOf(t.Type.Name), StringComparer.OrdinalIgnoreCase)
        .Select(t => new NpcKindOption(t.Type, NpcKindNames.PlaceOf(t.Type.Name), Detail(t.Type, t.Threat)))
        .ToList();

    private static string Detail(NpcGuardType type, NpcThreat threat)
    {
        var who = string.Join(", ", type.Characters.Select(NpcKindNames.CharacterOf));
        var level = threat switch
        {
            NpcThreat.Low => Loc.T("Spawns.NpcWizard.Threat.Low"),
            NpcThreat.Medium => Loc.T("Spawns.NpcWizard.Threat.Medium"),
            NpcThreat.High => Loc.T("Spawns.NpcWizard.Threat.High"),
            _ => string.Empty,
        };
        return level.Length == 0 ? who : $"{level} · {who}";
    }

    /// <summary>The next click on the island map places the chosen NPCs there.</summary>
    [RelayCommand(CanExecute = nameof(CanPickNpcPlace))]
    private void PickNpcPlace()
    {
        if (_services.Projects.Current is null)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Spawns.NoProject"));
            return;
        }

        MapTool = SpawnMapTool.Place;
        IsNpcPlacing = true;
    }

    /// <summary>Stops waiting for the click; nothing is changed.</summary>
    [RelayCommand]
    private void CancelNpcPlace() => IsNpcPlacing = false;

    /// <summary>A click on the map while the wizard waits: the chosen NPCs, <see cref="NpcPosts"/> posts around the point, in one undo step.</summary>
    internal async Task PlaceNpcsAsync(Vector2 world)
    {
        IsNpcPlacing = false;
        if (SelectedNpcKind is not { } kind || _source is not { } source)
        {
            return;
        }

        if (_services.Projects.Current?.State is not { } state)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Spawns.NoProject"));
            return;
        }

        var ground = await Task.Run(() => source.GroundAt(world.X, world.Y)).ConfigureAwait(true);
        var posts = NpcPlacement.PostsAround(new FVector(world.X, world.Y, (ground ?? 0f) + SpawnGroundCheck.CharacterLift), NpcPosts);
        var planned = state.AssetClones.SelectMany(c => c.Packages.Select(p => p.New)).ToList();
        var taken = _places.Select(p => p.ActorName).Concat(state.AddedActors.Keys.Select(a => a.Actor)).ToList();
        var zones = SpawnPlaces.WithEdits(_stock.Where(p => p.Kind == SpawnPlaceKind.Zone).ToList(), state);
        var own = NpcOwnCopy ? SelectedNpcCharacter : null;
        try
        {
            var ops = await Task.Run(() =>
            {
                var package = kind.Type.ZonePackage;
                var rows = source.WeightLists(package).FirstOrDefault(l => l.Level == 0)
                    ?? throw new ArgumentException($"{Leaf(package)}: its encounters cannot be read.");
                var leaf = Leaf(package);
                var stored = source.Read(package)
                    .Where(t => t.Export == leaf && NpcGuards.NpcOnlyValues.Any(v => v.Path == t.Path))
                    .ToDictionary(t => t.Path, t => t.Value, StringComparer.Ordinal);
                if (own is not null)
                {
                    // An own copy of one NPC: its whole chain is copied, the stock NPCs and their data stay as the game has them.
                    var npc = source.NpcCopy(kind.Type, own.Name, planned);
                    return NpcPlacement.Plan([new NpcPostGroup(posts)], npc.Type, npc.Chain, NpcCopies.Rows(rows, npc.Chain), taken, zones, stored, npc.Tunes);
                }

                return NpcPlacement.Plan([new NpcPostGroup(posts)], kind.Type, source.ZoneCopy(package, planned), rows, taken, zones, stored);
            }).ConfigureAwait(true);

            var title = own is null ? kind.Title : $"{own.Title} ({Loc.T("Spawns.NpcWizard.OwnTag")})";
            if (Record(new BatchOp(Loc.F("Spawns.NpcWizard.Batch", posts.Count, title), ops)))
            {
                var done = own is null ? Loc.F("Spawns.NpcWizard.Done", posts.Count, kind.Title) : Loc.F("Spawns.NpcWizard.DoneOwn", posts.Count, own.Title);
                _services.Notifications.Success(Loc.T("Spawns.NpcWizard.Title"), done);
                _services.Logger.LogInformation("Placed {Count} NPC post(s) of {Kind}{Own} at {X:0}, {Y:0}", posts.Count, kind.Type.Name, own is null ? string.Empty : " (own copy of " + own.Name + ")", world.X, world.Y);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or FormatException or InvalidDataException)
        {
            _services.Notifications.Error(Loc.T("Module.NotRecorded"), ex.Message);
        }
    }
}
