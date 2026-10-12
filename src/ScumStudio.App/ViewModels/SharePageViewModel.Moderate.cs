using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Core.Sharing;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The Moderate tab: shown only when the owner's key file is on this PC (<see cref="ModeratorKey"/>). A removal is signed
/// with that key; the app carries no secret that could remove anything (audit F029/N045 is the lesson).
/// </summary>
public sealed partial class SharePageViewModel
{
    /// <summary>What happened to the last removal.</summary>
    [ObservableProperty]
    private string _moderateStatus = string.Empty;

    /// <summary>Asks "Remove … for everyone?" on that card (one question at a time).</summary>
    [RelayCommand]
    private void AskRemoveListing(ShareCardItem? card)
    {
        foreach (var other in Gallery)
        {
            other.IsConfirming = ReferenceEquals(other, card);
        }
    }

    [RelayCommand]
    private void KeepListing(ShareCardItem? card)
    {
        if (card is not null)
        {
            card.IsConfirming = false;
        }
    }

    /// <summary>Removes the mod from the gallery for everyone (DELETE signed with the key file).</summary>
    [RelayCommand]
    private async Task RemoveListingAsync(ShareCardItem? card)
    {
        if (card is null || IsBusy)
        {
            return;
        }

        using var key = ModeratorKey.TryLoad();
        if (key is null)
        {
            IsModerator = false; // the file is gone: the tab goes too
            Tabs = BuildTabs(moderator: false);
            SelectedTab = Tabs[0];
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _client.RemoveAsync(card.Id, key, _life.Token).ConfigureAwait(true);
            card.IsConfirming = false;
            if (!result.Ok && result.Error != ModShareError.NotFound)
            {
                ModerateStatus = ErrorText(result.Error);
                return;
            }

            _services.Logger.LogInformation("Share: {Name} ({Id}) removed from the gallery.", card.Name, card.Id);
            Gallery.Remove(card);
            ModerateStatus = Loc.F("Share.Moderate.Removed", card.Name);
            _services.Notifications.Success(ModerateStatus);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }
}
