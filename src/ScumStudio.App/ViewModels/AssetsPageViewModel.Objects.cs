using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;

namespace ScumStudio.App.ViewModels;

/// <summary>A category of the Objects view (Furniture › Tables, Nature › Rocks …) and how many objects it holds.</summary>
public sealed partial class ObjectCategoryNode : ViewModelBase
{
    /// <summary>Creates a node covering the catalogue categories <paramref name="ids"/> (and their sub-categories).</summary>
    public ObjectCategoryNode(string title, IReadOnlyList<string> ids, IReadOnlyList<ObjectCategoryNode> children, int count)
    {
        Title = title;
        Ids = ids;
        Children = children;
        Count = count;
    }

    /// <summary>Category title.</summary>
    public string Title { get; }

    /// <summary>Catalogue category ids this node lists.</summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary>Sub-categories that hold objects.</summary>
    public IReadOnlyList<ObjectCategoryNode> Children { get; }

    /// <summary>Objects in and below the category.</summary>
    public int Count { get; }

    /// <summary>A building set (walls, roads, bridges) picks its objects by name across the categories instead.</summary>
    public Func<DumpPackage, bool>? Match { get; init; }

    /// <summary><see cref="Count"/> formatted.</summary>
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>True when <paramref name="package"/> belongs to this node.</summary>
    public bool Contains(DumpPackage package) => Match?.Invoke(package)
        ?? (Ids.Count == 0 ? Children.Any(c => c.Contains(package)) : Ids.Any(id => package.Node == id || package.Node.StartsWith(id + ".", StringComparison.Ordinal)));

    /// <summary>Expansion state of the tree item.</summary>
    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>
/// The Objects view: only things that stand in the world or go in a pocket (meshes and Blueprints, no textures, materials
/// or sounds), sorted into the game's categories from the embedded content catalogue (see <see cref="AssetDumper"/>).
/// </summary>
public sealed partial class AssetsPageViewModel
{
    // Things a player picks up and carries vs. things that stay where the map has them.
    private static readonly string[] PickUpRoots = ["weapons", "attachments", "ammo", "explosives", "items"];
    private static readonly string[] WorldRoots = ["buildings", "furniture", "exterior", "basebuilding", "nature", "wrecks", "vehicles", "roads", "water", "characters"];

    private List<(DumpPackage Package, PackageEntry Entry)> _objects = [];

    /// <summary>Objects (by category) instead of the package folders; on by default.</summary>
    [ObservableProperty]
    private bool _isObjectsView = true;

    /// <summary>The category tree of the Objects view.</summary>
    [ObservableProperty]
    private IReadOnlyList<ObjectCategoryNode> _objectRoots = [];

    /// <summary>Selected category.</summary>
    [ObservableProperty]
    private ObjectCategoryNode? _selectedCategory;

    /// <summary>True for catalogue classes that are objects (a mesh, or a Blueprint that places one).</summary>
    public static bool IsObjectClass(string className) => className is "StaticMesh" or "SkeletalMesh" or "Blueprint";

    /// <summary>Builds the Objects tree for <paramref name="index"/>: catalogue objects that the source has.</summary>
    private void BuildObjects(PackageIndex index)
    {
        var byPath = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in index.Entries)
        {
            byPath.TryAdd(e.PackagePath, e);
        }

