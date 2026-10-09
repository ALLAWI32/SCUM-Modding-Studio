using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;

namespace ScumStudio.App.ViewModels;

/// <summary>A trader type in the Add object box (<c>Armorer</c> shown as "Armory").</summary>
/// <param name="Type">Trader type (<c>Armorer</c>, <c>GeneralGoods</c> …, <c>Bank</c>).</param>
public sealed record TraderTypeItem(string Type)
{
    /// <summary>Shown name.</summary>
    public string Label => Loc.Instance.Or("Trader.Type." + Type, Type);

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// Placing a trader anywhere (owner: "place a trader anywhere"): the Add object box's Trader part copies a stock trade post
/// of the chosen type (one that stores its trader on the instance, see <see cref="TraderPosts"/>) where the camera aims, as
/// one undoable edit (<see cref="AddBlueprintActorOp.Trader"/>): the export gives it a personality of its own named after it
/// (its section in the server's economy) and registers it with its outpost. "Edit stock" on a trader opens its economy.
/// </summary>
public sealed partial class MapPageViewModel
{
    private static readonly string[] KnownTraderTypes = ["Armorer", "GeneralGoods", "Mechanic", "Doctor", "Bartender", "Barber", "Harbourmaster", "Hunter", "MasterHunter", TraderPosts.BankType];

    /// <summary>Trader names given to copies made in one step (keyed by that step's reserved actors), so two copies differ.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISet<ActorRef>, List<(string Name, string Type)>> _stepTraders = new();
    private Task<IReadOnlyList<TraderKind>>? _traderKinds;
    private Task<IReadOnlyList<LevelDocument>>? _outpostDocuments;
    private AssetCatalog? _traderKindsCatalog;
    private Action<string>? _openEconomy;

    /// <summary>The trader types the Add object box offers (the game's, once its outposts were read).</summary>
    [ObservableProperty]
    private IReadOnlyList<TraderTypeItem> _traderTypes = KnownTraderTypes.Select(t => new TraderTypeItem(t)).ToList();

    /// <summary>The chosen trader type.</summary>
    [ObservableProperty]
    private TraderTypeItem? _selectedTraderType;

    /// <summary>The new trader's name: its section in the server's economy (default from the cell and type: <c>Z_4_Trader_1</c>).</summary>
    [ObservableProperty]
    private string _traderName = string.Empty;

    /// <summary>Its outpost: a stock one (<c>Outpost_A_0</c>) to join, or a new name (default: the cell's, <c>Outpost_Z_4</c>).</summary>
    [ObservableProperty]
    private string _traderOutpost = string.Empty;

    /// <summary>Opens the Economy page at a trader (set by the main window).</summary>
    public void SetEconomyOpener(Action<string> openEconomy) => _openEconomy = openEconomy;

    partial void OnSelectedTraderTypeChanged(TraderTypeItem? value)
    {
        if (value is not null && CellOfNewObjects() is { } cell)
        {
            TraderName = value.Type == TraderPosts.BankType ? string.Empty : FreeDefaultName(cell, value.Type);
        }
    }

    /// <summary>
    /// Fills the Trader part of the Add object box: defaults from the cell new objects go to, and the game's trade post types
    /// (read once, in the background, from the stock outposts).
    /// </summary>
    public void PrepareTraders()
    {
        if (CellOfNewObjects() is { } cell)
        {
            TraderOutpost = TraderPosts.DefaultOutpost(cell);
        }

        SelectedTraderType ??= TraderTypes.FirstOrDefault();
        OnSelectedTraderTypeChanged(SelectedTraderType);
        PrepareLifts();
        if (_services.Workspace.Catalog is not { } catalog)
        {
            return;
        }

        // The default name counts the game's traders of the cell too (B_4 has an armory: B_4_Armory_2).
        if (_services.Economy.GameTraders is null)
        {
            _ = _services.Economy.GameTradersAsync().ContinueWith(_ => _services.Dispatcher.Post(() => OnSelectedTraderTypeChanged(SelectedTraderType)), TaskScheduler.Default);
        }

        _ = TraderKindsAsync(catalog).ContinueWith(t => _services.Dispatcher.Post(() =>
        {
            if (t.IsCompletedSuccessfully && t.Result.Count > 0)
            {
                var selected = SelectedTraderType?.Type;
                TraderTypes = t.Result.Select(k => new TraderTypeItem(k.Type)).ToList();
                SelectedTraderType = TraderTypes.FirstOrDefault(k => k.Type == selected) ?? TraderTypes[0];
            }
        }), TaskScheduler.Default);
    }

    /// <summary>Places a trader of <paramref name="type"/> with the cell's default name and outpost (from the Economy page).</summary>
    public void PlaceTraderOfType(string type) => AddCompletion = AddTraderAsync(type);

    [RelayCommand]
    private void PlaceTrader()
    {
        if (SelectedTraderType is { } type)
        {
            AddCompletion = AddTraderAsync(type.Type, TraderName, TraderOutpost);
        }
    }

