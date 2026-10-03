using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Shared logic of the Vehicles and Weapons pages: the moddable assets of the connected game files (plus the project's
/// clones), the editable stored values of the selected asset's packages, apply/reset through the project journal, and
/// clone-under-a-new-name. Export happens on the Projects page together with the map edits.
/// </summary>
public abstract partial class ModulePageViewModel : PageViewModel, ISearchablePage, IDisposable
{
    private readonly AppServices _services;
    private readonly Action _openSetup;
    private readonly HashSet<ModdableKind> _kinds;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Dictionary<string, CookedPackage> _packageCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ModdableAsset> _stock = [];
    private AssetCatalog? _catalog;
    private Task _loadTask = Task.CompletedTask;
    private Task _partTask = Task.CompletedTask;
    private Task _previewTask = Task.CompletedTask;
    private IReadOnlyList<TunableRowViewModel> _allRows = [];

    /// <summary>Creates the page.</summary>
    protected ModulePageViewModel(AppServices services, Action? openSetup, string key, string title, string subtitle, IEnumerable<ModdableKind> kinds)
        : base(key, title, subtitle)
    {
        _services = services;
        _openSetup = openSetup ?? (() => { });
        _kinds = kinds.ToHashSet();
        _services.Workspace.CatalogChanged += OnCatalogChanged;
        _services.Projects.Changed += OnProjectChanged;
        if (_services.Workspace.Catalog is { } catalog)
        {
            _loadTask = LoadAsync(catalog);
        }
    }

    /// <summary>Filter chips.</summary>
    [ObservableProperty]
    private IReadOnlyList<ModuleFilter> _filters = [];

    /// <summary>Selected chip.</summary>
    [ObservableProperty]
    private ModuleFilter? _selectedFilter;

    /// <summary>Search text over names and categories.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Every listed asset (stock + clones).</summary>
    [ObservableProperty]
    private IReadOnlyList<ModuleItemViewModel> _allItems = [];

    /// <summary>Assets passing the chip and search filters.</summary>
    [ObservableProperty]
    private IReadOnlyList<ModuleItemViewModel> _items = [];

    /// <summary>Selected asset.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanRemoveClone), nameof(SpawnCommandText), nameof(SelectedTitle))]
    [NotifyCanExecuteChangedFor(nameof(CreateCloneCommand), nameof(RemoveCloneCommand))]
    private ModuleItemViewModel? _selectedItem;

    /// <summary>Editable packages of the selected asset.</summary>
    [ObservableProperty]
    private IReadOnlyList<ModulePart> _parts = [];

    /// <summary>Selected package.</summary>
    [ObservableProperty]
    private ModulePart? _selectedPart;

    /// <summary>Value groups of the selected package (after the value filter).</summary>
    [ObservableProperty]
    private IReadOnlyList<TunableGroupViewModel> _groups = [];

    /// <summary>Filter over value names.</summary>
    [ObservableProperty]
    private string _valueFilter = string.Empty;

    /// <summary>Show only the gameplay values the module knows (damage, range, mass, RPM …).</summary>
    [ObservableProperty]
    private bool _keyStatsOnly = true;

    /// <summary>Number of rows with a pending change.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyChangesCommand), nameof(DiscardChangesCommand))]
    private int _pendingCount;

    /// <summary>"12 values · 2 edited · 1 pending".</summary>
    [ObservableProperty]
    private string _valuesSummary = string.Empty;

    /// <summary>Display caption of the selected asset (its entity setup's <c>Caption</c>), or empty.</summary>
    [ObservableProperty]
    private string _caption = string.Empty;

    /// <summary>True while values are being read.</summary>
    [ObservableProperty]
    private bool _isLoadingValues;

    /// <summary>True while the asset list is being read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isLoading;

    /// <summary>Where the assets come from.</summary>
    [ObservableProperty]
    private string _sourceText = Loc.T("Assets.NoSource");

    /// <summary>New name for a clone.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCloneCommand))]
    [NotifyPropertyChangedFor(nameof(CloneNameError))]
    private string _cloneName = string.Empty;

    /// <summary>In-game display name for the clone (written to its entity setup's Caption), optional.</summary>
    [ObservableProperty]
    private string _cloneCaption = string.Empty;

    /// <summary>Vehicle clones: also clone the attachments (chassis, doors, wheels …).</summary>
    [ObservableProperty]
    private bool _includeAttachments = true;

    /// <summary>Vehicle clones: also clone the manual spawn presets.</summary>
    [ObservableProperty]
    private bool _includeSpawnPresets = true;

    /// <summary>Text under the clone form ("Creates 2 packages: …").</summary>
    [ObservableProperty]
    private string _clonePreview = string.Empty;

    /// <summary>3D model of the selected asset: its mesh (vehicles: chassis plus the default attachments on their sockets).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private PreviewModel? _preview;

    /// <summary>True while the preview model is prepared on a worker.</summary>
    [ObservableProperty]
    private bool _isPreviewLoading;

    /// <summary>Caption over the preview: mesh name and part count, or why there is none.</summary>
    [ObservableProperty]
    private string _previewText = string.Empty;

    /// <summary>Show the 3D view instead of the stored values (the "3D" tab in the item header, or "View in 3D" on a list item).</summary>
    [ObservableProperty]
    private bool _showPreviewTab;

    /// <summary>Raised by "Clone…" on a list item so the view can focus the clone name box.</summary>
    public event EventHandler? CloneRequested;

    /// <summary>True when a preview model is loaded.</summary>
    public bool HasPreview => Preview is not null;

    /// <summary>Completes when the selected asset's preview finished loading (tests, screenshots).</summary>
    public Task PreviewCompletion => _previewTask;

    /// <summary>True when the list is loaded.</summary>
    public bool HasCatalog => _catalog is not null;

    /// <summary>True when the empty state is shown.</summary>
    public bool ShowEmptyState => _catalog is null && !IsLoading;

    /// <summary>True when an asset is selected.</summary>
    public bool HasSelection => SelectedItem is not null;

    /// <summary>True when the selected asset is a project clone.</summary>
    public bool CanRemoveClone => SelectedItem?.IsClone == true;

    /// <summary>True on the Vehicles page (shows the vehicle clone options).</summary>
    public bool IsVehicleModule => _kinds.Contains(ModdableKind.Vehicle);

    /// <summary>Title of the details pane.</summary>
    public string SelectedTitle => SelectedItem?.Name ?? string.Empty;

    /// <summary>The admin command that spawns the selected asset in game.</summary>
    public string SpawnCommandText => SelectedItem is null
        ? string.Empty
        : SelectedItem.Asset.Kind == ModdableKind.Vehicle ? "#SpawnVehicle " + SelectedItem.Name : "#SpawnItem " + SelectedItem.Name;

    /// <summary>Why <see cref="CloneName"/> cannot be used, or null.</summary>
    public string? CloneNameError => string.IsNullOrWhiteSpace(CloneName) ? null : CloneFamilyPlanner.IsValidName(CloneName.Trim()) ? null : Loc.T("Module.CloneNameInvalid");

    /// <summary>Completes when the current list load finished (tests).</summary>
    public Task LoadCompletion => _loadTask;

    /// <summary>Completes when the current value load finished (tests).</summary>
    public Task ValuesCompletion => _partTask;

    /// <summary>Gameplay values shown by "Key stats" (property names).</summary>
    protected abstract IReadOnlySet<string> KeyStats { get; }

    /// <summary>Chips for the loaded assets.</summary>
    protected abstract IReadOnlyList<ModuleFilter> BuildFilters(IReadOnlyList<ModuleItemViewModel> items);

    /// <inheritdoc />
    public void ApplySearch(string? text) => SearchText = text ?? string.Empty;

    /// <summary>Opens a loose extracted folder as the workspace source and waits until the list is loaded (tests, screenshots).</summary>
    public async Task<bool> OpenLooseFolderAsync(string folder)
    {
        var ok = await _services.Operations.RunAsync(Loc.F("Assets.Opening", Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))),
            (p, ct) => _services.Workspace.OpenLooseAsync(folder, p, ct)).ConfigureAwait(true);
        await _loadTask.ConfigureAwait(true);
        return ok && _catalog is not null;
    }

    /// <summary>Selects the asset named <paramref name="name"/> and waits for its values.</summary>
    public async Task<bool> SelectAsync(string name)
    {
        await _loadTask.ConfigureAwait(true);
        var item = AllItems.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return false;
        }

        SelectedFilter = Filters.FirstOrDefault();
        SearchText = string.Empty;
        SelectedItem = item;
        await _partTask.ConfigureAwait(true);
        return true;
    }

    /// <summary>Selects a part by label and waits for its values.</summary>
    public async Task<bool> SelectPartAsync(string label)
    {
        var part = Parts.FirstOrDefault(p => string.Equals(p.Label, label, StringComparison.OrdinalIgnoreCase));
        if (part is null)
        {
            return false;
        }

        SelectedPart = part;
        await _partTask.ConfigureAwait(true);
        return true;
    }

    /// <summary>The row of property <paramref name="name"/> in the selected part (all rows, ignoring filters).</summary>
    public TunableRowViewModel? FindRow(string name) => _allRows.FirstOrDefault(r => r.Tunable.Name == name);

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Workspace.CatalogChanged -= OnCatalogChanged;
        _services.Projects.Changed -= OnProjectChanged;
        _io.Dispose();
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private void OpenSetup() => _openSetup();

    [RelayCommand]
    private async Task OpenLooseFolderPickerAsync()
    {
        var folder = await _services.Dialogs.PickFolderAsync(Loc.T("Assets.PickLoose")).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await OpenLooseFolderAsync(folder).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Undo()
    {
        if (_services.Projects.Undo() is { } entry)
        {
            _services.Notifications.Info(Loc.T("Module.Undone"), entry.Op.Describe());
        }
    }

    [RelayCommand]
    private void Redo()
    {
        if (_services.Projects.Redo() is { } entry)
        {
            _services.Notifications.Info(Loc.T("Module.Redone"), entry.Op.Describe());
        }
    }

    [RelayCommand]
    private void ToggleKeyStats() => KeyStatsOnly = !KeyStatsOnly;

    /// <summary>Shows the selected asset in the 3D view.</summary>
    [RelayCommand]
    private void ViewIn3D()
    {
        if (SelectedItem is not null)
        {
            ShowPreviewTab = true;
        }
    }

    /// <summary>Copies the admin command that spawns the selected asset.</summary>
    [RelayCommand]
    private async Task CopySpawnCommandAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        await _services.Dialogs.SetClipboardTextAsync(SpawnCommandText).ConfigureAwait(true);
        _services.Notifications.Info(Loc.T("Assets.Copied"), SpawnCommandText);
    }

    /// <summary>Starts a clone of the selected asset: back to the values tab, the name box focused.</summary>
    [RelayCommand]
    private void CloneItem()
    {
        if (SelectedItem is null)
        {
            return;
        }

        ShowPreviewTab = false;
        CloneRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ResetRow(TunableRowViewModel? row)
    {
        if (row is not null && row.Tunable.CanEdit)
        {
            row.Value = row.StockValue;
        }
    }

    private bool CanApplyChanges() => PendingCount > 0;

    /// <summary>Records every pending change as a <see cref="SetAssetValueOp"/> in the project journal.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyChanges))]
    public void ApplyChanges()
    {
        if (!_services.Projects.HasProject)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Module.NoProjectChanges"));
            return;
        }

        var pending = _allRows.Where(r => r.IsPending).ToList();
        var invalid = pending.FirstOrDefault(r => r.Error is not null);
        if (invalid is not null)
        {
            _services.Notifications.Error(Loc.T("Module.InvalidValue"), $"{invalid.Label}: {invalid.Error}");
            return;
        }

        var applied = 0;
        foreach (var row in pending)
        {
            var op = new SetAssetValueOp(row.Package, row.Tunable.Export, row.Tunable.Path, row.Tunable.Kind.ToString(), row.CommittedValue, row.NormalizedValue);
            try
            {
                _services.Projects.Apply(op);
                applied++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                _services.Notifications.Error(Loc.T("Module.NotRecorded"), $"{row.Label}: {ex.Message}");
            }
        }

        if (applied > 0)
        {
            _services.Notifications.Success(Loc.T("Module.ValuesChanged"), Loc.F("Module.ValuesChangedDetail", applied));
        }

        RefreshCommitted();
    }

    [RelayCommand(CanExecute = nameof(CanApplyChanges))]
    private void DiscardChanges()
    {
        foreach (var row in _allRows)
        {
            row.Value = row.CommittedValue;
        }
    }

    private bool CanCreateClone() => SelectedItem is not null && CloneFamilyPlanner.IsValidName(CloneName.Trim());

    /// <summary>Clones the selected asset under <see cref="CloneName"/> (journal: <see cref="CloneAssetOp"/>) and selects the clone.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateClone))]
    public async Task CreateCloneAsync()
    {
        var item = SelectedItem;
        var catalog = _catalog;
        if (item is null || catalog is null)
        {
            return;
        }

        if (!_services.Projects.HasProject)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Module.NoProjectClones"));
            return;
        }

        var template = item.CloneOf ?? item.PackagePath;
        if (item.IsClone)
        {
            _services.Notifications.Warning(Loc.T("Module.CloneStock"), Loc.F("Module.CloneStockDetail", ModuleItemViewModel.ShortName(template)));
            return;
        }

        var name = CloneName.Trim();
        ClonePlan plan;
        try
        {
            plan = await Task.Run(() => item.Asset.Kind == ModdableKind.Vehicle
                ? CloneFamilyPlanner.PlanVehicle(catalog, template, name, new VehicleCloneOptions { IncludeAttachments = IncludeAttachments, IncludeSpawnPresets = IncludeSpawnPresets })
                : CloneFamilyPlanner.PlanItem(catalog, template, name)).ConfigureAwait(true);
        }
        catch (ArgumentException ex)
        {
            _services.Notifications.Error(Loc.T("Module.CannotClone"), ex.Message);
            return;
        }

        var op = new CloneAssetOp(ModuleItemViewModel.KindLabel(item.Asset.Kind).ToLowerInvariant(), template, plan.NewPrimary,
            plan.Packages.Select(p => new PackagePair(p.Key, p.Value)).ToList());
        try
        {
            _services.Projects.Apply(op);
        }
        catch (InvalidOperationException ex)
        {
            _services.Notifications.Error(Loc.T("Module.CannotClone"), ex.Message);
            return;
        }

        _services.Notifications.Success(Loc.T("Module.CloneCreated"), op.Describe());
        var caption = CloneCaption.Trim();
        CloneName = string.Empty;
        CloneCaption = string.Empty;
        await RebuildListAsync().ConfigureAwait(true);
        if (await SelectAsync(ModuleItemViewModel.ShortName(plan.NewPrimary)).ConfigureAwait(true) && caption.Length > 0)
        {
            await SetCaptionAsync(caption).ConfigureAwait(true);
        }
    }

    /// <summary>Sets the in-game name of the selected asset (its entity setup's top-level <c>Caption</c>) through the journal.</summary>
    public async Task<bool> SetCaptionAsync(string caption)
    {
        var es = Parts.FirstOrDefault(p => string.Equals(p.PackagePath, SelectedItem?.Asset.EntitySetupPath, StringComparison.OrdinalIgnoreCase));
        if (es is null)
        {
            _services.Notifications.Warning(Loc.T("Module.NoEntitySetup"), Loc.T("Module.NoEntitySetupDetail"));
            return false;
        }

        SelectedPart = es;
        await _partTask.ConfigureAwait(true);
        var row = _allRows.FirstOrDefault(r => r.Tunable.Name == "Caption" && r.Tunable.Kind == TunableKind.Text && r.Tunable.CanEdit);
        if (row is null)
        {
            _services.Notifications.Warning(Loc.T("Module.NoCaption"), Loc.T("Module.NoCaptionDetail"));
            return false;
        }

        row.Value = caption;
        ApplyChanges();
        return true;
    }

    /// <summary>Resets the clone's edited values and removes it (journal: <see cref="RemoveAssetCloneOp"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveClone))]
    public async Task RemoveCloneAsync()
    {
        var item = SelectedItem;
        var project = _services.Projects.Current;
        if (item is not { IsClone: true } || project is null)
        {
            return;
        }

        var clone = project.State.AssetClones.FirstOrDefault(c => string.Equals(c.NewPrimary, item.PackagePath, StringComparison.OrdinalIgnoreCase));
        if (clone is null)
        {
            return;
        }

        try
        {
            foreach (var value in clone.Packages.SelectMany(p => project.State.GetAssetValues(p.New)).ToList())
            {
                _services.Projects.Apply(new SetAssetValueOp(value.Package, value.Export, value.Path, value.ValueKind, value.Current, value.Base));
            }

            _services.Projects.Apply(clone.Inverse());
            _services.Notifications.Info(Loc.T("Module.CloneRemoved"), ModuleItemViewModel.ShortName(clone.NewPrimary));
        }
        catch (InvalidOperationException ex)
        {
            _services.Notifications.Error(Loc.T("Module.CannotRemoveClone"), ex.Message);
        }

        await RebuildListAsync().ConfigureAwait(true);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedFilterChanged(ModuleFilter? value) => ApplyFilter();

    partial void OnValueFilterChanged(string value) => RebuildGroups();

    partial void OnKeyStatsOnlyChanged(bool value) => RebuildGroups();

    partial void OnCloneNameChanged(string value) => UpdateClonePreview();

    partial void OnIncludeAttachmentsChanged(bool value) => UpdateClonePreview();

    partial void OnIncludeSpawnPresetsChanged(bool value) => UpdateClonePreview();

    partial void OnSelectedItemChanged(ModuleItemViewModel? value)
    {
        Parts = [];
        SelectedPart = null;
        Caption = string.Empty;
        _allRows = [];
        Groups = [];
        Preview = null;
        _previewBase = null;
        Paints = [];
        ClearArmour();
        PreviewText = string.Empty;
        UpdateClonePreview();
        if (value is not null && _catalog is { } catalog)
        {
            _partTask = LoadPartsAsync(catalog, value);
            _previewTask = LoadPreviewAsync(catalog, value);
        }
    }

    /// <summary>
    /// Builds the 3D preview of <paramref name="item"/> on a worker (a clone shows its stock template's mesh), with a
    /// vehicle's armour kit fitted when <paramref name="addOn"/> names one.
    /// </summary>
    private async Task LoadPreviewAsync(AssetCatalog catalog, ModuleItemViewModel item, string? addOn = null)
    {
        IsPreviewLoading = true;
        try
        {
            var template = item.CloneOf ?? item.PackagePath;
            var model = await Task.Run(() => new MeshPreviewLoader(catalog, _services.Logger).LoadBlueprint(template, addOn)).ConfigureAwait(true);
            if (!ReferenceEquals(SelectedItem, item))
            {
                return;
            }

            Preview = model;
            if (model is not null)
            {
                _ = LoadPaintsAsync(catalog, item, model);
            }

            PreviewText = model is null
                ? Loc.T("Module.NoMesh")
                : Loc.F("Module.PreviewInfo", model.Name, model.Parts.Count, model.Triangles);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("No preview for {Package}: {Message}", item.PackagePath, ex.Message);
            if (ReferenceEquals(SelectedItem, item))
            {
                PreviewText = Loc.F("Module.PreviewFailed", ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(SelectedItem, item))
            {
                IsPreviewLoading = false;
            }
        }
    }

    /// <summary>The inventory icon of <paramref name="item"/> (a clone shows its template's), through the thumbnail cache.</summary>
    private async Task<Bitmap?> LoadIconAsync(ModuleItemViewModel item, CancellationToken cancellationToken)
    {
        if (_catalog is not { } catalog)
        {
            return null;
        }

        var entitySetup = item.IsClone
            ? _stock.FirstOrDefault(s => string.Equals(s.PackagePath, item.CloneOf, StringComparison.OrdinalIgnoreCase))?.EntitySetupPath
            : item.Asset.EntitySetupPath;
        if (await Task.Run(() => InventoryIcons.FindIconPath(catalog, entitySetup), cancellationToken).ConfigureAwait(true) is not { } iconPath)
        {
            return null;
        }

        var png = await _services.Thumbnails.GetTextureAsync(catalog, iconPath, cancellationToken).ConfigureAwait(true);
        return png is null ? null : await Task.Run(() => new Bitmap(png), cancellationToken).ConfigureAwait(true);
    }

    partial void OnSelectedPartChanged(ModulePart? value)
    {
        if (value is not null && _catalog is { } catalog)
        {
            _partTask = LoadValuesAsync(catalog, value);
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        _packageCache.Clear();
        if (_services.Workspace.Catalog is { } catalog)
        {
            _loadTask = LoadAsync(catalog);
        }
        else
        {
            _catalog = null;
            AllItems = [];
            Items = [];
            SelectedItem = null;
            SourceText = Loc.T("Assets.NoSource");
            OnPropertyChanged(nameof(HasCatalog));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private void OnProjectChanged(object? sender, EventArgs e) => _services.Dispatcher.Invoke(() =>
    {
        UpdateEditCounts();
        if (_catalog is not null && ClonesChanged())
        {
            _io.Wait();
            try
            {
                _packageCache.Clear();
            }
            finally
            {
                _io.Release();
            }

            _ = RebuildListAsync();
            return;
        }

        RefreshCommitted();
        ReloadPaints();
    });

    private async Task LoadAsync(AssetCatalog catalog)
    {
        IsLoading = true;
        try
        {
            var stock = await Task.Run(() => ModdableAssets.Find(catalog).Where(a => _kinds.Contains(a.Kind)).ToList()).ConfigureAwait(true);
            _catalog = catalog;
            _stock = stock;
            SourceText = catalog.DisplayName;
            await RebuildListAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            _services.Logger.LogError(ex, "Reading the {Title} list failed.", Title);
            _services.Notifications.Error(Loc.F("Module.NotListed", Title), ex.Message);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasCatalog));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private Task RebuildListAsync()
    {
        var selected = SelectedItem?.PackagePath;
        var items = _stock.Select(a => new ModuleItemViewModel(a, icon: LoadIconAsync)).ToList();
        if (_services.Projects.Current is { } project)
        {
            foreach (var clone in project.State.AssetClones)
            {
                if (ModdableAssets.Classify(clone.Template) is not { } template || !_kinds.Contains(template.Kind))
                {
                    continue;
                }

                var map = clone.Packages.ToDictionary(p => p.Old, p => p.New, StringComparer.OrdinalIgnoreCase);
                var stockEs = _stock.FirstOrDefault(s => string.Equals(s.PackagePath, clone.Template, StringComparison.OrdinalIgnoreCase))?.EntitySetupPath;
                var asset = template with
                {
                    PackagePath = clone.NewPrimary,
                    EntitySetupPath = stockEs is not null && map.TryGetValue(stockEs, out var es) ? es : null,
                };
                items.Add(new ModuleItemViewModel(asset, clone.Template, LoadIconAsync));
            }
        }

        items = items.OrderBy(i => i.Asset.Kind).ThenBy(i => i.Asset.Category, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        AllItems = items;
        _knownClones = _services.Projects.Current?.State.AssetClones.Select(c => c.NewPrimary).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        UpdateEditCounts();
        var filters = BuildFilters(items);
        var previous = SelectedFilter?.Label;
        Filters = filters;
        SelectedFilter = filters.FirstOrDefault(f => f.Label == previous) ?? filters.FirstOrDefault();
        ApplyFilter();
        Subtitle = ListSubtitle(items);
        SelectedItem = selected is null ? Items.FirstOrDefault() : AllItems.FirstOrDefault(i => string.Equals(i.PackagePath, selected, StringComparison.OrdinalIgnoreCase)) ?? Items.FirstOrDefault();
        return _partTask;
    }

    private IReadOnlyList<string> _knownClones = [];

    private bool ClonesChanged()
    {
        var now = _services.Projects.Current?.State.AssetClones.Select(c => c.NewPrimary).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        return !now.SequenceEqual(_knownClones, StringComparer.OrdinalIgnoreCase);
    }

    private static string ListSubtitle(IReadOnlyList<ModuleItemViewModel> items) =>
        Loc.F("Module.Subtitle", items.Count(i => !i.IsClone)) + (items.Any(i => i.IsClone) ? Loc.F("Module.Subtitle.Clones", items.Count(i => i.IsClone)) : string.Empty);

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        if (AllItems.Count > 0)
        {
            // Filter chips keep their labels until the next load: replacing them would reset the list and the selection.
            Subtitle = ListSubtitle(AllItems);
            foreach (var item in AllItems)
            {
                item.OnLanguageChanged();
            }
        }

        if (_services.Workspace.Catalog is null)
        {
            SourceText = Loc.T("Assets.NoSource");
        }

        OnPropertyChanged(nameof(CloneNameError));
        UpdateClonePreview();
        UpdateSummary();
    }

    private void UpdateEditCounts()
    {
        var state = _services.Projects.Current?.State;
        foreach (var item in AllItems)
        {
            if (state is null)
            {
                item.EditCount = 0;
                continue;
            }

            var packages = new List<string> { item.PackagePath };
            if (item.Asset.EntitySetupPath is { } es)
            {
                packages.Add(es);
            }

            if (state.FindCloneOf(item.PackagePath) is { } clone)
            {
                packages.AddRange(clone.Packages.Select(p => p.New));
            }

            item.EditCount = packages.Distinct(StringComparer.OrdinalIgnoreCase).Sum(p => state.GetAssetValues(p).Count);
        }
    }

    private void ApplyFilter()
    {
        var tokens = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = AllItems
            .Where(i => SelectedFilter?.Matches(i) ?? true)
            .Where(i => tokens.All(t => i.PackagePath.Contains(t, StringComparison.OrdinalIgnoreCase) || i.Asset.Category.Contains(t, StringComparison.OrdinalIgnoreCase)));
        // Names that start with the typed word first, then names containing it, then folder/category matches (stable order within).
        Items = tokens.Length == 0
            ? matches.ToList()
            : matches.OrderBy(i => i.Name.StartsWith(tokens[0], StringComparison.OrdinalIgnoreCase) ? 0 : i.Name.Contains(tokens[0], StringComparison.OrdinalIgnoreCase) ? 1 : 2).ToList();
    }

    private void UpdateClonePreview()
    {
        var item = SelectedItem;
        if (item is null)
        {
            ClonePreview = string.Empty;
            return;
        }

        if (item.IsClone)
        {
            ClonePreview = Loc.T("Module.ClonePreview.IsClone");
            return;
        }

        var name = CloneName.Trim();
        if (!CloneFamilyPlanner.IsValidName(name))
        {
            ClonePreview = item.Asset.Kind == ModdableKind.Vehicle
                ? Loc.F("Module.ClonePreview.Vehicle", item.Name, IncludeAttachments ? Loc.T("Module.ClonePreview.Attachments") : string.Empty,
                    IncludeSpawnPresets ? Loc.T("Module.ClonePreview.SpawnPresets") : string.Empty)
                : Loc.F("Module.ClonePreview.Item", item.Name, item.Asset.EntitySetupPath is null ? string.Empty : Loc.T("Module.ClonePreview.EntitySetup"));
            return;
        }

        var newPrimary = item.Asset.Kind == ModdableKind.Vehicle ? "BPC_" + (name.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase) ? name[4..] : name) : name;
        ClonePreview = Loc.F("Module.ClonePreview.New", newPrimary, PackageMap.Folder(item.PackagePath),
            (item.Asset.Kind == ModdableKind.Vehicle ? "#SpawnVehicle " : "#SpawnItem ") + newPrimary);
    }

    private async Task LoadPartsAsync(AssetCatalog catalog, ModuleItemViewModel item)
    {
        var parts = new List<ModulePart> { new(ModuleItemViewModel.KindLabel(item.Asset.Kind), item.PackagePath, Loc.Instance.Or("Module.Part.Gameplay." + item.Asset.Kind, string.Empty)) };
        if (item.Asset.EntitySetupPath is { } es)
        {
            parts.Add(new ModulePart(Loc.T("Module.Part.EntitySetup"), es, Loc.T("Module.Part.EntitySetup.Tip")));
        }

        if (item.Asset.Kind == ModdableKind.Vehicle)
        {
            var attachments = await Task.Run(() => VehicleAttachments(catalog, item)).ConfigureAwait(true);
            parts.AddRange(attachments);
        }

        if (!ReferenceEquals(SelectedItem, item))
        {
            return;
        }

        Parts = parts;
        SelectedPart = parts[0];
        await _partTask.ConfigureAwait(true);
        if (item.Asset.EntitySetupPath is { } esPath)
        {
            var caption = await Task.Run(() => TryReadCaption(catalog, esPath)).ConfigureAwait(true);
            if (ReferenceEquals(SelectedItem, item))
            {
                Caption = caption ?? string.Empty;
            }
        }
    }

    private List<ModulePart> VehicleAttachments(AssetCatalog catalog, ModuleItemViewModel item)
    {
        IEnumerable<string> candidates;
        if (item.IsClone && _services.Projects.Current?.State.FindCloneOf(item.PackagePath) is { } clone)
        {
            candidates = clone.Packages.Select(p => p.New);
        }
        else
        {
            var token = ModdableAssets.VehicleToken(item.Name);
            candidates = ModdableAssets.ReadImportedPackages(catalog, item.PackagePath)
                .Where(p => PackageMap.Leaf(p).Contains(token, StringComparison.OrdinalIgnoreCase) && catalog.PackageExists(p));
        }

        var prefix = item.Name + "_";
        return candidates
            .Where(p => p.Contains("/Attachments/", StringComparison.OrdinalIgnoreCase) && PackageMap.Leaf(p).StartsWith("BPC_", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                var leaf = PackageMap.Leaf(p);
                var label = leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? leaf[prefix.Length..] : leaf;
                return new ModulePart(Loc.F("Module.Part.Attachment", label.Replace('_', ' ')), p, Loc.F("Module.Part.Attachment.Tip", leaf));
            })
            .ToList();
    }

    private string? TryReadCaption(AssetCatalog catalog, string esPath)
    {
        try
        {
            var package = ReadForEditing(catalog, esPath);
            var tunable = TunableReader.Read(package).FirstOrDefault(t => t.Name == "Caption" && t.Kind == TunableKind.Text);
            if (tunable is null)
            {
                return null;
            }

            return _services.Projects.Current?.State.GetAssetValue(esPath, tunable.Export, tunable.Path)?.Current ?? tunable.Value;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task LoadValuesAsync(AssetCatalog catalog, ModulePart part)
    {
        IsLoadingValues = true;
        try
        {
            var tunables = await Task.Run(() => TunableReader.Read(ReadForEditing(catalog, part.PackagePath))).ConfigureAwait(true);
            if (!ReferenceEquals(SelectedPart, part))
            {
                return;
            }

            var state = _services.Projects.Current?.State;
            _allRows = tunables
                .Select(t => new TunableRowViewModel(part.PackagePath, t, state?.GetAssetValue(part.PackagePath, t.Export, t.Path)?.Current ?? t.Value))
                .ToList();
            foreach (var row in _allRows)
            {
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TunableRowViewModel.IsPending))
                    {
                        UpdateSummary();
                    }
                };
            }

            RebuildGroups();
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
        {
            _allRows = [];
            Groups = [];
            ValuesSummary = Loc.F("Module.CouldNotRead", part.PackagePath, ex.Message);
        }
        finally
        {
            IsLoadingValues = false;
        }
    }

    /// <summary>The package as the mod starts from: stock from the catalog, or the in-memory rename-clone of its template.</summary>
    private CookedPackage ReadForEditing(AssetCatalog catalog, string packagePath)
    {
        _io.Wait();
        try
        {
            if (_packageCache.TryGetValue(packagePath, out var cached))
            {
                return cached;
            }

            CookedPackage package;
            if (_services.Projects.Current?.State.FindCloneOf(packagePath) is { } clone)
            {
                var pair = clone.Packages.First(p => string.Equals(p.New, packagePath, StringComparison.OrdinalIgnoreCase));
                var map = new PackageMap(clone.Packages.Select(p => new KeyValuePair<string, string>(p.Old, p.New)));
                var source = ModdableAssets.ReadPackage(catalog, pair.Old);
                var cloned = PackageCloner.Clone(source, pair.Old, map);
                package = CookedPackage.Parse(cloned.Bytes.UAsset, cloned.Bytes.UExp, cloned.UBulk, packagePath);
            }
            else
            {
                package = ModdableAssets.ReadPackage(catalog, packagePath);
            }

            _packageCache[packagePath] = package;
            return package;
        }
        finally
        {
            _io.Release();
        }
    }

    private void RebuildGroups()
    {
        var tokens = ValueFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = _allRows.Where(r => tokens.All(t => r.Tunable.Path.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Label.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Tunable.Group.Contains(t, StringComparison.OrdinalIgnoreCase)));
        if (KeyStatsOnly)
        {
            var key = rows.Where(r => KeyStats.Contains(r.Tunable.Name)).ToList();
            rows = key.Count > 0 ? key : rows;
        }

        Groups = rows
            .GroupBy(r => r.Tunable.Group, StringComparer.Ordinal)
            .Select(g => new TunableGroupViewModel(g.Key, g.ToList()))
            .ToList();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        PendingCount = _allRows.Count(r => r.IsPending);
        var shown = Groups.Sum(g => g.Rows.Count);
        var edited = _allRows.Count(r => r.IsOverridden);
        ValuesSummary = Loc.F("Module.ValuesSummary", shown, _allRows.Count, edited, PendingCount);
    }

    private void RefreshCommitted()
    {
        var state = _services.Projects.Current?.State;
        foreach (var row in _allRows)
        {
            var committed = state?.GetAssetValue(row.Package, row.Tunable.Export, row.Tunable.Path)?.Current ?? row.StockValue;
            if (!string.Equals(committed, row.CommittedValue, StringComparison.Ordinal) || row.IsPending && TunableValue.AreEqual(row.Tunable.Kind, row.Value, committed))
            {
                row.Commit(committed);
            }
        }

        if (SelectedItem?.Asset.EntitySetupPath is { } es && _allRows.FirstOrDefault(r => r.Tunable.Name == "Caption" && string.Equals(r.Package, es, StringComparison.OrdinalIgnoreCase)) is { } captionRow)
        {
            Caption = captionRow.CommittedValue;
        }

        UpdateSummary();
    }
}
