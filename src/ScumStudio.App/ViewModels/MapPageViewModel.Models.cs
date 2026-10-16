using System.Windows.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Import;

namespace ScumStudio.App.ViewModels;

/// <summary>One 3D model of the project's library in the Map's Add object › My models: its picture, name and Place.</summary>
public sealed class MapModelRow : ThumbnailItem
{
    private readonly Func<MapModelRow, CancellationToken, Task<Bitmap?>> _picture;

    /// <summary>Creates the row over <paramref name="model"/>.</summary>
    public MapModelRow(LibraryModel model, int placed, Action<MapModelRow> place, Func<MapModelRow, CancellationToken, Task<Bitmap?>> picture)
    {
        Model = model;
        Placed = placed;
        _picture = picture;
        Place = new RelayCommand(() => place(this));
    }

    /// <summary>The model.</summary>
    public LibraryModel Model { get; }

    /// <summary>Its name ("Wooden Table").</summary>
    public string Name => Model.Name;

    /// <summary>How many times the project shows it on the map.</summary>
    public int Placed { get; }

    /// <summary>"SM_SS_Wooden_Table · on the map 2 times".</summary>
    public string Caption => Placed > 0 ? Loc.F("Map.MyModels.RowPlaced", "SM_" + Model.Token, Placed) : "SM_" + Model.Token;

    /// <summary>Puts it in front of the camera.</summary>
    public ICommand Place { get; }

    /// <inheritdoc />
    protected override Task<Bitmap?> LoadThumbnailAsync(CancellationToken cancellationToken) => _picture(this, cancellationToken);
}

/// <summary>
/// Add object › My models (Pheidian, 2026-10-13: "Is there a way to import custom models into the editor?"; owner: "i will
/// add next update"): the project's imported 3D models (<see cref="ModelLibrary"/>, the same ones the Craftables page
/// lists), placed like any game mesh (a new mesh actor in the journal: the gizmo moves, turns and scales it, Ctrl+Z undoes
/// it) and carried by Export mod into the pak with the placement (<see cref="Level.Export.ProjectExporter"/>). Import
/// 3D model… is the Craftables page's import (<see cref="ModelImports"/>); without Unreal Engine 4.27 it says what is needed.
/// </summary>
public sealed partial class MapPageViewModel
{
    private CancellationTokenSource? _modelImportCancel;

    /// <summary>The project's 3D models.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMyModels))]
    private IReadOnlyList<MapModelRow> _myModels = [];

    /// <summary>A 3D file is being imported (shows Cancel).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportMapModelCommand))]
    private bool _isImportingModel;

    /// <summary>Unreal Engine 4.27 is not installed: the import explains what it needs instead of starting.</summary>
    [ObservableProperty]
    private bool _modelEngineMissing;

    /// <summary>The project has imported 3D models.</summary>
    public bool HasMyModels => MyModels.Count > 0;

    /// <summary>What the import needs (<see cref="ModelEngineMissing"/>).</summary>
    public string ModelEngineHint => ModelImports.NoEngineText;

    /// <summary>Lists the open project's 3D models again (the Add object flyout opens, an import ends).</summary>
    public void RefreshMyModels()
    {
        ModelEngineMissing = !ModelImports.HasEngine;
        var project = _services.Projects.Current;
        var used = project is null
            ? []
            : project.State.AddedActors
                .Where(a => !project.State.IsDeleted(a.Key) && a.Value is AddStaticMeshActorOp)
                .Select(a => project.State.GetMeshOverride(a.Key) ?? ((AddStaticMeshActorOp)a.Value).StaticMesh)
                .Where(ModelLibrary.IsModelMesh)
                .ToList();
        MyModels = ModelLibrary.List(project?.DirectoryPath)
            .Select(m => new MapModelRow(m, used.Count(u => string.Equals(u, m.MeshObjectPath, StringComparison.OrdinalIgnoreCase)), PlaceModel, ModelPictureAsync))
            .ToList();
    }

    /// <summary>The project's model named <paramref name="name"/> (its name, token, mesh name or path), or null.</summary>
    private LibraryModel? MyModelNamed(string name)
    {
        if (_services.Projects.Current is not { } project)
        {
            return null;
        }

        bool Is(string value) => string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
        return ModelLibrary.List(project.DirectoryPath)
            .FirstOrDefault(m => Is(m.Name) || Is(m.Token) || Is("SM_" + m.Token) || Is(m.MeshPackage) || Is(m.MeshObjectPath));
    }

    private void PlaceModel(MapModelRow row)
    {
        if (ModelLibrary.FolderOf(_services.Projects.Current?.DirectoryPath, row.Model.MeshPackage, out var problem) is null)
        {
            _services.Notifications.Warning(Loc.T("Map.AddFailed"), Loc.F("Map.MyModels.Gone", row.Name, problem));
            RefreshMyModels();
            return;
        }

        if (AddMeshActor(row.Model.MeshObjectPath))
        {
            RefreshMyModels(); // its count; the viewport loads it from its own files (EnsureMeshLoaded)
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportMapModel))]
    private async Task ImportMapModelAsync()
    {
        var title = Loc.T("Map.MyModels.Title");
        if (await ModelImports.PickAsync(_services, title, Loc.T("Map.NoProject.Adds")).ConfigureAwait(true) is not { } job)
        {
            ModelEngineMissing = !ModelImports.HasEngine;
            return;
        }

        using var cancel = _modelImportCancel = new CancellationTokenSource();
        IsImportingModel = true;
        try
        {
            if (await ModelImports.ImportAsync(_services, job, cancel.Token).ConfigureAwait(true) is { } model)
            {
                _services.Notifications.Success(title, Loc.F("Map.MyModels.Imported", new LibraryModel(model.Token, model.Folder).Name, job.Project.Manifest.Name));
            }
        }
        finally
        {
            _modelImportCancel = null;
            IsImportingModel = false;
            RefreshMyModels();
        }
    }

    private bool CanImportMapModel() => !IsImportingModel;

    [RelayCommand]
    private void CancelMapModelImport() => _modelImportCancel?.Cancel();

    /// <summary>The 3D picture of a model, made from its own cooked files (the Assets page's thumbnails).</summary>
    private async Task<Bitmap?> ModelPictureAsync(MapModelRow row, CancellationToken cancellationToken)
    {
        if (_services.Projects.Current is not { } project)
        {
            return null;
        }

        var folder = Path.Combine(project.DirectoryPath, row.Model.Folder);
        using var own = Directory.Exists(folder) ? AssetCatalog.OpenLoose(folder) : null;
        if (own is null || !own.TryGetPackageFile(row.Model.MeshPackage, out var file))
        {
            return null;
        }

        var png = await _services.Thumbnails.GetAssetAsync(own, new PackageEntry(file.Path, row.Model.MeshPackage, "StaticMesh"), cancellationToken).ConfigureAwait(false);
        return png is null ? null : await Task.Run(() => new Bitmap(png), cancellationToken).ConfigureAwait(false);
    }
}
