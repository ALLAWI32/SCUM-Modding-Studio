using Avalonia.Controls;
using Avalonia.Interactivity;
using ScumStudio.App.Localization;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.App.Views;

/// <summary>
/// The window with the step-by-step guide for installing UE4SS on a cloud (rented) server, where the app cannot reach the
/// server's files. The texts are strings of every UI language (<c>Guide.*</c>); the settings block is the same list the installer writes.
/// </summary>
public partial class Ue4ssGuideWindow : Window
{
    /// <summary>The settings lines of step 5, as the installer writes them.</summary>
    public static string SettingsText { get; } = string.Join(Environment.NewLine, Ue4ssInstaller.ServerSettings.Select(p => $"{p.Key} = {p.Value}"));

    /// <summary>Creates the window.</summary>
    public Ue4ssGuideWindow()
    {
        InitializeComponent();
        SettingsBlock.Text = SettingsText;
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(SettingsText).ConfigureAwait(true);
            CopiedText.Text = Loc.T("Guide.Copied");
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
