using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// How a built craftable behaves (owner, 2026-10-17, his "Base Cabin 03"): built like the game's own element (a source that
/// is a game base element, made Furniture before 3.5, switches in one click), whether it needs a flag, what a repair
/// costs and whether it is placed in front of the player (experimental) (<see cref="Craftable.NeedsFlag"/>,
/// <see cref="Craftable.RepairCostPercent"/>, <see cref="Craftable.InFront"/>).
/// </summary>
public sealed partial class CraftableRowViewModel
{
    private bool? _needsFlag;
    private bool _needsFlagSet;
    private float? _repairCost;
    private bool _repairCostSet;
    private InFrontSettings? _inFront;
    private bool _inFrontSet;

    /// <summary>The names whose values follow the kind and the element family.</summary>
    internal static readonly string[] BuildingNames =
        [nameof(CanBeLikeGame), nameof(LikeGameText), nameof(BorrowsRecipe), nameof(BorrowedText), nameof(GameNeedsFlag), nameof(NeedsFlag), nameof(NeedsFlagStock), nameof(IsLargeTemplate), nameof(LargeTemplateCaption),
         nameof(InFrontOffered), nameof(PlaceInFront), nameof(InFrontSuggested)];

    /// <summary>The names of "Place in front".</summary>
    private static readonly string[] InFrontNames =
        [nameof(PlaceInFront), nameof(InFrontMeters), nameof(InFrontAxisIndex), nameof(InFrontSuggested)];

    /// <summary>
    /// Its source is a base element of the game (or shows one) but it is built on a template (Furniture, Station): one
    /// click makes it a clone of that element's own family.
    /// </summary>
    public bool CanBeLikeGame => Kind is CraftKind.Furniture or CraftKind.Station && Element is not null;

    /// <summary>"This is the game's BP_Base_Cabin_03: build it like the game's …".</summary>
    public string LikeGameText => Element is { } element ? Loc.F("Craftables.Building.LikeGame", ElementEditorViewModel.Leaf(element.Family.Element)) : string.Empty;

    /// <summary>The game ships no recipe for its element: a copy of the chair's builds it (<see cref="ElementFamily.BorrowsRecipe"/>).</summary>
    public bool BorrowsRecipe => IsElement && Element?.Family.BorrowsRecipe == true;

    /// <summary>"The game ships no recipe for BP_Base_Cabin_03 …".</summary>
    public string BorrowedText => Element is { } element ? Loc.F("Craftables.Building.Borrowed", ElementEditorViewModel.Leaf(element.Family.Element)) : string.Empty;

    /// <summary>
    /// The game's answer for its element: a flag is needed unless the element stores <c>_canBePlacedWithoutFlag</c> true
    /// (the shelter); the chair and the workbench store none (C++ default false).
    /// </summary>
    public bool GameNeedsFlag => !(IsElement && Element?.Family.Levels.FirstOrDefault()?.Values.FirstOrDefault(v => v.Path == "_canBePlacedWithoutFlag") is { } stored
        && TunableValue.ParseBool(stored.Value));

    /// <summary>Building it needs a flag area (the game's answer until changed).</summary>
    public bool NeedsFlag
    {
        get => NeedsFlagModel ?? GameNeedsFlag;
        set
        {
            if (value == NeedsFlag)
            {
                return;
            }

            _needsFlagSet = true;
            _needsFlag = value == GameNeedsFlag ? null : value;
            OnPropertyChanged(nameof(NeedsFlag));
            Changed();
        }
    }

    /// <summary>"Game: ✓" (needs a flag) or "Game: ✗".</summary>
    public string NeedsFlagStock => Loc.F("Craftables.Element.Stock", GameNeedsFlag ? "✓" : "✗");

    /// <summary>What a repair costs, in percent of the game's (100 = the game's).</summary>
    public decimal RepairCost
    {
        get => (decimal)(RepairCostModel ?? 100);
        set
        {
            var percent = Math.Clamp(Math.Round(value), 10, 1000);
            if (percent == RepairCost)
            {
                return;
            }

            _repairCostSet = true;
            _repairCost = percent == 100 ? null : (float)percent;
            OnPropertyChanged(nameof(RepairCost));
            Changed();
        }
    }

    /// <summary>"Game: 100".</summary>
    public string RepairCostStock => Loc.F("Craftables.Element.Stock", 100.ToString(CultureInfo.CurrentCulture));

    /// <summary>A furniture or station craftable this large is placed like the game's large buildings (<see cref="CraftablesPlanner.LargeBuildingMeters"/>).</summary>
    public bool IsLargeTemplate => Kind is CraftKind.Furniture or CraftKind.Station && SizeMeters >= CraftablesPlanner.LargeBuildingMeters;