        // The catalogue knows each package's class: no header reads before a tile can draw its picture. Far-view models
        // look like buildings but are blurred, merged with their surroundings and have no collision; the undersides of
        // the water never show in the game: neither is listed.
        _objects = AssetDumper.Packages
            .Where(p => IsObjectClass(p.ClassName) && byPath.ContainsKey(p.PackagePath)
                        && !ScumStudio.Level.Export.FarModels.IsFarViewMesh(p.PackagePath) && !ScumStudio.Level.Export.FarModels.IsUndersideMesh(p.PackagePath))
            .Select(p => (p, byPath[p.PackagePath] is { ClassName: null } e ? e with { ClassName = p.ClassName } : byPath[p.PackagePath]))
            .ToList();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (package, _) in _objects)
        {
            for (var id = package.Node; id.Length > 0; id = id.LastIndexOf('.') is var dot and > 0 ? id[..dot] : string.Empty)
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        var roots = AssetDumper.Tree.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var world = Group(Loc.T("Assets.Group.World"), WorldRoots);
        ObjectRoots = [Sets(), Group(Loc.T("Assets.Group.PickUp"), PickUpRoots), world];
        SelectedCategory = world;

        ObjectCategoryNode Node(DumpNode n) =>
            new(n.Title, [n.Id], n.Children.Select(Node).Where(c => c.Count > 0).ToList(), counts.GetValueOrDefault(n.Id));

        // Every kind of wall, road and bridge in one place, wherever the catalogue filed it (owner request).
        ObjectCategoryNode Sets()
        {
            var children = BuildingSets
                .Select(set => new ObjectCategoryNode(Loc.T(set.Key), [], [], _objects.Count(o => set.Match(o.Package))) { Match = set.Match })
                .Where(c => c.Count > 0)
                .ToList();
            return new ObjectCategoryNode(Loc.T("Assets.Group.Sets"), [], children, children.Sum(c => c.Count)) { IsExpanded = true };
        }

        ObjectCategoryNode Group(string title, string[] ids)
        {
            var children = ids.Where(roots.ContainsKey).Select(id => Node(roots[id])).Where(c => c.Count > 0).ToList();
            return new ObjectCategoryNode(title, ids, children, children.Sum(c => c.Count)) { IsExpanded = true };
        }
    }

    // ponytail: name keywords; distant LODs, terrain layers and effects are left out. Extend the lists when a kind is missed.
    internal static readonly (string Key, Func<DumpPackage, bool> Match)[] BuildingSets =
    [
        ("Assets.Set.Walls", p => NameHas(p, "wall") && !NameHas(p, "paper", "lamp", "light", "clock", "shelf", "socket", "switch", "poster", "painting", "cabinet", "decal", "mount")),
        ("Assets.Set.Roads", p => (p.Node.StartsWith("roads.roads", StringComparison.Ordinal) || NameHas(p, "road", "asphalt", "curb", "sidewalk", "pavement"))
            && !NameHas(p, "railroad", "sign", "tunnel", "decal", "lamp", "light")),
        ("Assets.Set.Bridges", p => NameHas(p, "bridge") && !NameHas(p, "distant")),
    ];

    private static bool NameHas(DumpPackage package, params string[] words)
    {
        if (package.Node.StartsWith("terrain.", StringComparison.Ordinal) || package.Node.StartsWith("effects.", StringComparison.Ordinal))
        {
            return false;
        }

        var name = package.PackagePath[(package.PackagePath.LastIndexOf('/') + 1)..];
        return !name.EndsWith("_WM", StringComparison.OrdinalIgnoreCase) && words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Lists the objects of <paramref name="category"/>.</summary>
    private void ShowCategory(ObjectCategoryNode? category)
    {
        if (category is null)
        {
            Items = [];
            ResultText = string.Empty;
            return;
        }

        var all = _objects.Where(o => category.Contains(o.Package)).ToList();
        Items = all.Take(MaxListed).Select(o => new AssetItemViewModel(o.Entry, LoadThumbnailAsync)).ToList();
        ResultText = all.Count > MaxListed
            ? Loc.F("Assets.ShowingObjects", MaxListed, all.Count, category.Title)
            : Loc.F(all.Count == 1 ? "Assets.Objects.One" : "Assets.Objects.Many", all.Count, category.Title);
    }

    /// <summary>The packages search looks through: the objects in the Objects view, every package otherwise.</summary>
    private IReadOnlyList<PackageEntry> SearchScope(PackageIndex index) => IsObjectsView ? _objects.Select(o => o.Entry).ToList() : index.Entries;

    partial void OnSelectedCategoryChanged(ObjectCategoryNode? value)
    {
        if (IsObjectsView && string.IsNullOrWhiteSpace(SearchText))
        {
            ShowCategory(value);
        }
    }

    partial void OnIsObjectsViewChanged(bool value)
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            _ = DebouncedSearchAsync(SearchText);
        }
        else if (value)
        {
            ShowCategory(SelectedCategory);
        }
        else
        {
            ShowFolder(SelectedFolder);
        }
    }
}
