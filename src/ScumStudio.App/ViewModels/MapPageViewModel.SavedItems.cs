using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.Level.Saves;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Clean saved items (D02, "phantom fridges"): the panel that removes from a SCUM.db what the project's removed or reverted
/// item spawners already spawned. The export report, the item spawner warnings and the Delete list point here.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>The Clean saved items panel while it is open; else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedItems))]
    private SavedItemsViewModel? _savedItems;

    /// <summary>True while the Clean saved items panel is open.</summary>
    public bool HasSavedItems => SavedItems is not null;

    /// <summary>The project's item spawners that are gone (every one its history placed and it no longer has, and the game's it deleted).</summary>
    internal IReadOnlyList<SpawnerTarget> SavedItemTargets() => _services.Projects.Current is { } project
        ? SavedItemCleaner.TargetsOf(project.History.Select(h => h.Op), project.State, a => PristineOf(a)?.Actor)
        : [];

    /// <summary>Opens Clean saved items (the toolbar button, and "Clean saved items" in the Delete list).</summary>
    [RelayCommand]
    private void OpenSavedItems()
    {
        CloseCompanions();
        SavedItems ??= new SavedItemsViewModel(_services.Dialogs, SavedItemTargets, SavedItemsAreaCenter) { OnClose = () => SavedItems = null };
    }

    /// <summary>The area's centre for Clean saved items: the selected object, or null.</summary>
    private SavedItemsCenter? SavedItemsAreaCenter() => SelectedActor is { } item
        ? new SavedItemsCenter(item.Actor.WorldTransform.Translation.X, item.Actor.WorldTransform.Translation.Y, item.Name)
        : null;
}
