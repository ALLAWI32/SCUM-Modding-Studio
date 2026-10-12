using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Core.Sharing;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.App.ViewModels;

/// <summary>The tabs of the Share card.</summary>
public enum ShareTab
{
    /// <summary>Upload the project's edits.</summary>
    Share,

    /// <summary>Browse and get other players' mods.</summary>
    Gallery,

    /// <summary>The shared mods in the open project.</summary>
    Installed,

    /// <summary>Remove uploads (only with the moderator key on this PC).</summary>
    Moderate,
}

/// <summary>A tab of the Share card, named in the current language.</summary>
/// <param name="Tab">Which tab.</param>
/// <param name="Key">Its string key (the tooltip is <c>Key.Tip</c>).</param>
/// <param name="IconKey">Its glyph.</param>
public sealed record ShareTabItem(ShareTab Tab, string Key, string IconKey)
{
    /// <summary>Its name.</summary>
    public string Title => Loc.T(Key);

    /// <summary>What it is for.</summary>
    public string Tip => Loc.T(Key + ".Tip");
}

/// <summary>A map cell of the "What to share" list with the number of edits a share of it carries.</summary>
public sealed partial class ShareGroupItem : ObservableObject
{
    private readonly Action _changed;

    /// <summary>Creates the row; <paramref name="changed"/> runs when it is ticked or cleared.</summary>
    public ShareGroupItem(string key, int edits, Action changed)
    {
        Key = key;
        Edits = edits;
        _changed = changed;
    }

    /// <summary>The cell (<c>A_0</c>).</summary>
    public string Key { get; }

    /// <summary>Its edits.</summary>
    public int Edits { get; }

    /// <summary>"A_0 · 12 edits".</summary>
    public string Label => Loc.F("Share.Sector.Item", Key, Edits);

    /// <summary>Chosen.</summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Re-reads <see cref="Label"/> after the UI language changed.</summary>
    public void Relabel() => OnPropertyChanged(nameof(Label));

    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>
/// "Share your mod" (owner, 2026-10-12: "Share your mod … upload your work"): a card over the window with four tabs. Share
/// packs the project's own edits of the chosen map cells (never game files) with a name, a description and a picture taken
/// in the Map or from a file, and uploads them; Gallery lists what others shared and adds one to the project as one
/// undoable step; "Added to this project" takes one out again; Moderate (only with the owner's key file) removes uploads.
/// Nothing talks to the gallery until a tab asks for it.
/// </summary>
public sealed partial class SharePageViewModel : PageViewModel, IDisposable
{
    /// <summary>Height of a picture taken in the Map (the width follows the view, at most <see cref="ViewportCapture.MaxWidth"/>).</summary>
    public const int CaptureHeight = 1080;

    private readonly AppServices _services;
    private readonly Func<string, PageViewModel?> _navigate;
    private readonly ModShareService _client;
    private readonly CancellationTokenSource _life = new();
    private bool _choosing;

    /// <summary>Creates the card over <paramref name="services"/>; <paramref name="navigate"/> switches the workspace page (the Map for a picture).</summary>
    public SharePageViewModel(AppServices services, Func<string, PageViewModel?> navigate, ModShareService? client = null, IViewportCapture? capture = null)
        : base("share", "Share your mod", "Only your project's own edits travel: never game files or paks.")
    {
        _services = services;
        _navigate = navigate;
        _client = client ?? new ModShareService(services.Logger, () => services.Settings.Load().ModShareUrl);
        Capture = capture;
        _displayName = services.Settings.Load().ModShareName ?? string.Empty;
        _tabs = BuildTabs(moderator: false);
        _selectedTab = _tabs[0];
        services.Projects.Changed += OnProjectsChanged;
        Loc.Instance.LanguageChanged += OnUiLanguageChanged;
    }

    /// <summary>Takes the picture in the Map; set by the view once it is in a window.</summary>
    public IViewportCapture? Capture { get; set; }

    /// <summary>The card is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardShown))]
    private bool _isOpen;

    /// <summary>The card steps aside for the Map: only the capture bar shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardShown))]
    private bool _isCapturing;

    /// <summary>The card (not the capture bar) is visible.</summary>
    public bool IsCardShown => IsOpen && !IsCapturing;

    /// <summary>The tabs (Moderate only with the key file).</summary>
    [ObservableProperty]
    private IReadOnlyList<ShareTabItem> _tabs;

