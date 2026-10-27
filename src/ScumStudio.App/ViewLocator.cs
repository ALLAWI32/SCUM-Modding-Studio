using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views;
using ScumStudio.App.Views.Pages;

namespace ScumStudio.App;

/// <summary>
/// Maps page and dialog view models to their views (explicit table, no reflection, so it stays trim-safe and the
/// mapping is visible in one place).
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Views = new()
    {
        [typeof(HomePageViewModel)] = () => new HomePageView(),
        [typeof(MapPageViewModel)] = () => new MapPageView(),
        [typeof(VehiclesPageViewModel)] = () => new ModulePageView(),
        [typeof(WeaponsPageViewModel)] = () => new ModulePageView(),
        [typeof(ItemsPageViewModel)] = () => new ModulePageView(),
        [typeof(SpawnsPageViewModel)] = () => new SpawnsPageView(),
        [typeof(EconomyPageViewModel)] = () => new EconomyPageView(),
        [typeof(CraftablesPageViewModel)] = () => new CraftablesPageView(),
        [typeof(AssetsPageViewModel)] = () => new AssetsPageView(),
        [typeof(ServerPageViewModel)] = () => new ServerPageView(),
        [typeof(ProjectsPageViewModel)] = () => new ProjectsPageView(),
        [typeof(SettingsPageViewModel)] = () => new SettingsPageView(),
        [typeof(SharePageViewModel)] = () => new SharePageView(),
        [typeof(SetupViewModel)] = () => new SetupView(),
        [typeof(WelcomeViewModel)] = () => new WelcomeView(),
    };

    /// <summary>Returns the view type for a view model type, or null.</summary>
    public static Type? ViewTypeFor(Type viewModelType) =>
        Views.TryGetValue(viewModelType, out var factory) ? factory().GetType() : null;

    /// <inheritdoc />
    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        if (!Views.TryGetValue(param.GetType(), out var factory))
        {
            return new TextBlock { Text = "No view registered for " + param.GetType().Name };
        }

        return factory();
    }

    /// <inheritdoc />
    public bool Match(object? data) => data is PageViewModel or SetupViewModel or WelcomeViewModel;
}
