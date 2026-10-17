using CommunityToolkit.Mvvm.Input;
using ScumStudio.Level.Export;

namespace ScumStudio.App.ViewModels;

public sealed partial class CraftablesPageViewModel
{
    private ModuleExportViewModel? _pageExport;

    /// <summary>Step 6, Export: the craftables as a mod of their own (client and server paks with .sig, and the Registry pak).</summary>
    public ModuleExportViewModel PageExport => _pageExport ??= new ModuleExportViewModel(_services, ExportModule.Craftables);

    /// <summary>Step 1: opens the gallery of the game's objects and items in 3D.</summary>
    [RelayCommand]
    private void OpenGallery() => IsGalleryOpen = true;
}
