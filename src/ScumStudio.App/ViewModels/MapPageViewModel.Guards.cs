using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// "Replace with NPC guards" on a selected sentry robot spawner (Discord igor8802: "when creating patrols at points of
/// interest, the robots can be replaced with NPCs"). SCUM's armed NPCs cannot walk a route (see <see cref="NpcGuards"/>),
/// so the panel says so and offers the closest thing: guard posts along the robot's route, in a guard zone of the chosen
/// NPCs. One undo step; the export writes the level (the robot removed) and the island's spawn data for client and server.
/// </summary>
public sealed partial class MapPageViewModel
{
    private const string SentrySpawnerClass = "SentrySpawner2";
    private (AssetCatalog Catalog, Task<IReadOnlyList<NpcGuardType>> Load)? _guardTypesLoad;

    /// <summary>True when the selection is a sentry robot spawner the project can replace.</summary>
    [ObservableProperty]
    private bool _hasGuardsOption;

    /// <summary>The armed NPC kinds a guard post can spawn (read from the game in the background).</summary>
    [ObservableProperty]
    private IReadOnlyList<NpcGuardType> _guardTypes = [];

    /// <summary>The chosen NPC kind.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplaceSentriesCommand))]
    private NpcGuardType? _selectedGuardType;

    /// <summary>Posts along each robot's route.</summary>
    [ObservableProperty]
    private int _guardPosts = NpcGuards.DefaultPosts;

    /// <summary>On: every robot of the selected one's place (its level) is replaced, not only the selected one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuardTargetsText))]
    private bool _guardsWholePlace;

    /// <summary>True while the NPC kinds are read.</summary>
    [ObservableProperty]
    private bool _isLoadingGuardTypes;

    /// <summary>True while a replacement is planned.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplaceSentriesCommand))]
    private bool _isReplacingSentries;

    /// <summary>The post counts the panel offers.</summary>
    public IReadOnlyList<int> GuardPostChoices { get; } = Enumerable.Range(1, 12).ToList();

    /// <summary>"Replaces 1 robot" / "Replaces 4 robots of A_4_Military_Base".</summary>
    public string GuardTargetsText => GuardTargets() is { Count: > 0 } targets
        ? Loc.F("Map.Guards.Targets", targets.Count, targets[0].LevelName)
        : string.Empty;

    /// <summary>The robots the replacement takes: the selected spawner, or every one of its level.</summary>
    private List<ActorItemViewModel> GuardTargets()
    {
        if (SelectedActor is not { ClassName: SentrySpawnerClass, IsDeleted: false } selected || SelectedInstanceKey is not null)
        {
            return [];
        }

        return GuardsWholePlace
            ? AllActors.Where(a => a is { ClassName: SentrySpawnerClass, IsDeleted: false } && a.Reference.Level.Equals(selected.Reference.Level, StringComparison.OrdinalIgnoreCase)).ToList()
            : [selected];
    }

    /// <summary>Shows the panel for a sentry spawner and starts reading the NPC kinds once (called with the selection's other panels).</summary>
    private void RefreshGuardsOption()
    {
        HasGuardsOption = _services.Projects.Current is not null && GuardTargets().Count > 0;
        OnPropertyChanged(nameof(GuardTargetsText));
        if (!HasGuardsOption || _services.Workspace.Catalog is not { } catalog || _guardTypesLoad?.Catalog == catalog)
        {
            return;
        }

        var load = Task.Run(() => NpcGuards.Types(catalog));
        _guardTypesLoad = (catalog, load);
        IsLoadingGuardTypes = true;
        _ = load.ContinueWith(t => _services.Dispatcher.Post(() =>
        {
            if (_guardTypesLoad?.Load != load)
            {
                return;
            }

            IsLoadingGuardTypes = false;
            GuardTypes = t.IsCompletedSuccessfully ? t.Result : [];
            SelectedGuardType = GuardTypes.FirstOrDefault(g => g.Name.Contains("Military_Base", StringComparison.OrdinalIgnoreCase)) ?? GuardTypes.FirstOrDefault();
            if (!t.IsCompletedSuccessfully)
            {
                _services.Notifications.Error(Loc.T("Map.Guards.Title"), t.Exception?.GetBaseException().Message ?? Loc.T("Map.Guards.NoTypes"));
            }
        }), TaskScheduler.Default);
    }

    private bool CanReplaceSentries() => SelectedGuardType is not null && !IsReplacingSentries;

    /// <summary>Replaces the robots with guard posts of the chosen NPCs: one undo step.</summary>
    [RelayCommand(CanExecute = nameof(CanReplaceSentries))]
    private async Task ReplaceSentriesAsync()
    {
        var targets = GuardTargets();
        if (SelectedGuardType is not { } type || targets.Count == 0 || _services.Workspace.Catalog is not { } catalog)
        {
            return;
        }

        if (_services.Projects.Current?.State is not { } state)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Spawns.NoProject"));
            return;
        }

        // Each robot's route as the map has it now: where the spawner stands, then its patrol points (moved ones included).
        var routes = targets.Select(t =>
        {
            var world = RootWorldOf(t, CurrentRootTransform(t));
            var points = state.GetSpawnPoints(t.Reference, null, SpawnPointArrays.PatrolPoints)?.Select(p => p.Local.Location).ToList()
                ?? t.Actor.PatrolPoints.ToList();
            return new SentryRoute(t.Reference, [world.Translation, .. points.Select(world.TransformPosition)]);
        }).ToList();
        var posts = GuardPosts;
        var planned = state.AssetClones.SelectMany(c => c.Packages.Select(p => p.New)).ToList();
        var taken = state.AddedActors.Keys.Where(a => SpawnPlaces.IsStaticData(a.Level)).Select(a => a.Actor).ToList();
        IsReplacingSentries = true;
        try
        {
            var ops = await Task.Run(() =>
            {
                var leaf = type.ZonePackage[(type.ZonePackage.LastIndexOf('/') + 1)..];
                var copy = ZoneCopies.Plan(catalog, type.ZonePackage, ZoneCopies.SuggestName(leaf, ZoneCopies.Taken(catalog, planned)), planned);
                var rows = SpawnChoices.ForZone(catalog, type.ZonePackage).FirstOrDefault(l => l.Level == 0)
                    ?? throw new ArgumentException($"{leaf}: its encounters cannot be read.");
                return NpcGuards.Plan(routes, type, posts, copy, rows, taken);
            }).ConfigureAwait(true);
            var entry = _services.Projects.Apply(new BatchOp(Loc.F("Map.Guards.Batch", targets.Count, type.Name), ops));
            _services.Notifications.Success(Loc.T("Map.Guards.Title"), Loc.F("Map.Guards.Done", targets.Count, routes.Sum(r => NpcGuards.Posts(r.Path, posts).Count), type.Name));
            _services.Logger.LogInformation("Replaced {Count} sentry robot(s) with NPC guards: {Edit}", targets.Count, entry.Op.Describe());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or FormatException or InvalidDataException)
        {
            _services.Notifications.Error(Loc.T("Module.NotRecorded"), ex.Message);
        }
        finally
        {
            IsReplacingSentries = false;
            RefreshGuardsOption();
        }
    }
}
