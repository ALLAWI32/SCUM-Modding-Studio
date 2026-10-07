using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;

namespace ScumStudio.App.ViewModels;

/// <summary>A kind of report in the picker, named in the current language.</summary>
public sealed record ReportCategoryOption(ReportCategory Category, string Key)
{
    /// <summary>Its name.</summary>
    public string Label => Loc.T(Key);

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// The Support card (owner): a way to support the developer (PayPal, e-mail) and "Report a problem", a form that sends
/// what the user writes, in any language, to the owner's Discord in English (<see cref="ReportService"/>).
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Where support goes.</summary>
    public const string PayPalUrl = "https://www.paypal.me/ALLAWI34SGWTG121";

    /// <summary>The community server (a permanent invite): help, mods, bug reports, releases.</summary>
    public const string DiscordUrl = "https://discord.gg/ympctW5SYG";

    /// <summary>Below this window width the title strip shows the tabs as icons only (their names stay in the tooltips).</summary>
    public const double CompactHeaderWidth = 1420;

    /// <summary>True in a narrow window: the tab labels and the "Project" caption hide so the search field and the buttons never overlap.</summary>
    [ObservableProperty]
    private bool _isCompactHeader;

    /// <summary>The owner's e-mail.</summary>
    public const string SupportEmail = "ejfjh81@gmail.com";

    private static readonly (ReportCategory Category, string Key)[] Categories =
    [
        (ReportCategory.Bug, "Support.Report.Bug"),
        (ReportCategory.Idea, "Support.Report.Idea"),
    ];

    /// <summary>The Support card is open.</summary>
    [ObservableProperty]
    private bool _isSupportOpen;

    /// <summary>The kinds of report.</summary>
    [ObservableProperty]
    private IReadOnlyList<ReportCategoryOption> _reportCategories = Categories.Select(c => new ReportCategoryOption(c.Category, c.Key)).ToList();

    /// <summary>The chosen kind.</summary>
    [ObservableProperty]
    private ReportCategoryOption? _selectedReportCategory;

    /// <summary>What the user wrote.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendReportCommand))]
    [NotifyPropertyChangedFor(nameof(ReportLengthText))]
    private string _reportText = string.Empty;

    /// <summary>Send the app's version, language, system and last log lines along (they help with every bug).</summary>
    [ObservableProperty]
    private bool _reportIncludeDetails = true;

    /// <summary>Translating and posting right now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendReportCommand))]
    private bool _isSendingReport;

    /// <summary>What happened to the last report.</summary>
    [ObservableProperty]
    private string _reportStatus = string.Empty;

    /// <summary>True when this build can send reports (it carries the webhook).</summary>
    public bool CanReport => Services.Reports.IsAvailable;

    /// <summary>"120 / 1500".</summary>
    public string ReportLengthText => $"{ReportText.Length} / {ReportService.MaxLength}";

    [RelayCommand]
    private void OpenSupport()
    {
        SelectedReportCategory ??= ReportCategories[0];
        ReportStatus = string.Empty;
        IsSupportOpen = true;
    }

    [RelayCommand]
    private void CloseSupport() => IsSupportOpen = false;

    [RelayCommand]
    private void OpenPayPal() => OpenUrl(PayPalUrl);

    [RelayCommand]
    private void OpenDiscord() => OpenUrl(DiscordUrl);

    [RelayCommand]
    private void OpenIssues() => OpenUrl(ReportService.IssuesUrl);

    [RelayCommand]
    private async Task CopyEmailAsync()
    {
        await Services.Dialogs.SetClipboardTextAsync(SupportEmail).ConfigureAwait(true);
        Services.Notifications.Info(Loc.T("Support.Email.Copied"), SupportEmail);
    }

    private bool CanSendReport() => CanReport && !IsSendingReport && ReportText.Trim().Length >= 10;

    /// <summary>Sends the report: translated to English, with the details when asked, to the owner's Discord.</summary>
    [RelayCommand(CanExecute = nameof(CanSendReport))]
    private async Task SendReportAsync()
    {
        IsSendingReport = true;
        ReportStatus = Loc.T("Support.Report.Sending");
        try
        {
            var details = ReportIncludeDetails ? ReportDetails() : [];
            var result = await Services.Reports.SendAsync(SelectedReportCategory?.Category ?? ReportCategory.Bug, ReportText, details).ConfigureAwait(true);
            if (result.Sent)
            {
                ReportText = string.Empty;
                ReportStatus = Loc.T(result.Translated ? "Support.Report.Sent" : "Support.Report.SentAsWritten");
                Services.Notifications.Info(Loc.T("Support.Report.Title"), ReportStatus);
            }
            else
            {
                ReportStatus = Loc.F("Support.Report.Failed", result.Error ?? string.Empty);
            }
        }
        finally
        {
            IsSendingReport = false;
        }
    }

    /// <summary>Version, language, system and the last log lines: what every bug report needs.</summary>
    private List<(string Name, string Value)> ReportDetails()
    {
        var log = Services.Log.Entries.TakeLast(12).Select(e => $"{e.Time} {e.Level} {e.Category}: {e.Message}".Trim());
        var tail = string.Join('\n', log);
        return
        [
            ("Version", ShortVersion),
            ("Language", Loc.Instance.Language),
            ("System", RuntimeInformation.OSDescription),
            ("Log", tail.Length > 0 ? "```\n" + (tail.Length > 900 ? tail[^900..] : tail) + "\n```" : string.Empty),
        ];
    }

    /// <summary>The kinds of report named in the new language.</summary>
    private void RefreshSupportTexts()
    {
        var chosen = SelectedReportCategory?.Category;
        ReportCategories = Categories.Select(c => new ReportCategoryOption(c.Category, c.Key)).ToList();
        SelectedReportCategory = ReportCategories.FirstOrDefault(c => c.Category == chosen) ?? ReportCategories[0];
        OnPropertyChanged(nameof(ReportLengthText));
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Services.Notifications.Warning(Loc.T("Support.Title"), ex.Message);
        }
    }
}
