using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;

namespace ScumStudio.App.ViewModels;

/// <summary>One line of release notes as shown: a heading, a bullet or plain text (Markdown marks removed).</summary>
public sealed record NoteLine(string Text, bool IsHeading, bool IsBullet)
{
    /// <summary>Plain text line.</summary>
    public bool IsText => !IsHeading && !IsBullet;
}

/// <summary>Updates (see <see cref="Updater"/>): the header's Update button, the update card, and "what's new" after one.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Asks GitHub for a newer release; null (tests, headless runs) never checks.</summary>
    public Func<CancellationToken, Task<ReleaseInfo?>>? UpdateCheck { get; set; }

    /// <summary>Closes the app (after an update was put in place and the new version started).</summary>
    public Action? RequestShutdown { get; set; }

    /// <summary>How often the app asks GitHub again while it runs (a release published meanwhile shows up).</summary>
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(30);

    /// <summary>A newer release, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateButtonText), nameof(UpdateButtonTip), nameof(UpdateTitle), nameof(UpdateNotes))]
    private ReleaseInfo? _update;

    /// <summary>Asking GitHub right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText), nameof(UpdateButtonTip))]
    [NotifyCanExecuteChangedFor(nameof(OpenUpdateCommand))]
    private bool _isCheckingUpdate;

    /// <summary>The last check could not reach GitHub.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText), nameof(UpdateButtonTip))]
    private bool _updateCheckFailed;

    /// <summary>The update card is open.</summary>
    [ObservableProperty]
    private bool _isUpdateOpen;

    /// <summary>Downloading or installing.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _isUpdating;

    /// <summary>Download progress 0..100.</summary>
    [ObservableProperty]
    private double _updateProgress;

    /// <summary>What the update is doing, or why it failed.</summary>
    [ObservableProperty]
    private string _updateStatus = string.Empty;

    /// <summary>Notes of the version just installed, shown once after the restart.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWhatsNewOpen))]
    private IReadOnlyList<NoteLine>? _whatsNew;

    /// <summary>True when a newer release is out.</summary>
    public bool HasUpdate => Update is not null;

    /// <summary>The header's update button shows (always, where the app can check).</summary>
    public bool CanCheckUpdates => UpdateCheck is not null;

    /// <summary>"Update 0.2.2", "Checking …", "Up to date · 0.2.1" or "Updates" (GitHub not reached).</summary>
    public string UpdateButtonText => Update is { } u ? Loc.F("Update.Button", u.Version.ToString(3))
        : IsCheckingUpdate ? Loc.T("Update.Checking")
        : UpdateCheckFailed ? Loc.T("Update.Offline")
        : Loc.F("Update.UpToDate", Updater.Current.ToString(3));

    /// <summary>What the update button does now.</summary>
    public string UpdateButtonTip => Update is not null ? Loc.T("Update.Button.Tip")
        : UpdateCheckFailed ? Loc.T("Update.Offline.Tip")
        : Loc.F("Update.UpToDate.Tip", Updater.Current.ToString(3));

    /// <summary>"Version 0.2.0 is available".</summary>
    public string UpdateTitle => Update is { } u ? Loc.F("Update.Title", u.Version.ToString(3)) : string.Empty;

    /// <summary>"You have 0.1.0 ...".</summary>
    public string UpdateSubtitle => Loc.F("Update.Subtitle", Updater.Current.ToString(3));

    /// <summary>The newer release's notes.</summary>
    public IReadOnlyList<NoteLine> UpdateNotes => Update is { } u ? ReleaseNotes(u.Notes) : [];

    /// <summary>True while the "what's new" card shows.</summary>
    public bool IsWhatsNewOpen => WhatsNew is { Count: > 0 };

    /// <summary>"What's new in 0.2.0".</summary>
    public string WhatsNewTitle => Loc.F("Update.WhatsNewTitle", Updater.Current.ToString(3));

    /// <summary>After an update: deletes the old files and shows the new version's notes once. Then asks GitHub for a newer release.</summary>
    private async Task StartUpdatesAsync()
    {
        try
        {
            if (Updater.CanInstall)
            {
                Updater.CleanUp(AppContext.BaseDirectory);
            }

            if (Updater.TakeWhatsNew(Services.DataDirectory, Updater.Current) is { } notes)
            {
                WhatsNew = ReleaseNotes(notes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Services.Logger.LogWarning("Update clean-up: {Message}", ex.Message);
        }

        if (UpdateCheck is null)
        {
            return;
        }

        OnPropertyChanged(nameof(CanCheckUpdates));
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        while (true)
        {
            await CheckForUpdateAsync().ConfigureAwait(true);
            await Task.Delay(RecheckEvery).ConfigureAwait(true);
        }
    }

    /// <summary>Asks GitHub for a newer release (the header button shows the answer).</summary>
    private async Task CheckForUpdateAsync()
    {
        if (UpdateCheck is not { } check || IsCheckingUpdate)
        {
            return;
        }

        IsCheckingUpdate = true;
        try
        {
            Update = await check(CancellationToken.None).ConfigureAwait(true);
            UpdateCheckFailed = false;
            if (Update is { } found)
            {
                Services.Logger.LogInformation("Update available: {Version} ({Page}).", found.Version, found.PageUrl);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            UpdateCheckFailed = true;
            Services.Logger.LogInformation("Update check skipped: {Message}", ex.Message);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private bool CanOpenUpdate() => !IsCheckingUpdate;

    /// <summary>The header button: the update card when a newer release is out, else a new check.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenUpdate))]
    private async Task OpenUpdateAsync()
    {
        if (Update is null)
        {
            await CheckForUpdateAsync().ConfigureAwait(true);
        }

        IsUpdateOpen = Update is not null;
    }

    [RelayCommand]
    private void CloseUpdate()
    {
        if (!IsUpdating)
        {
            IsUpdateOpen = false;
        }
    }

    [RelayCommand]
    private void CloseWhatsNew() => WhatsNew = null;

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (Update is { } u && u.PageUrl.Length > 0)
        {
            try
            {
                Process.Start(new ProcessStartInfo(u.PageUrl) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Services.Notifications.Warning(Loc.T("Update.Title.Short"), ex.Message);
            }
        }
    }

    private bool CanInstallUpdate() => !IsUpdating;

    /// <summary>Downloads the release, puts it in place of this one and restarts into it.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (Update is not { } release)
        {
            return;
        }

        if (!Updater.CanInstall || release.ZipUrl is null || Environment.ProcessPath is not { } exe)
        {
            UpdateStatus = Loc.T("Update.DevBuild");
            OpenReleasePage();
            return;
        }

        IsUpdating = true;
        UpdateProgress = 0;
        UpdateStatus = Loc.F("Update.Downloading", 0);
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p * 100;
                UpdateStatus = Loc.F("Update.Downloading", (int)(p * 100));
            });
            var zip = await Updater.DownloadAsync(release, Path.Combine(Services.DataDirectory, "updates"), progress, CancellationToken.None).ConfigureAwait(true);
            UpdateStatus = Loc.T("Update.Installing");
            Updater.SaveWhatsNew(Services.DataDirectory, release);
            await Task.Run(() => Updater.Install(zip, AppContext.BaseDirectory)).ConfigureAwait(true);
            TryDelete(zip);
            Services.Logger.LogInformation("Updated to {Version}; restarting.", release.Version);
            UpdateStatus = Loc.T("Update.Restarting");
            Updater.Start(exe);
            RequestShutdown?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            Services.Logger.LogWarning("Update failed: {Message}", ex.Message);
            UpdateStatus = Loc.F("Update.Failed", ex.Message);
            IsUpdating = false;
        }
    }

    /// <summary>Release notes (GitHub Markdown) as lines: <c>#</c> headings, <c>-</c>/<c>*</c> bullets, links and emphasis reduced to their text.</summary>
    public static IReadOnlyList<NoteLine> ReleaseNotes(string markdown)
    {
        var lines = new List<NoteLine>();
        foreach (var raw in (markdown ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var heading = line.StartsWith('#');
            var bullet = !heading && (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal));
            var text = heading ? line.TrimStart('#').Trim() : bullet ? line[2..].Trim() : line;
            text = Regex.Replace(text, @"!?\[([^\]]*)\]\([^)]*\)", "$1"); // links and images → their text
            text = text.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
            if (text.Length > 0)
            {
                lines.Add(new NoteLine(text, heading, bullet));
            }
        }

        return lines;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover download is harmless
        }
    }
}
