using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.App.ViewModels;

/// <summary>One post in the guard tool's list.</summary>
/// <param name="Index">Its place in the set (0 based).</param>
/// <param name="Post">The post.</param>
public sealed record GuardPostRow(int Index, GuardPost Post)
{
    /// <summary><c>#3  guard 4  (456300, -234600)  Z 83.0 m</c>.</summary>
    public string Text => Loc.F("Map.GuardPosts.PostRow", Index + 1, Post.Level, Post.Position.X.ToString("0", CultureInfo.InvariantCulture),
        Post.Position.Y.ToString("0", CultureInfo.InvariantCulture), (Post.Position.Z / 100.0).ToString("0.0", CultureInfo.InvariantCulture));
}

/// <summary>Where the height of a new guard post comes from.</summary>
public enum GuardFloorMode
{
    /// <summary>Found under the click: the top floor with a ceiling over it (inside a building), else the ground (<see cref="GuardFloors"/>).</summary>
    Auto,

    /// <summary>Where the cursor hits the map: the ground, a floor or a roof.</summary>
    Surface,

    /// <summary>One height typed in for every click.</summary>
    Fixed,
}

/// <summary>
/// The guard tool of the Map page (owner, 2026-10-26: "a constructor for spawns like the dungeon's guards, placed on the 3D model
/// of the map, because on the flat map it is not clear where you put them"; then: "find the height ourselves" and "I want to be
/// able to change the points"). A click on the 3D map puts a post; its height is found under the click (the top floor with a
/// ceiling over it, else the ground) and can be stepped to another floor; a post is picked by clicking its pole and dragged along
/// its floor, or set by its X, Y and Z. The posts are sets (<see cref="GuardSpawnSet"/>) in the project's journal (one undo step per
/// edit) and become a server Lua mod (<see cref="GuardSpawns.Generate"/>) written to the owner's server. Nothing goes into the pak.
/// </summary>
public sealed partial class MapPageViewModel
{
    private const float GuardMoveThresholdCm = 20f;
    private const long GuardFloorsHoverTicks = TimeSpan.TicksPerMillisecond * 120;

    private bool _guardSync;
    private string? _pendingGuardName;
    private FVector? _guardDab;
    private FVector? _guardHover;
    private (string Set, int Index)? _guardGrab;
    private FVector? _guardGrabAt;
    private FVector? _guardDragTo;
    private (float X, float Y, long Ticks, (IReadOnlyList<float> Floors, int Default) Found)? _guardFloorsCache;

    /// <summary>The guard tool is on: a click on the 3D map puts a post there.</summary>
    [ObservableProperty]
    private bool _guardTool;

    /// <summary>The project's guard sets (and the new one that has no post yet).</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _guardSetNames = [];

    /// <summary>The set being edited.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteGuardSetCommand), nameof(RemoveGuardPostCommand), nameof(InstallGuardModCommand), nameof(WriteGuardFileCommand), nameof(SnapGuardPostCommand))]
    private string? _selectedGuardSet;

    /// <summary>The guard level of the next post (3 weakest, 5 strongest).</summary>
    [ObservableProperty]
    private int _guardLevel = 4;

    /// <summary>Where the height of a new post comes from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuardFloorAuto), nameof(IsGuardFloorSurface), nameof(IsGuardFloorFixed))]
    private GuardFloorMode _guardFloorMode = GuardFloorMode.Auto;

    /// <summary>How many floors above (positive) or below (negative) the default one the next post goes.</summary>
    [ObservableProperty]
    private int _guardFloorStep;

    /// <summary>"Floor 2 of 3: 83.0 m", or that none was found, for the floor found under the cursor.</summary>
    [ObservableProperty]
    private string _guardFloorText = string.Empty;

    /// <summary>The fixed floor height (metres, the game's Z).</summary>
    [ObservableProperty]
    private double _guardFloorMeters = 83;

    /// <summary>The posts of the set, in the order they were put.</summary>
    [ObservableProperty]
    private IReadOnlyList<GuardPostRow> _guardPostRows = [];

