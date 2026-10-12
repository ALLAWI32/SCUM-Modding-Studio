using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Projects;

namespace ScumStudio.App.ViewModels;

/// <summary>A mod of the gallery: picture, name, maker, description, cells, size and date.</summary>
public sealed partial class ShareCardItem : ObservableObject
{
    /// <summary>Creates the card for <paramref name="listing"/>.</summary>
    public ShareCardItem(ModListing listing) => Listing = listing;

    /// <summary>The row of the gallery index.</summary>
    public ModListing Listing { get; }

    /// <summary>Its gallery id.</summary>
    public string Id => Listing.Id;

    /// <summary>Its name (plain text).</summary>
    public string Name => Listing.Name;

    /// <summary>Its description (plain text).</summary>
    public string Description => Listing.Description;

    /// <summary>"by Allawi".</summary>
    public string ByText => Loc.F("Share.Gallery.By", Listing.Author);

    /// <summary>"A_0, B_1 · 420 KB · 12/10/2026".</summary>
    public string Meta => Loc.F("Share.Gallery.Meta",
        Listing.Sectors.Count == 0 ? "—" : string.Join(", ", Listing.Sectors),
        SharePageViewModel.SizeText(Listing.Size),
        Listing.Created.ToLocalTime().ToString("d", CultureInfo.CurrentCulture));

    /// <summary>"Made with ScumStudio 3.1.0".</summary>
    public string VersionText => Loc.F("Share.Gallery.Version", Listing.AppVersion);

    /// <summary>The "Remove from gallery for everyone?" question.</summary>
    public string ConfirmText => Loc.F("Share.Moderate.Confirm", Listing.Name, Listing.Author);

    /// <summary>Its picture, once fetched.</summary>
    [ObservableProperty]
    private Bitmap? _thumb;

    /// <summary>The moderator asked to remove it and the question is showing.</summary>
    [ObservableProperty]
    private bool _isConfirming;

    /// <summary>Re-reads the composed texts after the UI language changed.</summary>
    public void Relabel()
    {
        OnPropertyChanged(nameof(ByText));
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(ConfirmText));
    }
}

/// <summary>A shared mod in the open project ("Added to this project").</summary>
public sealed partial class ShareInstalledItem : ObservableObject
{
    /// <summary>Creates the row.</summary>
    public ShareInstalledItem(ShareInstall install) => Install = install;

    /// <summary>What Remove takes back.</summary>
    public ShareInstall Install { get; }

    /// <summary>Its name.</summary>
    public string Name => Install.Name;

    /// <summary>"by Allawi · 12/10/2026".</summary>
    public string ByText => Loc.F("Share.Gallery.By", Install.Author) + " · " + Install.Installed.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);

    /// <summary>Re-reads the composed texts after the UI language changed.</summary>
    public void Relabel() => OnPropertyChanged(nameof(ByText));
}

/// <summary>What getting a mod would do, shown before anything changes.</summary>
public sealed partial class SharePendingViewModel : ObservableObject
{
    /// <summary>Creates the preview of <paramref name="plan"/> for <paramref name="project"/>.</summary>
    public SharePendingViewModel(string id, Project project, ShareContent content, ShareImportPlan plan)
    {
        Id = id;
        Project = project;
        Content = content;
        Plan = plan;
    }

    /// <summary>The mod's gallery id.</summary>
    public string Id { get; }

    /// <summary>The project the plan was made for.</summary>
    public Project Project { get; }

    /// <summary>The downloaded mod (in memory only: no archive is written).</summary>
    public ShareContent Content { get; }

    /// <summary>The worked-out step.</summary>
    public ShareImportPlan Plan { get; }

    /// <summary>"Add North airfield to your project?".</summary>
    public string Title => Loc.F("Share.Preview.Title", Content.Manifest.Name);

    /// <summary>One line per group, then renames, craftables and economy.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            var lines = Plan.Counts.OrderBy(c => Level.Editing.JournalSlice.Order(c.Key))
                .Select(c => Loc.F("Share.Preview.Counts", c.Value, SharePageViewModel.GroupName(c.Key))).ToList();
            if (Plan.Renamed.Count > 0)
            {
                lines.Add(Loc.F("Share.Preview.Renamed", Plan.Renamed.Count));
            }

            if (Plan.CraftablesAdded.Count > 0)
            {
                lines.Add(Loc.F("Share.Preview.Craftables", Plan.CraftablesAdded.Count));
            }

