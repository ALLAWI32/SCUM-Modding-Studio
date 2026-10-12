namespace ScumStudio.App.ViewModels;

public sealed partial class SettingsPageViewModel
{
    private MapToolbar? _toolbar;

    /// <summary>The Map header's switches (Map toolbar card); the Map follows them at once.</summary>
    public MapToolbar Toolbar => _toolbar ??= new MapToolbar(_services.UiState);
}
