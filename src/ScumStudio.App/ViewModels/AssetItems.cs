using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>Icon and tint for an asset class (muted field colours that read on the olive-black surfaces).</summary>
public static class AssetIcons
{
    /// <summary>Returns the icon resource key and tint (<c>#RRGGBB</c>) for a main-export class.</summary>
    public static (string IconKey, string Tint) ForClass(string? className, bool isMap = false)
    {
        if (isMap || string.Equals(className, "World", StringComparison.OrdinalIgnoreCase))
        {
            return ("Icon.World", "#6FA89C");
        }

        if (className is null)
        {
            return ("Icon.File", "#8A8672");
        }

        bool Is(string text) => className.Contains(text, StringComparison.OrdinalIgnoreCase);
        return true switch
        {
            _ when Is("Texture") => ("Icon.Texture", "#9DB35E"),
            _ when Is("SkeletalMesh") => ("Icon.SkeletalMesh", "#B08CB8"),
            _ when Is("StaticMesh") => ("Icon.Mesh", "#6C9BDB"),
            _ when Is("Material") => ("Icon.Material", "#D9826A"),
            _ when Is("Blueprint") || className.EndsWith("_C", StringComparison.Ordinal) => ("Icon.Blueprint", "#5FA9C4"),
            _ when Is("Sound") || Is("AkAudio") || Is("Wwise") => ("Icon.Sound", "#D4A53A"),
            _ when Is("Anim") || Is("Skeleton") || Is("PhysicsAsset") => ("Icon.Animation", "#C79A6B"),
            _ when Is("DataTable") || Is("Curve") || Is("DataAsset") || Is("Preset") => ("Icon.List", "#ABA691"),
            _ when Is("MapBuildData") || Is("Landscape") => ("Icon.Landscape", "#7FA05A"),
            _ => ("Icon.File", "#8A8672"),
        };
    }
}

/// <summary>A folder of the asset tree (children created on first access).</summary>
public sealed partial class AssetFolderNode : ViewModelBase
{
    private IReadOnlyList<AssetFolderNode>? _children;
    private int? _count;

    /// <summary>Wraps <paramref name="folder"/>.</summary>
    public AssetFolderNode(PackageFolder folder) => Folder = folder;

    /// <summary>The folder.</summary>
    public PackageFolder Folder { get; }

    /// <summary>Folder name.</summary>
    public string Name => Folder.Name.Length == 0 ? Localization.Loc.T("Assets.Root") : Folder.Name;

    /// <summary>Package-style path, e.g. <c>/Game/ConZ_Files</c>.</summary>
    public string Path => Folder.Path;

    /// <summary>Packages in and below the folder.</summary>
    public int Count => _count ??= Folder.TotalPackageCount;

    /// <summary><see cref="Count"/> formatted.</summary>
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Sub-folders.</summary>
    public IReadOnlyList<AssetFolderNode> Children => _children ??= Folder.Folders.Select(f => new AssetFolderNode(f)).ToList();

    /// <summary>Expansion state of the tree item.</summary>
    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>A package in the asset list or tile grid; its picture is made while a row or tile shows it.</summary>
public sealed class AssetItemViewModel : ThumbnailItem
{
    private readonly Func<AssetItemViewModel, CancellationToken, Task<Bitmap?>>? _thumbnails;
    private bool _isSelected;

    /// <summary>Wraps <paramref name="entry"/>; <paramref name="thumbnails"/> makes the small picture of a tile on screen.</summary>
    public AssetItemViewModel(PackageEntry entry, Func<AssetItemViewModel, CancellationToken, Task<Bitmap?>>? thumbnails = null)
    {
        Entry = entry;
        _thumbnails = thumbnails;
        (IconKey, Tint) = AssetIcons.ForClass(entry.ClassName, entry.IsMap);
    }

    /// <summary>The package (its class is filled in by <see cref="ResolveClass"/> when the index had none).</summary>
    public PackageEntry Entry { get; private set; }

    /// <summary>Package name.</summary>
    public string Name => Entry.Name;

    /// <summary>Main class or a placeholder.</summary>
    public string ClassName => Entry.ClassName ?? (Entry.IsMap ? "World" : "Package");

    /// <summary>Folder path.</summary>
    public string Folder => Entry.Folder;

    /// <summary>Icon resource key.</summary>
    public string IconKey { get; private set; }

    /// <summary>Icon tint.</summary>
    public string Tint { get; private set; }

    /// <summary>True for the selected package (tile highlight).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Records the main class read from the package header (large pak sets do not resolve classes while indexing).</summary>
    public void ResolveClass(string? className)
    {
        if (className is null || Entry.ClassName is not null)
        {
            return;
        }

        Entry = Entry with { ClassName = className };
        (IconKey, Tint) = AssetIcons.ForClass(className, Entry.IsMap);
        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(ClassName));
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(Tint));
    }

    /// <inheritdoc />
    protected override Task<Bitmap?> LoadThumbnailAsync(CancellationToken cancellationToken) =>
        _thumbnails is { } load ? load(this, cancellationToken) : Task.FromResult<Bitmap?>(null);
}

/// <summary>One row of the tile grid (the grid virtualizes rows, so only the tiles on screen exist as controls).</summary>
/// <param name="Tiles">The row's packages.</param>
public sealed record AssetTileRow(IReadOnlyList<AssetItemViewModel> Tiles);

