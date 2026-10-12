using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScumStudio.App.ViewModels;

/// <summary>The Share button of the title strip and its card ("Share your mod").</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Window width from which the Share button shows its name next to the tab names (measured in every language with the
    /// Update button showing: German needs 1750 px); narrower, it is an icon with its tooltip.
    /// </summary>
    public const double ShareLabelWidth = 1760;

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