    /// <summary>The picked post.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveGuardPostCommand), nameof(SnapGuardPostCommand))]
    private GuardPostRow? _selectedGuardPost;

    /// <summary>The picked post's guard level (changing it is an edit).</summary>
    [ObservableProperty]
    private int _selectedGuardPostLevel = 4;

    /// <summary>The picked post's X (cm; changing it moves the post).</summary>
    [ObservableProperty]
    private double _selectedGuardPostX;

    /// <summary>The picked post's Y (cm).</summary>
    [ObservableProperty]
    private double _selectedGuardPostY;

    /// <summary>The picked post's Z (cm).</summary>
    [ObservableProperty]
    private double _selectedGuardPostZ;

    /// <summary>How far outside the posts' area a player wakes the guards (metres).</summary>
    [ObservableProperty]
    private double _guardReachMeters = GuardSpawns.DefaultReach / 100.0;

    /// <summary>The guards leave when nobody has been near for this long (seconds).</summary>
    [ObservableProperty]
    private double _guardIdleSeconds = GuardSpawns.DefaultIdleSeconds;

    /// <summary>A killed guard's post is filled again after this long (seconds).</summary>
    [ObservableProperty]
    private double _guardRespawnSeconds = GuardSpawns.DefaultRespawnSeconds;

    /// <summary>What the 3D map draws for the tool: the posts and the circle that wakes the set.</summary>
    [ObservableProperty]
    private GuardOverlay? _guardOverlay;

    /// <summary>The guard levels a post can have.</summary>
    public IReadOnlyList<int> GuardLevels { get; } = Enumerable.Range(GuardSpawns.MinLevel, GuardSpawns.MaxLevel - GuardSpawns.MinLevel + 1).ToList();

    /// <summary>"Posts: 8".</summary>
    public string GuardPostsText => Loc.F("Map.GuardPosts.Posts", GuardPostRows.Count);

    /// <summary>The floor is found under the click (switch for the toggle).</summary>
    public bool IsGuardFloorAuto
    {
        get => GuardFloorMode == GuardFloorMode.Auto;
        set => PickGuardFloorMode(value, GuardFloorMode.Auto);
    }

    /// <summary>The post stands where the cursor hits the map (switch for the toggle).</summary>
    public bool IsGuardFloorSurface
    {
        get => GuardFloorMode == GuardFloorMode.Surface;
        set => PickGuardFloorMode(value, GuardFloorMode.Surface);
    }

    /// <summary>One typed floor height is used (switch for the toggle).</summary>
    public bool IsGuardFloorFixed
    {
        get => GuardFloorMode == GuardFloorMode.Fixed;
        set => PickGuardFloorMode(value, GuardFloorMode.Fixed);
    }

    private void PickGuardFloorMode(bool on, GuardFloorMode mode)
    {
        if (on)
        {
            GuardFloorMode = mode;
        }
    }

    /// <summary>The picked set has posts in the project.</summary>
    private bool HasGuardSet => CurrentGuardSet() is not null;

    partial void OnGuardToolChanged(bool value)
    {
        if (value)
        {
            BrushSelect = false; // one tool at a time under the cursor
            HoleTool = false;
        }

        _guardDab = null;
        _guardGrab = null;
        _guardDragTo = null;
        OnPropertyChanged(nameof(ViewportBrushMode));
        OnPropertyChanged(nameof(BrushOnGround));
        RefreshGuardSets();
    }

    partial void OnSelectedGuardSetChanged(string? value)
    {
        if (!_guardSync)
        {
            RefreshGuardSets();
        }
    }

    partial void OnSelectedGuardPostChanged(GuardPostRow? value)
    {
        if (_guardSync)
        {
            return;
        }

        if (value is not null)
        {
            _guardSync = true;
            ShowGuardPost(value.Post);
            _guardSync = false;
        }

        RefreshGuardOverlay();
    }

