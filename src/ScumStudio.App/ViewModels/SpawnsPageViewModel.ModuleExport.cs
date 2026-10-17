using ScumStudio.Level.Export;

namespace ScumStudio.App.ViewModels;

public sealed partial class SpawnsPageViewModel
{
    private ModuleExportViewModel? _pageExport;

    /// <summary>"Export this page": the Spawns page's changes as a mod of their own, its unsaved ones saved first (review 3.5.0).</summary>
    public ModuleExportViewModel PageExport => _pageExport ??= new ModuleExportViewModel(_services, ExportModule.Spawns, () =>
    {
        if (HasPending)
        {
            Apply();
        }
    });
}