    /// <summary>The open tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShareTab), nameof(IsGalleryTab), nameof(IsInstalledTab), nameof(IsModerateTab))]
    private ShareTabItem? _selectedTab;

    /// <summary>The Share tab is open.</summary>
    public bool IsShareTab => SelectedTab?.Tab == ShareTab.Share;

    /// <summary>The Gallery tab is open.</summary>
    public bool IsGalleryTab => SelectedTab?.Tab == ShareTab.Gallery;

    /// <summary>The "Added to this project" tab is open.</summary>
    public bool IsInstalledTab => SelectedTab?.Tab == ShareTab.Installed;

    /// <summary>The Moderate tab is open.</summary>
    public bool IsModerateTab => SelectedTab?.Tab == ShareTab.Moderate;

    /// <summary>The moderator key file is on this PC (checked each time the card opens).</summary>
    [ObservableProperty]
    private bool _isModerator;

    /// <summary>Sharing, getting or removing right now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShareCommand), nameof(CaptureCommand), nameof(ImportPictureCommand), nameof(ApplyPendingCommand))]
    private bool _isBusy;

    /// <summary>A project is open.</summary>
    public bool HasProject => _services.Projects.HasProject;

    /// <summary>This build knows a gallery address.</summary>
    public bool IsConfigured => _client.IsConfigured;

    // ------------------------------------------------------------------ Your name

    /// <summary>The name shown with the user's mods (kept in the settings when the box is left, on Share and on Close).</summary>
    [ObservableProperty]
    private string _displayName;

    // ------------------------------------------------------------------ What to share

    /// <summary>The map cells the project has edits in.</summary>
    public ObservableCollection<ShareGroupItem> Sectors { get; } = [];

    /// <summary>Every cell and the island-wide edits.</summary>
    [ObservableProperty]
    private bool _wholeMap;

    /// <summary>Share the edits of levels that belong to no cell.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChoice))]
    private bool _includeIslandWide;

    /// <summary>Island-wide edits in the project.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IslandWideText), nameof(HasIslandWide), nameof(HasChoice))]
    private int _islandWideEdits;

    /// <summary>Share the asset edits (vehicles, weapons, items).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChoice))]
    private bool _includeAssets;

    /// <summary>Asset edits in the project.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssetsText), nameof(HasAssets), nameof(HasChoice))]
    private int _assetEdits;

    /// <summary>Carry the project's craftables (not those made from an imported 3D model).</summary>
    [ObservableProperty]
    private bool _includeCraftables = true;

    /// <summary>Craftables that travel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CraftablesText), nameof(HasCraftables))]
    private int _craftablesCount;

    /// <summary>Craftables made from an imported 3D model (cooked game files: they stay home).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CraftablesSkippedText), nameof(HasSkippedCraftables))]
    private int _craftablesSkipped;

    /// <summary>Carry the economy sections of the chosen cells' traders.</summary>
    [ObservableProperty]
    private bool _includeEconomy = true;

    /// <summary>Traders whose economy section the choice carries.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EconomyText), nameof(HasEconomy))]
    private int _economyTraders;

    /// <summary>The groups the choice pulls in (a copy of something made there), or empty.</summary>
    [ObservableProperty]
    private string _pulledInText = string.Empty;

    /// <summary>Why the choice cannot be shared as it is (the technical detail), or empty.</summary>
    [ObservableProperty]
    private string _choiceDetail = string.Empty;

    /// <summary>"Island-wide edits (3)".</summary>
    public string IslandWideText => Loc.F("Share.IslandWide", IslandWideEdits);

    /// <summary>"Vehicles, weapons and other assets (2)".</summary>
    public string AssetsText => Loc.F("Share.Assets", AssetEdits);

    /// <summary>"Craftables (4)".</summary>
    public string CraftablesText => Loc.F("Share.Craftables", CraftablesCount);

    /// <summary>"Imported 3D models that stay on your PC: 1".</summary>
    public string CraftablesSkippedText => Loc.F("Share.Craftables.Skipped", CraftablesSkipped);

    /// <summary>"Trader economy (2 trader(s))".</summary>
    public string EconomyText => Loc.F("Share.Economy", EconomyTraders);

    /// <summary>The project has island-wide edits.</summary>
    public bool HasIslandWide => IslandWideEdits > 0;

