using ScumStudio.Level.Export;

namespace ScumStudio.App.ViewModels;

public sealed partial class MapPageViewModel
{
    private ModuleExportViewModel? _mapExport;

    /// <summary>"Map only": the Map page's changes as a mod of their own (<see cref="ModuleExport"/>); what the sliders show is exported.</summary>
    public ModuleExportViewModel MapExport => _mapExport ??= new ModuleExportViewModel(_services, ExportModule.Map, CommitShape);
}
