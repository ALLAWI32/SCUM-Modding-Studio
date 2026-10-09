using CommunityToolkit.Mvvm.ComponentModel;

namespace ScumStudio.App.ViewModels;

/// <summary>The page's "Add from the game" gallery (owner: "the list is empty, why?": nothing showed what can be added).</summary>
public sealed partial class CraftablesPageViewModel
{
    /// <summary>Every object and item of the game in 3D pictures; a click adds one (<see cref="PickAsync"/>).</summary>
    public CraftGalleryViewModel Gallery { get; }

    /// <summary>The right side shows the gallery instead of the selected craftable; always while none is selected.</summary>
    [ObservableProperty]
    private bool _isGalleryOpen = true;

    partial void OnSelectedItemChanged(CraftableRowViewModel? oldValue, CraftableRowViewModel? newValue) => IsGalleryOpen = newValue is null;

    // Nothing selected: the gallery stays, the "Add from the game" tab clicked again would leave an empty panel (after the
    // tab's own write, so it shows checked again).
    partial void OnIsGalleryOpenChanged(bool value)
    {
        if (!value && SelectedItem is null)
        {
            _services.Dispatcher.Post(() => IsGalleryOpen = IsGalleryOpen || SelectedItem is null);
        }
    }

    /// <summary>A click on the craftable already selected: its editor again (the gallery closes; the selection did not change).</summary>
    public void ShowSelected()
    {
        if (SelectedItem is not null)
        {
            IsGalleryOpen = false;
        }
    }

    /// <summary>A gallery tile clicked: the craftable it already is, or a new one through the search box's <see cref="AddAsync"/>.</summary>
    private Task PickAsync(string packagePath)
    {
        if (Items.FirstOrDefault(i => string.Equals(i.Source, packagePath, StringComparison.OrdinalIgnoreCase)) is not { } row)
        {
            return AddAsync(packagePath);
        }

        SelectedItem = row;
        IsGalleryOpen = false;
        return Task.CompletedTask;
    }

    private HashSet<string> AddedSources() => Items.Select(i => i.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
