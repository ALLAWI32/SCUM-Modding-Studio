using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScumStudio.App.ViewModels;

/// <summary>The Share button of the title strip and its card ("Share your mod").</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Window width from which the Share button shows its name next to the tab names (measured in every language with the
    /// Update button showing and ten tabs: German needs 1865 px, Russian 1855); narrower, it is an icon with its tooltip.
    /// The same as <see cref="CompactHeaderWidth"/>: the tab names show only where the Share label fits too.
    /// </summary>
    public const double ShareLabelWidth = CompactHeaderWidth;

    /// <summary>True for a window width with room for the Share button's name.</summary>
    public static IValueConverter ShareLabelFits { get; } = new FuncValueConverter<double, bool>(width => width >= ShareLabelWidth);

    /// <summary>The Share card; made on first open, so nothing asks the gallery at startup.</summary>
    [ObservableProperty]
    private SharePageViewModel? _share;

    [RelayCommand]
    private void OpenShare()
    {
        Share ??= Track(new SharePageViewModel(Services, NavigateTo));
        Share.Open();
    }
}