/// <summary>An export of the selected package.</summary>
/// <param name="Index">Export index.</param>
/// <param name="Name">Object name.</param>
/// <param name="ClassName">Class.</param>
/// <param name="SizeText">Serialized size.</param>
public sealed record ExportRow(int Index, string Name, string ClassName, string SizeText);

/// <summary>Details pane of the Assets page.</summary>
public sealed partial class AssetDetailsViewModel : ViewModelBase
{
    /// <summary>Creates details for <paramref name="entry"/> (filled by <see cref="Complete"/>).</summary>
    public AssetDetailsViewModel(PackageEntry entry)
    {
        Entry = entry;
        _className = entry.ClassName ?? (entry.IsMap ? "World" : Localization.Loc.T("Assets.Resolving"));
        (IconKey, Tint) = AssetIcons.ForClass(entry.ClassName, entry.IsMap);
    }

    /// <summary>The package.</summary>
    public PackageEntry Entry { get; }

    /// <summary>Name.</summary>
    public string Name => Entry.Name;

    /// <summary>Package path.</summary>
    public string PackagePath => Entry.PackagePath;

    /// <summary>Provider file path.</summary>
    public string FilePath => Entry.FilePath;

    /// <summary>Icon key.</summary>
    public string IconKey { get; private set; }

    /// <summary>Icon tint.</summary>
    public string Tint { get; private set; }

    /// <summary>Main class.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportPng), nameof(CanExportGltf), nameof(CanPlaceInMap))]
    private string _className;

    /// <summary>Total size of the package files (.uasset + .uexp + .ubulk).</summary>
    [ObservableProperty]
    private string _sizeText = "...";

    /// <summary>Exports from the package header.</summary>
    [ObservableProperty]
    private IReadOnlyList<ExportRow> _exports = [];

    /// <summary>Key/value rows.</summary>
    [ObservableProperty]
    private IReadOnlyList<PropertyRow> _rows = [];

    /// <summary>True while loading.</summary>
    [ObservableProperty]
    private bool _isLoading = true;

    /// <summary>Error message when the header could not be read.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>3D preview of a static or skeletal mesh (LOD 0 with its base-colour textures), when the package is one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(ShowIcon))]
    private PreviewModel? _preview;

    /// <summary>Decoded image of a texture, or the base-colour texture of a material.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(ShowIcon), nameof(ImageText))]
    private TextureImage? _image;

    /// <summary>Parameters of a material (instance): parent chain, textures, colours, scalars.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMaterialRows))]
    private IReadOnlyList<PropertyRow> _materialRows = [];

    /// <summary>True while the preview is being prepared on a worker.</summary>
    [ObservableProperty]
    private bool _isPreviewLoading;

    /// <summary>Why no preview could be made, or null.</summary>
    [ObservableProperty]
    private string? _previewError;

    /// <summary>True for textures.</summary>
    public bool CanExportPng => AssetExportService.IsTextureClass(ClassName);

    /// <summary>True for meshes.</summary>
    public bool CanExportGltf => AssetExportService.IsMeshClass(ClassName);

    /// <summary>True for a static mesh, which the Map page can place as a new actor.</summary>
    public bool CanPlaceInMap => string.Equals(ClassName, "StaticMesh", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a 3D preview is loaded.</summary>
    public bool HasPreview => Preview is not null;

    /// <summary>True when an image is loaded.</summary>
    public bool HasImage => Image is not null;

    /// <summary>True when the class icon stands in for a preview.</summary>
    public bool ShowIcon => Preview is null && Image is null;

    /// <summary>True when material parameters are listed.</summary>
    public bool HasMaterialRows => MaterialRows.Count > 0;

    /// <summary>"2048 x 2048 · PF_BC7" (with the shown mip when it is not the largest).</summary>
    public string ImageText => Image is { } image
        ? $"{image.SourceWidth} x {image.SourceHeight} · {image.PixelFormat}" + (image.MipIndex > 0 ? Localization.Loc.F("Assets.ShownAt", image.Width, image.Height) : string.Empty)
        : string.Empty;

    /// <summary>Fills the loaded data.</summary>
    public void Complete(string className, long totalBytes, IReadOnlyList<ExportInfo> exports)
    {
        ClassName = className;
        (IconKey, Tint) = AssetIcons.ForClass(className, Entry.IsMap);
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(Tint));
        SizeText = FormatBytes(totalBytes);
        Exports = exports.Select(e => new ExportRow(e.Index, e.Name, e.ClassName, FormatBytes(e.SerialSize))).ToList();
        Rows =
        [
            new PropertyRow(Localization.Loc.T("Assets.Row.Class"), className),
            new PropertyRow(Localization.Loc.T("Assets.Row.Exports"), exports.Count.ToString("N0", CultureInfo.CurrentCulture)),
            new PropertyRow(Localization.Loc.T("Assets.Row.Size"), SizeText),
            new PropertyRow(Localization.Loc.T("Assets.Row.Package"), Entry.PackagePath),
            new PropertyRow(Localization.Loc.T("Assets.Row.Object"), Entry.ObjectPath),
            new PropertyRow(Localization.Loc.T("Assets.Row.File"), Entry.FilePath),
        ];
        IsLoading = false;
    }

    /// <summary>Formats a byte count (B, KB, MB, GB).</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? bytes.ToString("N0", CultureInfo.CurrentCulture) + " B"
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