    private void ShowGuardPost(GuardPost post)
    {
        SelectedGuardPostLevel = post.Level;
        SelectedGuardPostX = Math.Round(post.Position.X, 2);
        SelectedGuardPostY = Math.Round(post.Position.Y, 2);
        SelectedGuardPostZ = Math.Round(post.Position.Z, 2);
    }

    partial void OnGuardFloorModeChanged(GuardFloorMode value)
    {
        _guardFloorsCache = null;
        RefreshGuardOverlay();
    }

    partial void OnGuardFloorStepChanged(int value)
    {
        RefreshGuardOverlay();
    }

    partial void OnGuardFloorMetersChanged(double value) => RefreshGuardOverlay();

    partial void OnSelectedGuardPostLevelChanged(int value)
    {
        if (_guardSync || SelectedGuardPost is not { } row || CurrentGuardSet() is not { } set || row.Index >= set.Posts.Count || set.Posts[row.Index].Level == value)
        {
            return;
        }

        var posts = set.Posts.ToList();
        posts[row.Index] = posts[row.Index] with { Level = value };
        ApplyGuardSet(set.Name, set with { Posts = posts }, row.Index);
    }

    partial void OnSelectedGuardPostXChanged(double value) => MovePickedGuardPost(value, null, null);

    partial void OnSelectedGuardPostYChanged(double value) => MovePickedGuardPost(null, value, null);

    partial void OnSelectedGuardPostZChanged(double value) => MovePickedGuardPost(null, null, value);

    private void MovePickedGuardPost(double? x, double? y, double? z)
    {
        if (_guardSync || SelectedGuardPost is not { } row || CurrentGuardSet() is not { } set || row.Index >= set.Posts.Count)
        {
            return;
        }

        var old = set.Posts[row.Index].Position;
        var moved = new FVector((float)(x ?? old.X), (float)(y ?? old.Y), (float)(z ?? old.Z));
        if (moved.Equals(old))
        {
            return;
        }

        MoveGuardPost(set, row.Index, moved);
    }

    partial void OnGuardReachMetersChanged(double value) => ChangeGuardTiming(set => set with { Reach = (float)Math.Clamp(value, 0.0, 1000.0) * 100f });

    partial void OnGuardIdleSecondsChanged(double value) => ChangeGuardTiming(set => set with { IdleSeconds = (int)Math.Clamp(value, 10.0, 86_400.0) });

    partial void OnGuardRespawnSecondsChanged(double value) => ChangeGuardTiming(set => set with { RespawnSeconds = (int)Math.Clamp(value, 10.0, 86_400.0) });

    private void ChangeGuardTiming(Func<GuardSpawnSet, GuardSpawnSet> change)
    {
        if (_guardSync || CurrentGuardSet() is not { } set)
        {
            return;
        }

        var next = change(set);
        if (!next.Equals(set))
        {
            ApplyGuardSet(set.Name, next, SelectedGuardPost?.Index);
        }
    }

    private GuardSpawnSet? CurrentGuardSet() =>
        SelectedGuardSet is { } name ? _services.Projects.Current?.State.GetGuardSet(name) : null;

    // ------------------------------------------------------------------ the height of a post

    /// <summary>A floor higher: the next post, and the cursor's ghost, use it.</summary>
    [RelayCommand]
    private void GuardFloorUp() => GuardFloorStep++;

    /// <summary>A floor lower.</summary>
    [RelayCommand]
    private void GuardFloorDown() => GuardFloorStep--;

    /// <summary>
    /// Every surface a vertical line through (<paramref name="x"/>, <paramref name="y"/>) meets in the shown scene, from the top
    /// down: what is drawn (roofs, floors, bridges, rocks) and the landscape, the way "Fit to ground" sees it.
    /// </summary>
    internal IReadOnlyList<float> SurfacesAt(float x, float y)
    {
        var surfaces = new List<float>();
        if (PreparedScene is null)
        {
            return surfaces;
        }

        var (cx, cy) = SupportCellOf(x, y);
        var grid = Supports([], [], (cx, cy, cx, cy));
        var from = 10_000_000f;
        for (var i = 0; i < 24; i++)
        {
            if (SurfaceAtOrBelow(grid, x, y, from) is not { } z)
            {
                break;
            }

            surfaces.Add(z);
            from = z - 1f;
        }

        return surfaces;
    }