            if (Plan.EconomySections.Count > 0)
            {
                lines.Add(Loc.F("Share.Preview.Economy", Plan.EconomySections.Count));
            }

            return lines;
        }
    }

    /// <summary>The game builds differ.</summary>
    public bool HasBuildWarning => Plan.GameBuildDiffers;

    /// <summary>"Made for game build …; your project is on …".</summary>
    public string BuildWarning => Loc.F("Share.Preview.BuildDiffers", Plan.AuthorBuild, Plan.YourBuild);

    /// <summary>The edits clash with the project's own: nothing can be added.</summary>
    public bool HasConflict => Plan.ConflictDetail is not null;

    /// <summary>Why (the editor's own words, shown under the localized line).</summary>
    public string ConflictDetail => Plan.ConflictDetail ?? string.Empty;

    /// <summary>Re-reads the composed texts after the UI language changed.</summary>
    public void Relabel()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(BuildWarning));
    }
}

/// <summary>The Gallery tab (browse, Get with a preview, add as one step) and "Added to this project" (Remove).</summary>
public sealed partial class SharePageViewModel
{
    private const int MaxThumbsFetched = 60;
    private bool _galleryLoaded;
    private CancellationTokenSource? _thumbs;

    /// <summary>The mods in the gallery, newest first.</summary>
    public ObservableCollection<ShareCardItem> Gallery { get; } = [];

    /// <summary>The shared mods in the open project, oldest first.</summary>
    public ObservableCollection<ShareInstalledItem> Installed { get; } = [];

    /// <summary>Loading, offline, empty, or what happened to the last Get; empty when there is nothing to say.</summary>
    [ObservableProperty]
    private string _galleryStatus = string.Empty;

    /// <summary>The technical detail under <see cref="GalleryStatus"/> (a conflict's own words), or empty.</summary>
    [ObservableProperty]
    private string _galleryDetail = string.Empty;

    /// <summary>The gallery is being read.</summary>
    [ObservableProperty]
    private bool _isGalleryLoading;

    /// <summary>The mod got and waiting for "Add to my project", or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPendingCommand))]
    private SharePendingViewModel? _pending;

    /// <summary>A preview is showing.</summary>
    public bool HasPending => Pending is not null;

    /// <summary>The project has no shared mods.</summary>
    public bool HasNoInstalled => Installed.Count == 0;