    /// <summary>
    /// What <see cref="IsLargeTemplate"/> means for it: furniture copies the Large Log Shed's recipe, a station keeps the workbench's
    /// (a station's); both get the drawn-in placement check (review 3.5.1: the caption promised stations the shed's recipe).
    /// </summary>
    public string LargeTemplateCaption => Loc.T(Kind == CraftKind.Station ? "Craftables.Building.LargeStation" : "Craftables.Building.Large");

    /// <summary>The direction items of <see cref="InFrontAxisIndex"/>, in <see cref="InFrontAxis"/> order.</summary>
    public static IReadOnlyList<string> InFrontAxes { get; } = ["+X", "−X", "+Y", "−Y"];

    /// <summary>
    /// Placed in front of the player instead of around the point he aims at (experimental, <see cref="Craftable.InFront"/>):
    /// off by default, suggested from <see cref="CraftablesPlanner.LargeBuildingMeters"/> (<see cref="InFrontSuggested"/>).
    /// </summary>
    public bool PlaceInFront
    {
        get => InFrontOffered && InFrontModel is not null;
        set
        {
            if (value == PlaceInFront)
            {
                return;
            }

            SetInFront(value ? new InFrontSettings() : null);
        }
    }

    /// <summary>The distance in metres: the automatic one (<see cref="InFrontAuto"/>) until changed.</summary>
    public decimal InFrontMeters
    {
        get => (decimal)Math.Round(InFrontModel?.Meters ?? CraftablesPlanner.AutoInFrontMeters(SizeMeters), 1);
        set
        {
            var meters = (float)Math.Clamp(Math.Round(value, 1), 0, (decimal)CraftablesPlanner.InFrontMaxMeters);
            if (InFrontModel is not { } inFront || (decimal)meters == InFrontMeters)
            {
                return;
            }

            SetInFront(inFront with { Meters = Math.Abs(meters - CraftablesPlanner.AutoInFrontMeters(SizeMeters)) < 0.01f ? null : meters });
        }
    }

    /// <summary>The direction (<see cref="InFrontAxes"/>): +X unless a game test shows another.</summary>
    public int InFrontAxisIndex
    {
        get => (int)(InFrontModel?.Axis ?? InFrontAxis.PlusX);
        set
        {
            if (InFrontModel is not { } inFront || value < 0 || value >= InFrontAxes.Count || value == InFrontAxisIndex)
            {
                return;
            }

            SetInFront(inFront with { Axis = (InFrontAxis)value });
        }
    }

    /// <summary>"Automatic for its size: 8.5 m".</summary>
    public string InFrontAuto => Loc.F("Craftables.Building.InFront.Auto", CraftablesPlanner.AutoInFrontMeters(SizeMeters).ToString("0.#", CultureInfo.CurrentCulture));

    /// <summary>
    /// "Place in front" is offered only where nothing else keeps a big ghost off the player: not for large furniture and not
    /// for an element built on the Large Log Shed's recipe (<see cref="CraftablesPlanner.LargeBuildingRecipe"/>: the ghost's
    /// walls are checked, not its box, so the player may stand inside it).
    /// </summary>
    public bool InFrontOffered => !IsLargeTemplate && !BorrowsRecipe;

    /// <summary>Offered, off, and large enough that the game's placement would surround the player.</summary>
    public bool InFrontSuggested => InFrontOffered && !PlaceInFront && CraftablesPlanner.AutoInFrontMeters(SizeMeters) > 0;

    /// <summary>"Suggested for its size (15 m)".</summary>
    public string InFrontSuggestion => Loc.F("Craftables.Building.InFront.Suggested", SizeMeters.ToString("0.#", CultureInfo.CurrentCulture));

    private bool? NeedsFlagModel => _needsFlagSet ? _needsFlag : _source.NeedsFlag;

    private float? RepairCostModel => _repairCostSet ? _repairCost : _source.RepairCostPercent;

    private InFrontSettings? InFrontModel => _inFrontSet ? _inFront : _source.InFront;

    private void SetInFront(InFrontSettings? value)
    {
        _inFrontSet = true;
        _inFront = value;
        foreach (var name in InFrontNames)
        {
            OnPropertyChanged(name);
        }

        Changed();
    }

    /// <summary>Makes it a clone of its game element's own family (<see cref="CanBeLikeGame"/>); the kind warning follows.</summary>
    [RelayCommand]
    private void BuildLikeGame()
    {
        if (CanBeLikeGame)
        {
            Kind = CraftKind.Element;
        }
    }

    partial void OnElementChanged(ElementEditorViewModel? value) => NotifyBuilding();

    private void NotifyBuilding()
    {
        foreach (var name in BuildingNames)
        {
            OnPropertyChanged(name);
        }
    }
}
