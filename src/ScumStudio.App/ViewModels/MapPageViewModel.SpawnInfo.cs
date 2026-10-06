using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The spawn pins explained (owner: "the spawner does not tell me what spawns here; the yellow, blue and pink circles,
/// I understood nothing"): a legend of every pin colour with its own on/off switch, and for the selection the list of what
/// can spawn there — a loot point's preset, its loot groups and every item with its rarity; a world vehicle point's
/// vehicles; a whole building's presets.
/// </summary>
public sealed partial class MapPageViewModel
{
    private IReadOnlyList<SpawnLayerViewModel>? _spawnLayers;
    private (AssetCatalog Catalog, Lazy<LootTables> Loot, Lazy<VehicleSpawnGroups> Vehicles)? _spawnData;
    private int _spawnInfoCheck;

    /// <summary>What can spawn at the selection (empty when it spawns nothing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpawnInfo))]
    private IReadOnlyList<SpawnInfoRow> _spawnInfo = [];

    /// <summary>True when <see cref="SpawnInfo"/> has rows.</summary>
    public bool HasSpawnInfo => SpawnInfo.Count > 0;

    /// <summary>The legend: one switch per kind of pin, in the colour the map draws it.</summary>
    public IReadOnlyList<SpawnLayerViewModel> SpawnLayers => _spawnLayers ??= BuildSpawnLayers();

    private IReadOnlyList<SpawnLayerViewModel> BuildSpawnLayers()
    {
        var hidden = _services.UiState.Current.HiddenSpawnLayers;
        var layers = new (string Key, SpawnKind[] Kinds)[]
        {
            ("Traders", [SpawnKind.Trader]),
            ("Loot", [SpawnKind.Loot]),
            ("Vehicles", [SpawnKind.VehiclePlace]),
            ("Zombies", [SpawnKind.Zombie]),
            ("Zones", [SpawnKind.Zone, SpawnKind.ZoneRing]),
            ("Animals", [SpawnKind.Animal, SpawnKind.AnimalRing]),
            ("LootZones", [SpawnKind.LootZone]),
            ("Sentries", [SpawnKind.Sentry, SpawnKind.Patrol]),
            ("Creatures", [SpawnKind.Creature]),
            ("CarShops", [SpawnKind.Vehicle]),
            ("Drops", [SpawnKind.PlayerDrop]),
        };
        return layers.Select(l => new SpawnLayerViewModel(l.Key, HexOf(SpawnMarkers.Color(l.Kinds[0])), l.Kinds, !hidden.Contains(l.Key), OnSpawnLayerChanged)).ToList();

        // The pins are drawn unlit in linear colour: the swatch shows the same colour on screen.
        static string HexOf(System.Numerics.Vector4 c)
        {
            static int Srgb(float v) => (int)Math.Round(Math.Clamp(MathF.Pow(Math.Clamp(v, 0f, 1f), 1f / 2.2f), 0f, 1f) * 255f);
            return $"#{Srgb(c.X):X2}{Srgb(c.Y):X2}{Srgb(c.Z):X2}";
        }
    }

    private void OnSpawnLayerChanged()
    {
        var hidden = SpawnLayers.Where(l => !l.IsVisible).Select(l => l.Key).ToList();
        _services.UiState.Update(u => u with { HiddenSpawnLayers = hidden });
        RefreshHiddenIds();
    }

    /// <summary>The pin kinds switched off in the legend (all of them when the Spawns button is off).</summary>
    private HashSet<SpawnKind> HiddenSpawnKinds() => ShowSpawns
        ? SpawnLayers.Where(l => !l.IsVisible).SelectMany(l => l.Kinds).ToHashSet()
        : Enum.GetValues<SpawnKind>().ToHashSet();

    /// <summary>
    /// The pin kinds the map does not draw (bound to the viewport, which hides only those pins: not the building or the item
    /// a pin stands in, not the other pins of the same actor).
    /// </summary>
    [ObservableProperty]
    private IReadOnlySet<SpawnKind> _hiddenPinKinds = new HashSet<SpawnKind>();

    /// <summary>True when one loot point of a building is selected (it shows what spawns there; it moves with its building).</summary>
    public bool IsLootPointSelected => SelectedInstanceKey is { IsLootPoint: true } key && SelectedActor?.SelectableId == key.SelectableId;

    /// <summary>Fills <see cref="SpawnInfo"/> for the selection in the background (the loot tables are read once).</summary>
    private void ShowSpawnInfo(ActorItemViewModel? item)
    {
        var check = ++_spawnInfoCheck;
        SpawnInfo = [];
        if (item is null || _services.Workspace.Catalog is not { } catalog)
        {
            return;
        }

        List<SpawnMarker> markers;
        if (SelectedInstanceKey is { IsLootPoint: true } point && point.SelectableId == item.SelectableId)
        {
            markers = item.Actor.FindComponent(point.Component)?.SpawnMarkers is { } all && point.Marker < all.Count ? [all[point.Marker]] : [];
        }
        else
        {
            // A spawn part (a house's drill press spawner): its own item; another part or instance: nothing.
            markers = SelectedInstanceInfo() is { } part ? [.. part.Component.SpawnMarkers] : item.Actor.Components.SelectMany(c => c.SpawnMarkers).ToList();
        }

        var group = SelectedInstanceInfo() is null && SpawnMarkers.KindOf(item.Actor) == SpawnKind.VehiclePlace ? item.Actor.ClassPath : null;
        if (markers.Count == 0 && group is null)
        {
            return;
        }

        if (_spawnData?.Catalog != catalog)
        {
            _spawnData = (catalog, new Lazy<LootTables>(() => LootTables.Read(catalog)), new Lazy<VehicleSpawnGroups>(() => VehicleSpawnGroups.Read(catalog)));
        }

        var data = _spawnData.Value;
        _ = Task.Run(() => BuildSpawnInfo(markers, group, data.Loot, data.Vehicles)).ContinueWith(t => _services.Dispatcher.Post(() =>
        {
            if (check == _spawnInfoCheck && t.IsCompletedSuccessfully)
            {
                SpawnInfo = t.Result;
            }
        }), TaskScheduler.Default);
    }

    /// <summary>The rows: per preset its chance and loot groups with every item and its rarity; per vehicle group its vehicles.</summary>
    private static List<SpawnInfoRow> BuildSpawnInfo(List<SpawnMarker> markers, string? vehicleGroup, Lazy<LootTables> lootTables, Lazy<VehicleSpawnGroups> vehicleGroups)
    {
        var rows = new List<SpawnInfoRow>();
        if (vehicleGroup is not null)
        {
            var vehicles = vehicleGroups.Value.VehiclesOf(vehicleGroup).Select(v => v.Vehicle).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            rows.Add(new SpawnInfoRow(Loc.F("SpawnInfo.VehicleGroup", vehicleGroup), Loc.T("SpawnInfo.VehicleGroup.Tip"), null, 0));
            rows.AddRange(vehicles.Count == 0
                ? [new SpawnInfoRow(Loc.T("SpawnInfo.NoVehicles"), null, null, 2)]
                : vehicles.Select(v => new SpawnInfoRow(LootTables.Readable(v), null, null, 2)));
        }

        foreach (var points in markers.GroupBy(m => m.PresetPath ?? "#" + m.Preset, StringComparer.OrdinalIgnoreCase))
        {
            var marker = points.First();
            if (marker.PresetPath is null)
            {
                rows.Add(new SpawnInfoRow(Loc.F("SpawnInfo.FixedItem", LootTables.Readable(marker.Preset)), null, null, 0)); // a world spawner's one item
                continue;
            }

            var loot = lootTables.Value;
            var preset = loot.Preset(marker.PresetPath);
            var quantity = marker.MinQuantity == marker.MaxQuantity ? $"{marker.MinQuantity}" : $"{marker.MinQuantity}-{marker.MaxQuantity}";
            rows.Add(new SpawnInfoRow(
                Loc.F("SpawnInfo.Preset", preset?.Name ?? marker.Preset, points.Count()),
                preset is null ? Loc.T("SpawnInfo.NoPreset")
                    : Loc.F("SpawnInfo.Chance", Math.Round((preset.AlwaysSpawn ? 100f : preset.Probability) * marker.Probability / 100f), quantity),
                null, 0));
            if (preset is null)
            {
                continue;
            }

            foreach (var node in preset.Nodes)
            {
                rows.Add(new SpawnInfoRow(Loc.F("SpawnInfo.Group", LootTables.GroupName(node.Tag)), null, node.Rarity, 1));
                var items = loot.ItemsUnder(node.Tag).DistinctBy(i => (i.Name, i.Branch)).ToList();
                rows.AddRange(items.Count == 0
                    ? [new SpawnInfoRow(Loc.T("SpawnInfo.NoItems"), null, null, 2)]
                    : items.Select(i => new SpawnInfoRow(LootTables.Readable(i.Name), i.Branch.Replace(".", " › ", StringComparison.Ordinal), i.Rarity, 2)));
            }
        }

        return rows;
    }
}

/// <summary>One kind of spawn pin in the map's legend: its colour, what it is, and whether the map shows it.</summary>
public sealed partial class SpawnLayerViewModel : ObservableObject
{
    private readonly Action _changed;

    /// <summary>Creates a legend row.</summary>
    public SpawnLayerViewModel(string key, string hex, IReadOnlyList<SpawnKind> kinds, bool visible, Action changed)
    {
        Key = key;
        Hex = hex;
        Kinds = kinds;
        _isVisible = visible;
        _changed = changed;
    }

    /// <summary>Key of the layer (<c>Loot</c>, <c>Vehicles</c>, ...), kept in the UI state when switched off.</summary>
    public string Key { get; }

    /// <summary>The pin colour (<c>#RRGGBB</c>).</summary>
    public string Hex { get; }

    /// <summary>The pin kinds of the layer.</summary>
    public IReadOnlyList<SpawnKind> Kinds { get; }

    /// <summary>Name in the legend.</summary>
    public string Name => Key switch
    {
        "Traders" => Loc.T("Spawn.Layer.Traders"),
        "Loot" => Loc.T("Spawn.Layer.Loot"),
        "Vehicles" => Loc.T("Spawn.Layer.Vehicles"),
        "Zombies" => Loc.T("Spawn.Layer.Zombies"),
        "Zones" => Loc.T("Spawn.Layer.Zones"),
        "Animals" => Loc.T("Spawn.Layer.Animals"),
        "LootZones" => Loc.T("Spawn.Layer.LootZones"),
        "Sentries" => Loc.T("Spawn.Layer.Sentries"),
        "Creatures" => Loc.T("Spawn.Layer.Creatures"),
        "CarShops" => Loc.T("Spawn.Layer.CarShops"),
        _ => Loc.T("Spawn.Layer.Drops"),
    };

    /// <summary>What the pins are, in a sentence.</summary>
    public string Description => Key switch
    {
        "Traders" => Loc.T("Spawn.Layer.Traders.Tip"),
        "Loot" => Loc.T("Spawn.Layer.Loot.Tip"),
        "Vehicles" => Loc.T("Spawn.Layer.Vehicles.Tip"),
        "Zombies" => Loc.T("Spawn.Layer.Zombies.Tip"),
        "Zones" => Loc.T("Spawn.Layer.Zones.Tip"),
        "Animals" => Loc.T("Spawn.Layer.Animals.Tip"),
        "LootZones" => Loc.T("Spawn.Layer.LootZones.Tip"),
        "Sentries" => Loc.T("Spawn.Layer.Sentries.Tip"),
        "Creatures" => Loc.T("Spawn.Layer.Creatures.Tip"),
        "CarShops" => Loc.T("Spawn.Layer.CarShops.Tip"),
        _ => Loc.T("Spawn.Layer.Drops.Tip"),
    };

    /// <summary>The map shows these pins.</summary>
    [ObservableProperty]
    private bool _isVisible;

    partial void OnIsVisibleChanged(bool value) => _changed();
}

/// <summary>One row of "what can spawn here".</summary>
/// <param name="Text">The preset, group or item.</param>
/// <param name="Detail">Chance and count (preset), branch (item), or null.</param>
/// <param name="Rarity">The game's rarity (<c>Common</c> ... <c>ExtremelyRare</c>), or null.</param>
/// <param name="Level">0 = preset or vehicle group, 1 = loot group, 2 = item.</param>
public sealed record SpawnInfoRow(string Text, string? Detail, string? Rarity, int Level)
{
    /// <summary>The rarity in the user's language, or empty.</summary>
    public string RarityText => Rarity switch
    {
        null => string.Empty,
        "Abundant" => Loc.T("Loot.Rarity.Abundant"),
        "Common" => Loc.T("Loot.Rarity.Common"),
        "Uncommon" => Loc.T("Loot.Rarity.Uncommon"),
        "Rare" => Loc.T("Loot.Rarity.Rare"),
        "VeryRare" => Loc.T("Loot.Rarity.VeryRare"),
        "ExtremelyRare" => Loc.T("Loot.Rarity.ExtremelyRare"),
        _ => Rarity,
    };

    /// <summary>Colour of the rarity (grey common to orange extremely rare).</summary>
    public string RarityHex => Rarity switch
    {
        "Abundant" => "#9AA0A6",
        "Common" => "#C8CCD0",
        "Uncommon" => "#5DBB63",
        "Rare" => "#4C8DFF",
        "VeryRare" => "#B36BFF",
        "ExtremelyRare" => "#FF9F2E",
        _ => "#C8CCD0",
    };

    /// <summary>True for a preset or vehicle group header.</summary>
    public bool IsHeader => Level == 0;

    /// <summary>True for a loot group line.</summary>
    public bool IsGroup => Level == 1;

    /// <summary>True for an item or vehicle.</summary>
    public bool IsItem => Level == 2;

    /// <summary>True when <see cref="Detail"/> has text.</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}
