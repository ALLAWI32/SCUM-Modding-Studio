using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// A page's "Export this page" button (owner 2026-10-17: "each part as its own mod"): only that page's changes, as a pak of
/// their own for the client and the server, each with a .sig, plus the Registry pak that keeps every page pak's new assets
/// known to the game (<see cref="ModuleExport"/>). Export mod on the Map page still builds the whole project.
/// </summary>
public sealed partial class ModuleExportViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action? _before;

    /// <summary>Creates the button's model for <paramref name="module"/>; <paramref name="before"/> runs first (a page commits what it shows).</summary>
    public ModuleExportViewModel(AppServices services, ExportModule module, Action? before = null)
    {
        _services = services;
        Module = module;
        _before = before;
    }

    /// <summary>The page.</summary>
    public ExportModule Module { get; }

    /// <summary>What the last export wrote and how to install it (shown under the button), or null.</summary>
    [ObservableProperty]
    private string? _lastResult;

    /// <summary>The page's name for a module key (<c>vehicles</c>, <c>map</c> …), or null when that page has no mod of its own.</summary>
    public static ExportModule? ModuleOf(string pageKey) =>
        Enum.TryParse<ExportModule>(pageKey, ignoreCase: true, out var module) ? module : null;

    /// <summary>Builds the page's mod into the export folder (and the server's when set up).</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        _before?.Invoke();
        var project = _services.Projects.Current;
        if (ModExportService.ModuleBlocker(_services, project, Module) is { } blocker)
        {
            _services.Notifications.Warning(Loc.T("Map.CannotExportYet"), blocker);
            return;
        }

        var label = ModExportService.ModuleLabel(Module);
        var (ok, export) = await _services.Operations.RunAsync(Loc.F("Export.Module.Running", label),
            (progress, ct) => ModExportService.QuickExportModuleAsync(_services, project!, Module, ProjectsPageViewModel.DefaultExportFolder(), progress, ct)).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        var (results, installed) = export;
        LastResult = Describe(results, installed);
        if (results.Count > 0)
        {
            _services.Notifications.Success(Loc.F("Export.Module.Done", label), LastResult);
        }
    }

    /// <summary>The paks written, where the server copies went, and the install rule, in the UI language.</summary>
    public static string Describe(IReadOnlyList<ExportResult> results, IReadOnlyList<string> installed)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(installed);
        if (results.Count == 0)
        {
            return Loc.T("Export.Module.EconomyWritten");
        }

        var client = results.Where(r => r.Role == ProjectSourceRole.Client && r.PakPath is not null).ToList();
        var folder = client.Select(r => Path.GetDirectoryName(r.PakPath)).FirstOrDefault() ?? string.Empty;
        var text = Loc.F("Export.Module.Written", string.Join(" + ", client.Select(r => Path.GetFileName(r.PakPath))), folder);
        if (!results.Any(r => r.Role == ProjectSourceRole.Server))
        {
            text += " " + Loc.T("Export.Module.NoServer");
        }
        else if (installed.Count > 0)
        {
            text += " " + Loc.F("Map.ServerPakCopied", Path.GetDirectoryName(installed[0])).Trim();
        }

        var needs = results.SelectMany(r => r.Validation?.Needs ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (needs.Count > 0)
        {
            text += " " + Loc.F("Export.Module.Needs", string.Join(", ", needs));
        }

        return string.Create(CultureInfo.CurrentCulture, $"{text} {Loc.T("Export.Module.Install")}");
    }
}