    private float? SurfaceAtOrBelow(SupportGrid supports, float x, float y, float fromZ)
    {
        float? best = GroundAt(x, y) is { } ground && ground <= fromZ ? ground : null;
        if (!supports.TryGetValue(SupportCellOf(x, y), out var here))
        {
            return best;
        }

        foreach (var s in here)
        {
            if (x < s.Box.Min.X || x > s.Box.Max.X || y < s.Box.Min.Y || y > s.Box.Max.Y || s.Box.Min.Z > fromZ || best >= s.Box.Max.Z)
            {
                continue;
            }

            if (SurfaceOf(s.Mesh, s.Bend)?.HighestBelow(s.World, x, y, fromZ) is { } z && !(best >= z))
            {
                best = z;
            }
        }

        return best;
    }

    /// <summary>The floors under a point and the default one (see <see cref="GuardFloors"/>); with <paramref name="fresh"/> false a recent answer for the same spot is reused (the cursor moves a lot).</summary>
    private (IReadOnlyList<float> Floors, int Default) GuardFloorsAt(float x, float y, bool fresh)
    {
        var now = DateTime.UtcNow.Ticks;
        if (!fresh && _guardFloorsCache is { } cached && now - cached.Ticks < GuardFloorsHoverTicks && Math.Abs(cached.X - x) < 25f && Math.Abs(cached.Y - y) < 25f)
        {
            return cached.Found;
        }

        var found = GuardFloors.Choose(SurfacesAt(x, y));
        _guardFloorsCache = (x, y, now, found);
        return found;
    }

    /// <summary>The height a post put at <paramref name="at"/> gets in the chosen mode (the ground point's own height when no floor is found).</summary>
    private float GuardPostZ(FVector at, bool fresh)
    {
        switch (GuardFloorMode)
        {
            case GuardFloorMode.Fixed:
                return (float)(GuardFloorMeters * 100.0);
            case GuardFloorMode.Surface:
                return at.Z;
        }

        var (floors, @default) = GuardFloorsAt(at.X, at.Y, fresh);
        if (floors.Count == 0)
        {
            GuardFloorText = Loc.T("Map.GuardPosts.FloorNone");
            return at.Z;
        }

        var index = Math.Clamp(@default - GuardFloorStep, 0, floors.Count - 1);
        GuardFloorText = Loc.F("Map.GuardPosts.FloorOf", floors.Count - index, floors.Count, (floors[index] / 100.0).ToString("0.0", CultureInfo.InvariantCulture));
        return floors[index];
    }

    // ------------------------------------------------------------------ the cursor

    /// <summary>The cursor is over the map at <paramref name="at"/>: a half transparent post shows where a click would put the next.</summary>
    internal void HoverGuard(FVector at)
    {
        if (!GuardTool)
        {
            return;
        }

        _guardHover = at;
        RefreshGuardOverlay();
    }

    /// <summary>A click or drag with the tool on empty ground: the post goes where the stroke ends (<see cref="EndGuardStroke"/>).</summary>
    internal void NoteGuardDab(FVector at) => _guardDab = at;

    /// <summary>A press landed on a post's pole: it is picked (and dragged while the button is held).</summary>
    internal void GrabGuardPost(string set, int index)
    {
        if (_services.Projects.Current?.State.GetGuardSet(set) is not { } found || index < 0 || index >= found.Posts.Count)
        {
            return;
        }

        _guardGrab = (found.Name, index);
        _guardGrabAt = found.Posts[index].Position;
        _guardDragTo = null;
        if (!string.Equals(SelectedGuardSet, found.Name, StringComparison.OrdinalIgnoreCase))
        {
            SelectedGuardSet = found.Name;
        }

        SelectedGuardPost = GuardPostRows.ElementAtOrDefault(index);
        RefreshGuardOverlay();
    }

