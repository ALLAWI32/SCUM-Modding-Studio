namespace ScumStudio.App.ViewModels;

public abstract partial class ModulePageViewModel
{
    private ModuleExportViewModel? _pageExport;

    /// <summary>"Export this page": the Vehicles, Weapons or Items page's changes as a mod of their own, its unsaved values and paint saved first (review 3.5.0).</summary>
    public ModuleExportViewModel? PageExport =>
        _pageExport ??= ModuleExportViewModel.ModuleOf(Key) is { } module ? new ModuleExportViewModel(_services, module, SavePending) : null;

    private void SavePending()
    {
        if (PendingCount > 0)
        {
            ApplyChanges();
        }

        if (PaintPendingCount > 0)
        {
            ApplyPaint();
        }
    }
}
