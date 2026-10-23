using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Localization;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The spawn place picked on the Spawns map (owner: "see its details"): what it is, where it stands and which way it faces
/// (as the project has it), what it spawns, and the typed coordinates of a move that waits for Enter or Move here.
/// </summary>
public sealed partial class SpawnPointViewModel : ObservableObject
{
    private readonly Action<SpawnPointViewModel, string> _settingChanged;
    private readonly bool _ready;

    /// <summary>Creates the card of <paramref name="place"/> (marker <paramref name="id"/>) at <paramref name="value"/>, spawning one of <paramref name="choices"/>.</summary>
    public SpawnPointViewModel(int id, SpawnPlace place, TransformValue value, IReadOnlyList<string> choices, Action<SpawnPointViewModel, string> settingChanged)
    {
        Id = id;
        Place = place;
        Value = value;
        Actor = new ActorRef(SpawnPlaces.StaticDataPath, place.ActorName);
        _settingChanged = settingChanged;
        Choices = choices;
        _setting = choices.FirstOrDefault(c => c.Equals(place.Group, StringComparison.OrdinalIgnoreCase)) ?? (place.Group.Length > 0 ? place.Group : null);
        _xText = Metres(value.Location.X);
        _yText = Metres(value.Location.Y);
        _zText = Metres(value.Location.Z);
        _yawText = value.Rotation.Yaw.ToString("0.#", CultureInfo.CurrentCulture);
        _ready = true;
    }

    /// <summary>Its marker id on the map (index in the page's places).</summary>
    public int Id { get; }

    /// <summary>The place as the project has it.</summary>
    public SpawnPlace Place { get; }

    /// <summary>Where it stands and which way it faces (a zone's or area's size in the scale, metres).</summary>
    public TransformValue Value { get; }

    /// <summary>Its actor in the island's static data (what the edits name; the Map page shows the same name).</summary>
    public ActorRef Actor { get; }

    /// <summary>"VehicleSpawn_00123".</summary>
    public string Name => Place.ActorName;

    /// <summary>"Vehicle point".</summary>
    public string KindText => SpawnsPageViewModel.KindName(Place.Kind);

    /// <summary>Re-reads the texts composed here in the new UI language (review 3.5.0: the card kept the old one).</summary>
    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(CoordinatesText));
        OnPropertyChanged(nameof(SubZonesText));
    }

    /// <summary>The kind's icon.</summary>
    public string IconKey => SpawnsPageViewModel.KindIcon(Place.Kind);

    /// <summary>"X 1234.56 m · Y -567.80 m · Z 12.30 m · 90°".</summary>
    public string CoordinatesText => Loc.F("Spawns.Point.Where", Metres(Value.Location.X), Metres(Value.Location.Y), Metres(Value.Location.Z), Value.Rotation.Yaw.ToString("0.#", CultureInfo.CurrentCulture));

    /// <summary>
    /// A threat zone's sub-zones (boxes inside it with settings of their own: the game's, then the project's, such as the NPC
    /// area of a replaced robot), by settings, how many and their priority: "Sub-zones …: HTZ_Military_TV_Bunker_NPC ×3 (1)".
    /// Empty when it has none.
    /// </summary>
    public string SubZonesText => Place.SubZones.Count == 0 ? string.Empty
        : Loc.F("Spawns.Point.SubZones", string.Join(", ", Place.SubZones.GroupBy(s => (Name: SpawnPlaces.SettingName(s.Setting), s.Priority))
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key.Name} ×{g.Count()} ({g.Key.Priority})"))));

    /// <summary>It is a threat zone with sub-zones.</summary>
    public bool HasSubZones => Place.SubZones.Count > 0;

    /// <summary>What it can spawn instead (vehicle groups, zone settings, biomes); empty for a zombie / NPC point.</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>It has a choice of what it spawns (vehicles, zones, hunting areas).</summary>
    public bool HasChoices => SpawnChoices.PropertyOf(Place.Kind) is not null && Choices.Count > 0;

    /// <summary>A zombie / NPC point: it spawns what the threat zone around it draws.</summary>
    public bool SpawnsFromZone => SpawnChoices.PropertyOf(Place.Kind) is null;

    /// <summary>An indoor point: it keeps its height when moved (the terrain is not its floor).</summary>
    public bool IsIndoor => !SpawnPlaceMoves.SnapsToGround(Place);

    /// <summary>What it spawns; a change is recorded at once (one undo step).</summary>
    [ObservableProperty]
    private string? _setting;

    /// <summary>Typed X (metres) of a move.</summary>
    [ObservableProperty]
    private string _xText;

    /// <summary>Typed Y (metres) of a move.</summary>
    [ObservableProperty]
    private string _yText;

    /// <summary>Typed height (metres) of a move.</summary>
    [ObservableProperty]
    private string _zText;

    /// <summary>Typed turn (degrees) of a move.</summary>
    [ObservableProperty]
    private string _yawText;

    /// <summary>Why the typed values were not used; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    /// <summary>The typed values were refused.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>The typed values in centimetres and degrees; false when one is not a number.</summary>
    public bool TryRead(out float x, out float y, out float z, out float yaw)
    {
        (x, y, z, yaw) = (0f, 0f, 0f, 0f);
        if (!Number(XText, out var mx) || !Number(YText, out var my) || !Number(ZText, out var mz) || !Number(YawText, out var turn))
        {
            return false;
        }

        (x, y, z, yaw) = ((float)(mx * 100), (float)(my * 100), (float)(mz * 100), (float)turn);
        return true;
    }

    partial void OnSettingChanged(string? value)
    {
        if (_ready && value is not null)
        {
            _settingChanged(this, value);
        }
    }

    internal static string Metres(float cm) => (cm / 100.0).ToString("0.00", CultureInfo.CurrentCulture);

    // The user's own decimal sign, or a point.
    private static bool Number(string text, out double value) =>
        (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value))
        || (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value));
}