    /// <summary>The project has asset edits.</summary>
    public bool HasAssets => AssetEdits > 0;

    /// <summary>The project has craftables that can travel.</summary>
    public bool HasCraftables => CraftablesCount > 0;

    /// <summary>Some craftables stay home.</summary>
    public bool HasSkippedCraftables => CraftablesSkipped > 0;

    /// <summary>The choice carries economy sections.</summary>
    public bool HasEconomy => EconomyTraders > 0;

    /// <summary>At least one group of edits is chosen.</summary>
    public bool HasChoice => Sectors.Any(s => s.IsChecked) || (IncludeIslandWide && HasIslandWide) || (IncludeAssets && HasAssets);

    // ------------------------------------------------------------------ Name, description, picture

    /// <summary>The mod's name in the gallery.</summary>
    [ObservableProperty]
    private string _modName = string.Empty;

    /// <summary>The mod's description.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DescriptionLengthText))]
    private string _description = string.Empty;

    /// <summary>"120 / 600".</summary>
    public string DescriptionLengthText => Loc.F("Share.Description.Length", Description.Length, ShareFile.MaxDescription);

    /// <summary>Longest mod name.</summary>
    public static int MaxModName => ShareFile.MaxName;

    /// <summary>Longest description.</summary>
    public static int MaxDescription => ShareFile.MaxDescription;

    /// <summary>Longest display name.</summary>
    public static int MaxDisplayName => ShareFile.MaxAuthor;

    /// <summary>The picture as it will be uploaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    private SharePictures? _pictures;

    /// <summary>The picture, shown on the card.</summary>
    [ObservableProperty]
    private Bitmap? _pictureImage;

    /// <summary>A picture is set.</summary>
    public bool HasPicture => Pictures is not null;

    /// <summary>What happened to the last share (or why it cannot start).</summary>
    [ObservableProperty]
    private string _shareStatus = string.Empty;

    // ------------------------------------------------------------------ Card

    /// <summary>Shows the card (and ends a capture that was left open).</summary>
    [RelayCommand]
    public void Open()
    {
        using (var key = ModeratorKey.TryLoad())
        {
            IsModerator = key is not null;
        }

        var tab = SelectedTab?.Tab ?? ShareTab.Share;
        Tabs = BuildTabs(IsModerator);
        SelectedTab = Tabs.FirstOrDefault(t => t.Tab == tab) ?? Tabs[0];
        IsCapturing = false;
        RefreshProject();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        SaveDisplayName();
        IsCapturing = false;
        IsOpen = false;
    }

    /// <summary>Keeps <see cref="DisplayName"/> in the settings (null when blank); nothing is written when it did not change.</summary>
    public void SaveDisplayName()
    {
        var name = Clean(DisplayName, MaxDisplayName);
        if (name != DisplayName)
        {
            DisplayName = name;
        }

        var kept = name.Length == 0 ? null : name;
        if (_services.Settings.Load().ModShareName != kept)
        {
            _services.UpdateSettings(s => s with { ModShareName = kept });
        }
    }

    /// <summary>Forgets the name on this PC.</summary>
    [RelayCommand]
    private void ForgetName()
    {
        DisplayName = string.Empty;
        SaveDisplayName();
    }

    // ------------------------------------------------------------------ Picture

    /// <summary>Switches to the Map with the capture bar: the user flies the camera to the spot.</summary>
    [RelayCommand]
    private void StartCapture()
    {
        _navigate("map");
        IsCapturing = true;
    }

    [RelayCommand]
    private void CancelCapture() => IsCapturing = false;