    /// <summary>The grabbed post is dragged to <paramref name="at"/> (on its own floor plane).</summary>
    internal void DragGuardPost(FVector at)
    {
        if (_guardGrab is null)
        {
            return;
        }

        _guardDragTo = at;
        RefreshGuardOverlay();
    }

    /// <summary>The click ends: a grabbed post that was dragged moves there, one only clicked stays picked, else one new post goes at the last point the cursor touched.</summary>
    internal void EndGuardStroke()
    {
        if (!GuardTool)
        {
            return;
        }

        if (_guardGrab is { } grab)
        {
            var (to, from) = (_guardDragTo, _guardGrabAt);
            _guardGrab = null;
            _guardGrabAt = null;
            _guardDragTo = null;
            if (to is { } target && from is { } origin && Math.Abs(target.X - origin.X) + Math.Abs(target.Y - origin.Y) > GuardMoveThresholdCm
                && _services.Projects.Current?.State.GetGuardSet(grab.Set) is { } set && grab.Index < set.Posts.Count)
            {
                MoveGuardPost(set, grab.Index, target);
            }
            else
            {
                RefreshGuardOverlay();
            }

            return;
        }

        if (_guardDab is { } at)
        {
            _guardDab = null;
            PlaceGuardPost(at);
        }
    }

    /// <summary>Puts a post at <paramref name="at"/> (at the height the floor mode finds) into the picked set, or into a new one.</summary>
    internal void PlaceGuardPost(FVector at)
    {
        if (_services.Projects.Current?.State is not { } state)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.GuardPosts.NoProject"));
            return;
        }

        var name = SelectedGuardSet ?? _pendingGuardName ?? GuardSpawns.NewName(state.GuardSets.Keys);
        var post = new GuardPost(new FVector(at.X, at.Y, GuardPostZ(at, fresh: true)), GuardLevel);
        var existing = state.GetGuardSet(name);
        if (existing?.Posts.Count >= GuardSpawns.MaxPosts)
        {
            _services.Notifications.Warning(Loc.T("Map.GuardPosts.Title"), Loc.F("Map.GuardPosts.Full", GuardSpawns.MaxPosts));
            return;
        }

