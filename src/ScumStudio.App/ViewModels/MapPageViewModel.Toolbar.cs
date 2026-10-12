namespace ScumStudio.App.ViewModels;

public sealed partial class MapPageViewModel
{
    private MapToolbar? _toolbar;

    /// <summary>Which header items show (the settings button at the end of the header; the Settings page has the same switches).</summary>
    public MapToolbar Toolbar => _toolbar ??= new MapToolbar(_services.UiState);
}