    /// <summary>Draws the Map's current view again off screen at <see cref="CaptureHeight"/> and makes the picture of it.</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task CaptureAsync()
    {
        IsBusy = true;
        try
        {
            var shot = Capture is { } capture ? await capture.CaptureAsync(CaptureHeight, ViewportCapture.MaxWidth, _life.Token).ConfigureAwait(true) : null;
            if (shot is not { } frame)
            {
                _services.Notifications.Warning(Loc.T("Share.Title"), Loc.T("Share.Capture.NoMap"));
                return;
            }

            SetPictures(await Task.Run(() => SharePicture.FromRgba(frame.Rgba, frame.Width, frame.Height)).ConfigureAwait(true));
            ShareStatus = string.Empty;
            IsCapturing = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A GL error comes back from the render thread as it is; the card stays usable.
            _services.Logger.LogWarning(ex, "Share: the picture could not be taken: {Message}", ex.Message);
            _services.Notifications.Warning(Loc.T("Share.Title"), Loc.F("Share.Capture.Failed", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Uses an image file as the picture (scaled down keeping its shape).</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task ImportPictureAsync()
    {
        var file = await _services.Dialogs.OpenFileAsync(Loc.T("Share.Picture.Import"), "png;jpg;jpeg;bmp;gif;webp", Loc.T("Share.Picture.Filter")).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            SetPictures(await Task.Run(() => SharePicture.FromFile(file)).ConfigureAwait(true));
            ShareStatus = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            ShareStatus = Loc.F("Share.Picture.Unreadable", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetPictures(SharePictures pictures)
    {
        Pictures = pictures;
        PictureImage = ToBitmap(pictures.Preview);
    }

    // ------------------------------------------------------------------ Share

    private bool CanWork() => !IsBusy;

    private bool CanShare() => !IsBusy && HasProject;

    /// <summary>Packs the chosen edits with the picture and uploads them; the gallery opens on success.</summary>
    [RelayCommand(CanExecute = nameof(CanShare))]
    private async Task ShareAsync()
    {
        SaveDisplayName();
        ModName = Clean(ModName, MaxModName);
        if (MissingForShare() is { } missing)
        {
            ShareStatus = missing;
            return;
        }

        var project = _services.Projects.Current!;
        var info = new ShareManifest
        {
            Name = ModName,
            Author = DisplayName,
            Description = new string(Description.Replace("\r\n", "\n").Where(c => c == '\n' || !char.IsControl(c)).ToArray()).Trim(),
            AppVersion = MainWindowViewModel.ShortVersion,
            Created = DateTimeOffset.UtcNow,
        };
        IsBusy = true;
        ShareStatus = Loc.F("Share.Uploading", info.Name);
        try
        {
            // Collected here (the journal and the economy are the UI thread's); only the zipping runs aside.
            var content = ShareFile.Collect(project, Choice(), info, Pictures!.Preview, Pictures.Thumb, IncludeEconomy ? _services.Economy.Economy : null,
                IncludeCraftables, out _, out _);
            var zip = await Task.Run(() => ShareFile.Write(content)).ConfigureAwait(true);
            var (ran, result) = await _services.Operations.RunAsync(Loc.F("Share.Uploading", info.Name), (_, ct) => _client.UploadAsync(zip, ct)).ConfigureAwait(true);
            if (!ran || result is null)
            {
                ShareStatus = Loc.T("Share.Offline");
                return;
            }

            if (!result.Ok)
            {
                ShareStatus = ErrorText(result.Error, result.RetryAfterSeconds, zip.Length);
                return;
            }

            _services.Logger.LogInformation("Share: {Name} uploaded as {Id} ({Bytes} bytes, {Edits} edits).", info.Name, result.Value, zip.Length, content.Edits.Ops.Count);
            ShareStatus = Loc.F("Share.Uploaded", info.Name);
            _services.Notifications.Success(Loc.F("Share.Uploaded", info.Name));
            _galleryLoaded = false;
            SelectedTab = Tabs.First(t => t.Tab == ShareTab.Gallery);
        }
        catch (ShareException ex)
        {
            ShareStatus = ProblemText(ex.Problem) + Environment.NewLine + ex.Message;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShareStatus = Loc.F("Share.Failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>What still has to be filled in before sharing, or null.</summary>
    private string? MissingForShare() =>
        !HasProject ? Loc.T("Share.NoProject")
        : !IsConfigured ? Loc.T("Share.NotAvailable")
        : DisplayName.Length == 0 ? Loc.T("Share.Name.Missing")
        : ModName.Length == 0 ? Loc.T("Share.ModName.Missing")
        : !HasChoice ? Loc.T("Share.NothingChosen")
        : Pictures is null ? Loc.T("Share.Picture.Missing")
        : null;

    private SectorChoice Choice() => new(
        Sectors.Where(s => s.IsChecked).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
        IncludeIslandWide && HasIslandWide,
        IncludeAssets && HasAssets);

    /// <summary>Re-reads the project's groups, craftables and installed mods (keeps what was ticked).</summary>
    private void RefreshProject()
    {
        var ticked = Sectors.Where(s => s.IsChecked).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _choosing = true;
        Sectors.Clear();
        IslandWideEdits = AssetEdits = CraftablesCount = CraftablesSkipped = 0;
        if (_services.Projects.Current is { } project)
        {
            foreach (var group in JournalSlice.Groups(project.Journal.Applied).OrderBy(g => JournalSlice.Order(g.Key)))
            {
                if (group.Key == JournalSlice.IslandKey)
                {
                    IslandWideEdits = group.Edits;
                }
                else if (group.Key == JournalSlice.AssetsKey)
                {
                    AssetEdits = group.Edits;
                }
                else
                {
                    Sectors.Add(new ShareGroupItem(group.Key, group.Edits, OnChoiceChanged) { IsChecked = ticked.Contains(group.Key) });
                }
            }

            try
            {
                var craftables = CraftablesFile.Load(project.DirectoryPath).Items;
                CraftablesSkipped = craftables.Count(c => c.Imported is not null);
                CraftablesCount = craftables.Count - CraftablesSkipped;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                _services.Logger.LogWarning("Share: craftables.json could not be read: {Message}", ex.Message);
            }
        }

        _choosing = false;
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(IsConfigured));
        UpdateChoice();
        RefreshInstalled();
    }

    private void OnChoiceChanged() => UpdateChoice();

    partial void OnIncludeIslandWideChanged(bool value) => UpdateChoice();

    partial void OnIncludeAssetsChanged(bool value) => UpdateChoice();

    partial void OnIncludeEconomyChanged(bool value) => UpdateChoice();

    partial void OnWholeMapChanged(bool value)
    {
        if (_choosing)
        {
            return;
        }

        _choosing = true;
        foreach (var sector in Sectors)
        {
            sector.IsChecked = value;
        }

        IncludeIslandWide = value && HasIslandWide;
        _choosing = false;
        UpdateChoice();
    }

    /// <summary>Works out what the choice pulls in and which traders' economy it carries (the same rules as the upload).</summary>
    private void UpdateChoice()
    {
        if (_choosing)
        {
            return;
        }

        _choosing = true;
        WholeMap = (Sectors.Count > 0 || HasIslandWide) && Sectors.All(s => s.IsChecked) && (!HasIslandWide || IncludeIslandWide);
        _choosing = false;
        OnPropertyChanged(nameof(HasChoice));
        PulledInText = ChoiceDetail = string.Empty;
        EconomyTraders = 0;
        if (_services.Projects.Current is not { } project || !HasChoice)
        {
            return;
        }

        try
        {
            var probe = ShareFile.Collect(project, Choice(), new ShareManifest(), [], [], _services.Economy.Economy, craftables: false, out var pulledIn, out _);
            PulledInText = pulledIn.Count == 0 ? string.Empty : Loc.F("Share.PulledIn", string.Join(", ", pulledIn.Select(GroupName)));
            if (IncludeEconomy && probe.Files.TryGetValue(EconomyOverride.FileName, out var economy))
            {
                EconomyTraders = EconomyOverride.Parse(Encoding.UTF8.GetString(economy)).Traders.Count;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ShareException)
        {
            ChoiceDetail = Loc.F("Share.Failed", ex.Message); // the slice does not stand on its own: the detail says why
        }
    }

    // ------------------------------------------------------------------ Texts

    /// <summary>A group's name: the cell, "Island-wide" or "Assets".</summary>
    public static string GroupName(string key) =>
        key == JournalSlice.IslandKey ? Loc.T("Share.Group.IslandWide")
        : key == JournalSlice.AssetsKey ? Loc.T("Share.Group.Assets")
        : key;

    /// <summary>"420 KB" / "1.4 MB".</summary>
    public static string SizeText(long bytes) =>
        bytes < 1024 * 1024
            ? Loc.F("Share.Size.KB", Math.Max(1, (bytes + 1023) / 1024))
            : Loc.F("Share.Size.MB", (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture));

    /// <summary>The sentence for a gallery error.</summary>
    public static string ErrorText(ModShareError error, int? retryAfterSeconds = null, long size = 0) => error switch
    {
        ModShareError.NotConfigured => Loc.T("Share.NotAvailable"),
        ModShareError.Offline => Loc.T("Share.Offline"),
        ModShareError.TooLarge => Loc.F("Share.TooLarge", size > 0 ? SizeText(size) : "> " + SizeText(ModShareService.MaxPackageBytes), SizeText(ModShareService.MaxPackageBytes)),
        ModShareError.RateLimited => Loc.F("Share.RateLimited", Math.Max(1, ((retryAfterSeconds ?? 3600) + 59) / 60)),
        ModShareError.Rejected => Loc.T("Share.Rejected"),
        ModShareError.NotFound => Loc.T("Share.NotFound"),
        ModShareError.Unauthorized => Loc.T("Share.Moderate.Refused"),
        ModShareError.Full => Loc.T("Share.Full"),
        _ => Loc.T("Share.BadAnswer"),
    };

    /// <summary>The sentence for a package problem (the technical detail goes under it).</summary>
    public static string ProblemText(ShareProblem problem) => problem switch
    {
        ShareProblem.GameData => Loc.T("Share.GameData"),
        ShareProblem.TooLarge => Loc.F("Share.TooLarge", "> " + SizeText(ShareFile.MaxZipBytes), SizeText(ShareFile.MaxZipBytes)),
        ShareProblem.NewerFormat or ShareProblem.UnknownEdit => Loc.T("Share.NewerApp"),
        ShareProblem.Conflicts => Loc.T("Share.Preview.Conflict"),
        ShareProblem.NoProject => Loc.T("Share.NoProject"),
        _ => Loc.T("Share.BadPackage"),
    };

    /// <summary>Trimmed, without control characters, at most <paramref name="max"/> characters.</summary>
    private static string Clean(string? text, int max)
    {
        var clean = new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..max].TrimEnd();
    }

    internal static Bitmap? ToBitmap(byte[]? jpeg)
    {
        if (jpeg is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(jpeg);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // a picture the platform cannot decode: the card shows its placeholder
        }
    }

    private static IReadOnlyList<ShareTabItem> BuildTabs(bool moderator)
    {
        var tabs = new List<ShareTabItem>
        {
            new(ShareTab.Share, "Share.Tab.Share", "Icon.Share"),
            new(ShareTab.Gallery, "Share.Tab.Gallery", "Icon.Grid"),
            new(ShareTab.Installed, "Share.Tab.Installed", "Icon.Projects"),
        };
        if (moderator)
        {
            tabs.Add(new(ShareTab.Moderate, "Share.Tab.Moderate", "Icon.Shield"));
        }

        return tabs;
    }

    partial void OnSelectedTabChanged(ShareTabItem? value)
    {
        switch (value?.Tab)
        {
            case ShareTab.Gallery or ShareTab.Moderate when IsOpen && !_galleryLoaded && !IsGalleryLoading:
                _ = RefreshGalleryAsync();
                break;
            case ShareTab.Installed:
                RefreshInstalled();
                break;
        }
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value && SelectedTab?.Tab is ShareTab.Gallery or ShareTab.Moderate && !_galleryLoaded && !IsGalleryLoading)
        {
            _ = RefreshGalleryAsync();
        }
    }

    private void OnProjectsChanged(object? sender, EventArgs e) => _services.Dispatcher.Invoke(() =>
    {
        if (Pending is { } pending && !ReferenceEquals(pending.Project, _services.Projects.Current))
        {
            Pending = null; // planned for the project that was open before
        }

        if (IsOpen)
        {
            RefreshProject();
        }
    });

    private void OnUiLanguageChanged(object? sender, EventArgs e) => _services.Dispatcher.Invoke(() =>
    {
        var tab = SelectedTab?.Tab;
        Tabs = BuildTabs(IsModerator);
        SelectedTab = Tabs.FirstOrDefault(t => t.Tab == tab) ?? Tabs[0];
        foreach (var sector in Sectors)
        {
            sector.Relabel();
        }

        foreach (var card in Gallery)
        {
            card.Relabel();
        }

        foreach (var item in Installed)
        {
            item.Relabel();
        }

        Pending?.Relabel();
        OnLanguageChanged();
        foreach (var name in new[] { nameof(DescriptionLengthText), nameof(IslandWideText), nameof(AssetsText), nameof(CraftablesText), nameof(CraftablesSkippedText), nameof(EconomyText) })
        {
            OnPropertyChanged(name);
        }

        UpdateChoice();
    });

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Projects.Changed -= OnProjectsChanged;
        Loc.Instance.LanguageChanged -= OnUiLanguageChanged;
        _life.Cancel();
        _life.Dispose();
    }
}