        var next = existing is null ? new GuardSpawnSet(name, [post]) : existing with { Posts = [.. existing.Posts, post] };
        ApplyGuardSet(name, next, next.Posts.Count - 1);
    }

    /// <summary>Moves post <paramref name="index"/> of <paramref name="set"/> to <paramref name="to"/>: one undo step.</summary>
    private void MoveGuardPost(GuardSpawnSet set, int index, FVector to)
    {
        var posts = set.Posts.ToList();
        posts[index] = posts[index] with { Position = to };
        ApplyGuardSet(set.Name, set with { Posts = posts }, index);
    }

    /// <summary>Puts the picked post on the floor found under it now (the auto floor, as many floors up or down as the stepper says).</summary>
    [RelayCommand(CanExecute = nameof(CanSnapGuardPost))]
    private void SnapGuardPost()
    {
        if (CurrentGuardSet() is not { } set || SelectedGuardPost is not { } row || row.Index >= set.Posts.Count)
        {
            return;
        }

        var at = set.Posts[row.Index].Position;
        var (floors, @default) = GuardFloorsAt(at.X, at.Y, fresh: true);
        if (floors.Count == 0)
        {
            return;
        }

        var z = floors[Math.Clamp(@default - GuardFloorStep, 0, floors.Count - 1)];
        if (Math.Abs(z - at.Z) > 0.5f)
        {
            MoveGuardPost(set, row.Index, at with { Z = z });
        }
    }

    private bool CanSnapGuardPost() => SelectedGuardPost is not null && HasGuardSet;

    /// <summary>Takes the picked post out of the set (the last one takes the set away): one undo step.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveGuardPost))]
    private void RemoveGuardPost()
    {
        if (CurrentGuardSet() is not { } set || SelectedGuardPost is not { } row || row.Index >= set.Posts.Count)
        {
            return;
        }

        var posts = set.Posts.Where((_, i) => i != row.Index).ToList();
        ApplyGuardSet(set.Name, posts.Count == 0 ? null : set with { Posts = posts }, Math.Min(row.Index, posts.Count - 1));
    }

    private bool CanRemoveGuardPost() => SelectedGuardPost is not null && HasGuardSet;

    /// <summary>The next click starts another set.</summary>
    [RelayCommand]
    private void NewGuardSet()
    {
        var taken = (_services.Projects.Current?.State.GuardSets.Keys ?? Enumerable.Empty<string>()).Append(_pendingGuardName ?? string.Empty);
        _pendingGuardName = GuardSpawns.NewName(taken);
        SelectedGuardSet = _pendingGuardName;
        RefreshGuardSets();
    }

    /// <summary>Removes the whole set: one undo step.</summary>
    [RelayCommand(CanExecute = nameof(HasGuardSet))]
    private void DeleteGuardSet()
    {
        if (CurrentGuardSet() is { } set)
        {
            ApplyGuardSet(set.Name, null, null);
        }
    }

    /// <summary>Writes the guard mod into the server's UE4SS Mods folder and switches it on.</summary>
    [RelayCommand(CanExecute = nameof(HasGuardSets))]
    private void InstallGuardMod()
    {
        if (_services.Projects.Current?.State is not { } state || state.GuardSets.Count == 0)
        {
            return;
        }

        if (ServerInstall.Find(_services.Settings.Load().ServerPaksFolder) is not { } server)
        {
            _services.Notifications.Warning(Loc.T("Map.GuardPosts.Title"), Loc.T("Map.GuardPosts.NoServer"));
            return;
        }

        try
        {
            var mod = new ServerModStore(server).Save(GuardSpawns.ModName, GuardSpawns.Generate(state.GuardSets.Values));
            _services.Notifications.Success(Loc.T("Map.GuardPosts.Title"), Loc.F("Map.GuardPosts.Installed", mod.ScriptPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _services.Notifications.Error(Loc.T("Map.GuardPosts.Title"), ex.Message);
        }
    }

    /// <summary>Writes the mod's main.lua into the project's folder (for a server the app cannot reach).</summary>
    [RelayCommand(CanExecute = nameof(HasGuardSets))]
    private void WriteGuardFile()
    {
        if (_services.Projects.Current is not { } project || project.State.GuardSets.Count == 0)
        {
            return;
        }

        try
        {
            var path = Path.Combine(project.DirectoryPath, GuardSpawns.ModName, "Scripts", "main.lua");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, GuardSpawns.Generate(project.State.GuardSets.Values), new System.Text.UTF8Encoding(false));
            _services.Notifications.Success(Loc.T("Map.GuardPosts.Title"), Loc.F("Map.GuardPosts.Written", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Map.GuardPosts.Title"), ex.Message);
        }
    }

    private bool HasGuardSets() => _services.Projects.Current?.State.GuardSets.Count > 0;

    /// <summary>One edit that sets <paramref name="name"/> to <paramref name="next"/> (null removes it); <paramref name="pick"/> is the post to pick afterwards.</summary>
    private void ApplyGuardSet(string name, GuardSpawnSet? next, int? pick)
    {
        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Map.GuardPosts.NoProject"));
            return;
        }

        try
        {
            _services.Projects.Apply(new SetGuardSpawnsOp(name, project.State.GetGuardSet(name), next));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Loc.T("Map.GuardPosts.Title"), ex.Message);
            RefreshGuardSets();
            return;
        }

        _pendingGuardName = null;
        _guardSync = true;
        SelectedGuardSet = next is null ? null : name;
        _guardSync = false;
        RefreshGuardSets(pick);
    }

    /// <summary>Follows the project (an edit, undo or redo): the sets, the picked set's posts and timings, and what the map draws.</summary>
    private void RefreshGuardSets(int? pick = null)
    {
        var state = _services.Projects.Current?.State;
        var sets = state?.GuardSets ?? new Dictionary<string, GuardSpawnSet>();
        _guardSync = true;
        try
        {
            var names = sets.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (_pendingGuardName is not null && !names.Contains(_pendingGuardName, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(_pendingGuardName);
            }

            if (!GuardSetNames.SequenceEqual(names))
            {
                GuardSetNames = names;
            }

            if (SelectedGuardSet is null || !names.Contains(SelectedGuardSet, StringComparer.OrdinalIgnoreCase))
            {
                SelectedGuardSet = names.Count > 0 ? names[0] : null;
            }

            var set = SelectedGuardSet is { } picked && sets.TryGetValue(picked, out var found) ? found : null;
            var rows = set?.Posts.Select((p, i) => new GuardPostRow(i, p)).ToList() ?? [];
            GuardPostRows = rows;
            SelectedGuardPost = pick is { } index && index >= 0 && index < rows.Count ? rows[index]
                : SelectedGuardPost is { } old && old.Index < rows.Count ? rows[old.Index] : rows.FirstOrDefault();
            if (SelectedGuardPost is { } now)
            {
                ShowGuardPost(now.Post);
            }
            else
            {
                SelectedGuardPostLevel = GuardLevel;
            }

            GuardReachMeters = (set?.Reach ?? GuardSpawns.DefaultReach) / 100.0;
            GuardIdleSeconds = set?.IdleSeconds ?? GuardSpawns.DefaultIdleSeconds;
            GuardRespawnSeconds = set?.RespawnSeconds ?? GuardSpawns.DefaultRespawnSeconds;
        }
        finally
        {
            _guardSync = false;
        }

        OnPropertyChanged(nameof(GuardPostsText));
        DeleteGuardSetCommand.NotifyCanExecuteChanged();
        RemoveGuardPostCommand.NotifyCanExecuteChanged();
        SnapGuardPostCommand.NotifyCanExecuteChanged();
        InstallGuardModCommand.NotifyCanExecuteChanged();
        WriteGuardFileCommand.NotifyCanExecuteChanged();
        RefreshGuardOverlay();
    }

    /// <summary>The 3D map's drawing: every set's posts (the picked set bright), the picked post white, the circle that wakes the picked set, the ghost under the cursor.</summary>
    private void RefreshGuardOverlay()
    {
        if (!GuardTool)
        {
            GuardOverlay = null;
            return;
        }

        var markers = new List<GuardMarker>();
        var sets = _services.Projects.Current?.State.GuardSets;
        foreach (var (name, set) in sets ?? new Dictionary<string, GuardSpawnSet>())
        {
            var mine = string.Equals(name, SelectedGuardSet, StringComparison.OrdinalIgnoreCase);
            for (var i = 0; i < set.Posts.Count; i++)
            {
                var kind = !mine ? GuardMarkerKind.Other : SelectedGuardPost?.Index == i ? GuardMarkerKind.Selected : GuardMarkerKind.Normal;
                var at = _guardGrab is { } grab && grab.Index == i && string.Equals(grab.Set, name, StringComparison.OrdinalIgnoreCase) && _guardDragTo is { } dragged
                    ? dragged
                    : set.Posts[i].Position;
                markers.Add(new GuardMarker(at, set.Posts[i].Level, kind, set.Name, i));
            }
        }

        // The ghost: where a click would put the next post, at the height that click would find (not while a post is dragged).
        if (_guardHover is { } hover && _guardGrab is null)
        {
            markers.Add(new GuardMarker(new FVector(hover.X, hover.Y, GuardPostZ(hover, fresh: false)), GuardLevel, GuardMarkerKind.Ghost));
        }

        var current = CurrentGuardSet();
        FVector? centre = null;
        if (current is not null)
        {
            var (cx, cy) = current.Center;
            centre = new FVector(cx, cy, current.Posts.Average(p => p.Position.Z));
        }

        GuardOverlay = new GuardOverlay(markers, centre, current?.WakeRadius ?? 0f);
    }
}