    /// <summary>
    /// Adds a trader of <paramref name="type"/> where the viewport aims, in the level new objects go to: a copy of a stock trade
    /// post of that type named <paramref name="name"/> (default <c>&lt;cell&gt;_&lt;Armory|Trader|…&gt;_&lt;n&gt;</c>, see
    /// <see cref="TraderPosts.DefaultName"/>) in <paramref name="outpost"/> (default <c>Outpost_&lt;cell&gt;</c>). One edit in
    /// the project's history; the project's economy gets its section at once (<see cref="Services.ProjectEconomy"/>).
    /// </summary>
    public async Task<bool> AddTraderAsync(string type, string? name = null, string? outpost = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
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

        var target = NewObjectLevel(scene);
        var at = AimPointProvider?.Invoke() ?? FVector.Zero;
        var cell = CellOf(target) ?? "X_0";
        var bank = type == TraderPosts.BankType;
        var gameTraders = await _services.Economy.GameTradersAsync().ConfigureAwait(true);
        name = bank ? string.Empty : string.IsNullOrWhiteSpace(name) ? FreeDefaultName(cell, type) : name.Trim();
        outpost = string.IsNullOrWhiteSpace(outpost) ? TraderPosts.DefaultOutpost(cell) : outpost.Trim();
        if (!TraderPosts.IsValidName(outpost) || (!bank && !TraderPosts.IsValidName(name)))
        {
            _services.Notifications.Warning(Loc.T("Map.Trader.BadName"), Loc.T("Map.Trader.BadNameDetail"));
            return false;
        }

        try
        {
            var kinds = await TraderKindsAsync(catalog).ConfigureAwait(true);
            if (kinds.FirstOrDefault(k => k.Type == type) is not { } kind)
            {
                _services.Notifications.Warning(Loc.T("Map.Trader.NoSource"), Loc.F("Map.Trader.NoSourceDetail", type));
                return false;
            }

            if (!bank && TraderNameTaken(gameTraders, project.State, name))
            {
                _services.Notifications.Warning(Loc.T("Map.Trader.NameTaken"), Loc.F("Map.Trader.NameTakenDetail", name));
                return false;
            }

            var (source, placements) = await Task.Run(() =>
            {
                var document = ReadDocument(catalog, kind.Source.Level, CancellationToken.None);
                var actor = document.FindActor(kind.Source.Actor) ?? throw new InvalidDataException($"{kind.Source} is not in its level.");
                return (actor, PlacementsOf(document, actor.Name));
            }).ConfigureAwait(true);
            if (!ReferenceEquals(_services.Projects.Current, project))
            {
                return false; // the project was closed meanwhile
            }

            _foreignPlacements[kind.Source] = placements;
            var actorName = EditOpFactory.UniqueActorName(target, (bank ? ShortName(kind.ClassPath) : name) + "_Added", project.State);
            var op = new AddBlueprintActorOp(target.PackagePath, actorName, kind.ClassPath, kind.Source,
                TransformValue.FromTransform(source.WorldTransform with { Translation = at }))
            {
                Trader = new TraderPost(name, type, outpost) { Personality = kind.Personality },
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

    /// <summary>
    /// Places a trader of the type whose NPC is <paramref name="npcPackage"/> (a trader character picked in Assets): its
    /// trade post with that NPC, named and joined to its outpost by the cell, where the camera aims.
    /// </summary>
    public async Task<bool> AddTraderForNpcAsync(string npcPackage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(npcPackage);
        if (_services.Workspace.Catalog is not { } catalog)
        {
            return false;
        }

        var kinds = await TraderKindsAsync(catalog).ConfigureAwait(true);
        var kind = kinds.FirstOrDefault(k => k.NpcClass.Length > 0
            && string.Equals(AssetPaths.SplitObjectPath(k.NpcClass).PackagePath, npcPackage, StringComparison.OrdinalIgnoreCase));
        if (kind is null)
        {
            _services.Notifications.Warning(Loc.T("Map.Trader.NotATrader"), Loc.F("Map.Trader.NotATraderDetail", npcPackage[(npcPackage.LastIndexOf('/') + 1)..]));
            return false;
        }

        return await AddTraderAsync(kind.Type).ConfigureAwait(true);
    }

    /// <summary>The game's placeable trade post kinds for <paramref name="catalog"/>, read once on a worker.</summary>
    private Task<IReadOnlyList<TraderKind>> TraderKindsAsync(AssetCatalog catalog)
    {
        var documents = OutpostDocumentsAsync(catalog);
        if (_traderKinds is null || _traderKinds.IsFaulted)
        {
            _traderKinds = Task.Run(async () => TraderPosts.FindKinds(await documents.ConfigureAwait(false)));
        }

        return _traderKinds;
    }

    /// <summary>The stock outposts' levels (the trade posts and lifts to copy), read once per catalog on a worker.</summary>
    private Task<IReadOnlyList<LevelDocument>> OutpostDocumentsAsync(AssetCatalog catalog)
    {
        if (_outpostDocuments is null || _outpostDocuments.IsFaulted || !ReferenceEquals(_traderKindsCatalog, catalog))
        {
            var world = World;
            _traderKindsCatalog = catalog;
            _traderKinds = null;
            _liftKindsTask = null;
            _outpostDocuments = Task.Run<IReadOnlyList<LevelDocument>>(() =>
            {
                var levels = TraderPosts.OutpostLevels((world ?? WorldIndex.FromCatalog(catalog)).Packages.Select(p => p.PackagePath));
                var documents = new List<LevelDocument>();
                foreach (var level in levels)
                {
                    try
                    {
                        documents.Add(ReadDocument(catalog, level, CancellationToken.None));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
                    {
                        _services.Logger.LogWarning("{Level} could not be read for its trade posts: {Message}", level, ex.Message);
                    }
                }

                return documents;
            });
        }

        return _outpostDocuments;
    }

    /// <summary>True when a game trader or a trader of the project already has <paramref name="name"/> (it is an economy section).</summary>
    private static bool TraderNameTaken(IReadOnlyList<TraderInfo> gameTraders, EditState state, string name) =>
        TraderPosts.Placed(state).Any(p => string.Equals(p.Op.Trader!.Name, name, StringComparison.OrdinalIgnoreCase))
        || gameTraders.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The default name of a new trader (owner: "numbered per sector and type", see <see cref="TraderPosts.DefaultName"/>):
    /// the game's traders of the cell count (once read), so B_4's second armory is <c>B_4_Armory_2</c> and a farm's first is
    /// <c>A_3_Armory_1</c>.
    /// </summary>
    private string FreeDefaultName(string cell, string type, ISet<ActorRef>? reserved = null)
    {
        var name = TraderPosts.DefaultName(cell, type, KnownTraders(_services.Projects.Current?.State, reserved));
        if (reserved is not null)
        {
            _stepTraders.GetOrCreateValue(reserved).Add((name, type));
        }

        return name;
    }

    /// <summary>The game's traders (when read) and the project's placed ones, with the copies named in the same step as <paramref name="reserved"/>.</summary>
    private IEnumerable<(string Name, string Type)> KnownTraders(EditState? state, ISet<ActorRef>? reserved)
    {
        foreach (var trader in _services.Economy.GameTraders ?? [])
        {
            yield return (trader.Name, trader.Type);
        }

        if (state is null)
        {
            yield break;
        }

        foreach (var (_, op) in TraderPosts.Placed(state))
        {
            yield return (op.Trader!.Name, op.Trader.Type);
        }

        if (reserved is not null && _stepTraders.TryGetValue(reserved, out var step))
        {
            foreach (var trader in step)
            {
                yield return trader;
            }
        }
    }

    /// <summary>The cell (<c>A_3</c>) of the level new objects go to, or null without a scene.</summary>
    private string? CellOfNewObjects() => PreparedScene is { Documents.Count: > 0 } scene ? CellOf(NewObjectLevel(scene)) : null;

    /// <summary>A level's map cell (<c>A_3_Farm_01</c> → <c>A_3</c>), from the world index or its name.</summary>
    private string? CellOf(LevelDocument level)
    {
        if (World?.Packages.FirstOrDefault(p => string.Equals(p.PackagePath, level.PackagePath, StringComparison.OrdinalIgnoreCase))?.Cell is { } cell)
        {
            return cell.ToString();
        }

        return level.Name.Length >= 3 && MapCell.TryParse(level.Name[..3], out var parsed) ? parsed.ToString() : null;
    }

    /// <summary>A placed trader's markers under its own name (the copy's source names its own trader); others as they are.</summary>
    private IReadOnlyList<TraderMarker> PlacedTraderMarkers(AddBlueprintActorOp op, IReadOnlyList<TraderMarker> source) =>
        op.Trader is { Name.Length: > 0 } trader
            ? source.Count > 0
                ? source.Select(m => m with { Name = trader.Name, Type = trader.Type }).ToList()
                : [new TraderMarker(FTransform.Identity, trader.Name, trader.Type, NpcOf(trader.Type), trader.Personality ?? string.Empty)]
            : source;

    /// <summary>The NPC class of a trader type's stock post (once the outposts were read), else the type.</summary>
    private string NpcOf(string type) =>
        _traderKinds is { IsCompletedSuccessfully: true } kinds && kinds.Result.FirstOrDefault(k => k.Type == type)?.NpcClass is { Length: > 0 } npc ? npc : type;

    /// <summary>The selected actor's trader: a placed trader's own name, a stock trade post's trader name; null otherwise.</summary>
    public string? SelectedTraderName =>
        SelectedActor is not { } item ? null
        : item.IsAdded ? (_services.Projects.Current?.State.AddedActors.GetValueOrDefault(item.Reference) as AddBlueprintActorOp)?.Trader?.Name is { Length: > 0 } placed ? placed : null
        : item.Actor.TraderMarkers.FirstOrDefault()?.Name is { Length: > 0 } stock ? stock : null;

    /// <summary>True when the selection is a trader (its "Edit stock" opens its economy).</summary>
    public bool HasSelectedTrader => SelectedTraderName is not null;

    /// <summary>Opens the Economy page at the selected trader's section.</summary>
    [RelayCommand]
    private void EditStock()
    {
        if (SelectedTraderName is { } name)
        {
            _openEconomy?.Invoke(name);
        }
    }
}