    /// <summary>Reads the gallery (only when a tab asks: never at startup) and then its pictures.</summary>
    [RelayCommand]
    private async Task RefreshGalleryAsync()
    {
        _thumbs?.Cancel();
        IsGalleryLoading = true;
        GalleryStatus = Loc.T("Share.Gallery.Loading");
        GalleryDetail = string.Empty;
        try
        {
            var result = await _client.GetGalleryAsync(_life.Token).ConfigureAwait(true);
            Gallery.Clear();
            if (!result.Ok || result.Value is not { } listings)
            {
                GalleryStatus = ErrorText(result.Error);
                return;
            }

            _galleryLoaded = true;
            foreach (var listing in listings)
            {
                Gallery.Add(new ShareCardItem(listing));
            }

            GalleryStatus = Gallery.Count == 0 ? Loc.T("Share.Gallery.Empty") : string.Empty;
            _thumbs = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            _ = LoadThumbsAsync(Gallery.Take(MaxThumbsFetched).ToList(), _thumbs.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsGalleryLoading = false;
        }
    }

    private async Task LoadThumbsAsync(IReadOnlyList<ShareCardItem> cards, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var card in cards)
            {
                var result = await _client.GetThumbnailAsync(card.Id, cancellationToken).ConfigureAwait(true);
                if (result.Ok)
                {
                    card.Thumb = ToBitmap(result.Value);
                }
                else if (result.Error == ModShareError.Offline)
                {
                    return; // the rest would wait for the same timeout
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Downloads a mod (kept in memory), reads it and works out what it would do; nothing changes yet.</summary>
    [RelayCommand]
    private async Task GetAsync(ShareCardItem? card)
    {
        if (card is null || IsBusy)
        {
            return;
        }

        if (_services.Projects.Current is not { } project)
        {
            GalleryStatus = Loc.T("Share.NoProject");
            return;
        }

        IsBusy = true;
        Pending = null;
        GalleryStatus = Loc.F("Share.Getting", card.Name);
        GalleryDetail = string.Empty;
        try
        {
            var (ran, result) = await _services.Operations.RunAsync(Loc.F("Share.Getting", card.Name), (_, ct) => _client.DownloadAsync(card.Id, ct)).ConfigureAwait(true);
            if (!ran || result is null)
            {
                GalleryStatus = Loc.T("Share.Offline");
                return;
            }

            if (!result.Ok || result.Value is not { } zip)
            {
                GalleryStatus = ErrorText(result.Error);
                return;
            }

            var content = await Task.Run(() => ShareFile.Read(zip)).ConfigureAwait(true);
            var traders = await GameTradersAsync().ConfigureAwait(true);
            if (!ReferenceEquals(project, _services.Projects.Current))
            {
                GalleryStatus = string.Empty; // the project changed while it downloaded
                return;
            }

            var plan = ShareImport.Plan(project, content, traders, _services.Economy.Economy,
                Loc.F("Share.Batch.Title", content.Manifest.Name, content.Manifest.Author));
            Pending = new SharePendingViewModel(card.Id, project, content, plan);
            GalleryStatus = string.Empty;
        }
        catch (ShareException ex)
        {
            GalleryStatus = ProblemText(ex.Problem);
            GalleryDetail = ex.Message;
        }
        catch (InvalidDataException ex)
        {
            GalleryStatus = Loc.T("Share.BadPackage");
            GalleryDetail = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The game's traders, so a placed trader of the mod never takes a stock one's name; none when the paks cannot tell.</summary>
    private async Task<IEnumerable<(string Name, string Type)>> GameTradersAsync()
    {
        try
        {
            return (await _services.Economy.GameTradersAsync().ConfigureAwait(true)).Select(t => (t.Name, t.Type));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning(ex, "Share: the game's traders could not be read: {Message}", ex.Message);
            return [];
        }
    }

    private bool CanApplyPending() => Pending is { HasConflict: false } && !IsBusy;

    /// <summary>Adds the previewed mod as one step (Ctrl+Z undoes its edits; Remove takes all of it back).</summary>
    [RelayCommand(CanExecute = nameof(CanApplyPending))]
    private void ApplyPending()
    {
        if (Pending is not { } pending || !ReferenceEquals(pending.Project, _services.Projects.Current))
        {
            Pending = null;
            return;
        }

        try
        {
            var install = ShareImport.Apply(pending.Project, pending.Plan, pending.Id, pending.Content, _services.Economy.Economy);
            _services.Economy.Save();
            Pending = null;
            _services.Projects.Refresh(); // Map, Craftables and Economy follow
            _services.Logger.LogInformation("Share: {Name} ({Id}) added to the project as one step.", install.Name, install.Id);
            GalleryStatus = Loc.F("Share.Added", install.Name);
            _services.Notifications.Success(Loc.F("Share.Added", install.Name));
        }
        catch (ShareException ex)
        {
            GalleryStatus = ProblemText(ex.Problem);
            GalleryDetail = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _services.Logger.LogError(ex, "Share: could not add the mod: {Message}", ex.Message);
            GalleryStatus = Loc.F("Share.ApplyFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void CancelPending()
    {
        Pending = null;
        GalleryStatus = GalleryDetail = string.Empty;
    }

    /// <summary>Re-reads the open project's shared mods.</summary>
    private void RefreshInstalled()
    {
        Installed.Clear();
        if (_services.Projects.Current is { } project)
        {
            foreach (var install in ShareImport.Installed(project))
            {
                Installed.Add(new ShareInstalledItem(install));
            }
        }

        OnPropertyChanged(nameof(HasNoInstalled));
    }

    /// <summary>Takes a shared mod out of the project: its edits (one step), the craftables it added and the economy as it was.</summary>
    [RelayCommand]
    private void RemoveInstalled(ShareInstalledItem? item)
    {
        if (item is null || _services.Projects.Current is not { } project)
        {
            return;
        }

        try
        {
            ShareImport.Remove(project, item.Install, _services.Economy.Economy, Loc.F("Share.Remove.Batch.Title", item.Name));
            _services.Economy.Save();
            _services.Projects.Refresh();
            _services.Notifications.Success(Loc.F("Share.Removed", item.Name));
            RefreshInstalled();
        }
        catch (ShareException ex)
        {
            _services.Notifications.Warning(ProblemText(ex.Problem), ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _services.Logger.LogError(ex, "Share: could not remove {Name}: {Message}", item.Name, ex.Message);
            _services.Notifications.Error(Loc.F("Share.RemoveFailed", item.Name), ex.Message);
        }
    }
}
