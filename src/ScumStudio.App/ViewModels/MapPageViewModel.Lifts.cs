using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>A lift kind in the Add object box (<c>CarLift</c> shown as "Car lift").</summary>
/// <param name="Kind"><c>CarLift</c> or <c>BikeLift</c>.</param>
public sealed record LiftKindItem(string Kind)
{
    /// <summary>Shown name.</summary>
    public string Label => Loc.Instance.Or("Lift.Kind." + Kind, Kind);

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>A mechanic a lift can serve: a stock mechanic's trade post of a loaded level, or a placed Mechanic trader.</summary>
/// <param name="Post">The trade post actor.</param>
/// <param name="Label">Shown name ("Trader Mechanic B_4 · B_4_Mechanic").</param>
public sealed record MechanicItem(ActorRef Post, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// Placing a mechanic's lift (owner: "add lifts as placeable assets linked to the mechanic"): the Add object box's Lift part
/// copies the game's car or bike lift where the camera aims, in the level of the chosen mechanic, as one undoable edit
/// (<see cref="AddBlueprintActorOp.Mechanic"/>): the export points its <c>_assignedTradePost</c> at that mechanic's trade
/// post, so the vehicle on it is repaired and upgraded through that mechanic's menu.
/// </summary>
public sealed partial class MapPageViewModel
{
    private Task<IReadOnlyList<LiftKind>>? _liftKindsTask;

    /// <summary>The lift kinds the Add object box offers (the game's, once its outposts were read).</summary>
    [ObservableProperty]
    private IReadOnlyList<LiftKindItem> _liftKinds = TraderPosts.KnownLiftKinds.Select(k => new LiftKindItem(k)).ToList();

    /// <summary>The chosen lift kind.</summary>
    [ObservableProperty]
    private LiftKindItem? _selectedLiftKind;

    /// <summary>The mechanics of the loaded levels (stock and placed), nearest to the camera's aim first.</summary>
    [ObservableProperty]
    private IReadOnlyList<MechanicItem> _mechanics = [];

    /// <summary>The mechanic the new lift serves.</summary>
    [ObservableProperty]
    private MechanicItem? _selectedMechanic;

    /// <summary>Fills the Lift part of the Add object box: the mechanics in reach and the game's lift kinds (read in the background).</summary>
    public void PrepareLifts()
    {
        Mechanics = FindMechanics();
        SelectedMechanic = Mechanics.FirstOrDefault(m => SelectedMechanic is { } s && m.Post.Equals(s.Post)) ?? Mechanics.FirstOrDefault();
        SelectedLiftKind ??= LiftKinds.FirstOrDefault();
        if (_services.Workspace.Catalog is not { } catalog)
        {
            return;
        }

        _ = LiftKindsAsync(catalog).ContinueWith(t => _services.Dispatcher.Post(() =>
        {
            if (t.IsCompletedSuccessfully && t.Result.Count > 0)
            {
                var selected = SelectedLiftKind?.Kind;
                LiftKinds = t.Result.Select(k => new LiftKindItem(k.Kind)).ToList();
                SelectedLiftKind = LiftKinds.FirstOrDefault(k => k.Kind == selected) ?? LiftKinds[0];
            }
        }), TaskScheduler.Default);
    }

    [RelayCommand]
    private void PlaceLift()
    {
        if (SelectedLiftKind is { } kind && SelectedMechanic is { } mechanic)
        {
            AddCompletion = AddLiftAsync(kind.Kind, mechanic.Post);
        }
        else
        {
            _services.Notifications.Warning(Loc.T("Map.Lift.NoMechanic"), Loc.T("Map.Lift.NoMechanicDetail"));
        }
    }

    /// <summary>
    /// Adds a lift of <paramref name="kind"/> where the viewport aims, in the level of <paramref name="mechanic"/> (a stock
    /// mechanic's trade post or a placed Mechanic trader, which must be loaded), serving that mechanic. One edit in the history.
    /// </summary>
    public async Task<bool> AddLiftAsync(string kind, ActorRef mechanic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(mechanic);
        if (PreparedScene is not { } scene || scene.Documents.Count == 0)
        {
            _services.Notifications.Warning(Loc.T("Map.NoLevelLoaded"), Loc.T("Map.NoLevelLoadedDetail"));
            return false;
        }

        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.NoProject.Adds"));
            return false;
        }

        if (_services.Workspace.Catalog is not { } catalog)
        {
            return false;
        }

        var target = scene.Documents.FirstOrDefault(d => string.Equals(d.PackagePath, mechanic.Level, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            _services.Notifications.Warning(Loc.T("Map.Lift.NoMechanic"), Loc.T("Map.Lift.NoMechanicDetail"));
            return false;
        }

        var at = AimPointProvider?.Invoke() ?? FVector.Zero;
        try
        {
            var kinds = await LiftKindsAsync(catalog).ConfigureAwait(true);
            if (kinds.FirstOrDefault(k => k.Kind == kind) is not { } lift)
            {
                _services.Notifications.Warning(Loc.T("Map.Lift.NoSource"), Loc.F("Map.Lift.NoSourceDetail", kind));
                return false;
            }

            var (source, placements) = await Task.Run(() =>
            {
                var document = ReadDocument(catalog, lift.Source.Level, CancellationToken.None);
                var actor = document.FindActor(lift.Source.Actor) ?? throw new InvalidDataException($"{lift.Source} is not in its level.");
                return (actor, PlacementsOf(document, actor.Name));
            }).ConfigureAwait(true);
            if (!ReferenceEquals(_services.Projects.Current, project))
            {
                return false; // the project was closed meanwhile
            }

            _foreignPlacements[lift.Source] = placements;
            var actorName = EditOpFactory.UniqueActorName(target, lift.Kind + "_Added", project.State);
            var op = new AddBlueprintActorOp(target.PackagePath, actorName, lift.ClassPath, lift.Source,
                TransformValue.FromTransform(source.WorldTransform with { Translation = at }))
            {
                Mechanic = mechanic.Actor,
            };
            var entry = _services.Projects.Apply(op);
            _services.Notifications.Info(Loc.T("Map.Added"), entry.Op.Describe());
            SelectCreated(op);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or InvalidDataException)
        {
            _services.Notifications.Error(Loc.T("Map.AddFailed"), ex.Message);
            return false;
        }
    }

    /// <summary>The game's placeable lift kinds for <paramref name="catalog"/>, read once on a worker (with the trade posts).</summary>
    private Task<IReadOnlyList<LiftKind>> LiftKindsAsync(AssetCatalog catalog)
    {
        var documents = OutpostDocumentsAsync(catalog);
        if (_liftKindsTask is null || _liftKindsTask.IsFaulted)
        {
            _liftKindsTask = Task.Run(async () => TraderPosts.FindLifts(await documents.ConfigureAwait(false)));
        }

        return _liftKindsTask;
    }

    /// <summary>The mechanics of the loaded levels: stock mechanic trade posts and placed Mechanic traders, nearest to the aim first.</summary>
    private IReadOnlyList<MechanicItem> FindMechanics()
    {
        if (PreparedScene is not { Documents.Count: > 0 } scene)
        {
            return [];
        }

        var aim = AimPointProvider?.Invoke() ?? FVector.Zero;
        var found = new List<(MechanicItem Item, float Distance)>();
        foreach (var document in scene.Documents)
        {
            foreach (var actor in document.Actors)
            {
                if (actor.TraderMarkers.FirstOrDefault(t => t.Type == TraderPosts.MechanicType) is { } marker)
                {
                    var label = $"{SpawnMarkers.TraderLabel(marker)} · {marker.Name}";
                    found.Add((new MechanicItem(new ActorRef(document.PackagePath, actor.Name), label), (actor.WorldTransform.Translation - aim).Size()));
                }
            }
        }

        if (_services.Projects.Current?.State is { } state)
        {
            foreach (var (reference, op) in TraderPosts.Placed(state))
            {
                var trader = op.Trader!;
                if (trader.Type == TraderPosts.MechanicType && scene.Documents.Any(d => string.Equals(d.PackagePath, reference.Level, StringComparison.OrdinalIgnoreCase)))
                {
                    var marker = new TraderMarker(FTransform.Identity, trader.Name, trader.Type, string.Empty, string.Empty);
                    var at = (state.GetAddedTransform(reference) ?? op.Transform).Location;
                    found.Add((new MechanicItem(reference, $"{SpawnMarkers.TraderLabel(marker)} · {trader.Name}"), (at - aim).Size()));
                }
            }
        }

        return found.OrderBy(f => f.Distance).Select(f => f.Item).ToList();
    }
}
