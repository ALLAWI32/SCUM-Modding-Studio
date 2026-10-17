using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Textures;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The Picture box's own pictures (Jenni and the owner, 2026-10-16: "a bed must show a bed"): a picture drawn from the
/// craftable's 3D model at export (<see cref="CraftPictures.FromModel"/>), or a PNG or JPG of the user's, copied into the
/// project's <c>icons</c> folder (<see cref="CraftPictures.ProjectFileFor"/>).
/// </summary>
public sealed partial class CraftablesPageViewModel
{
    /// <summary>Asks for a PNG or JPG and makes it <paramref name="row"/>'s picture (a copy in the project folder).</summary>
    [RelayCommand]
    private async Task ImportPictureAsync(CraftableRowViewModel? row)
    {
        if (row is null || _services.Projects.Current is not { } project)
        {
            return;
        }

        var path = await _services.Dialogs.OpenFileAsync(Loc.T("Craftables.Picture.Import.Title"), ImageFiles.Extensions, "PNG / JPG").ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        try
        {
            _ = await Task.Run(() => ImageFiles.Load(path)).ConfigureAwait(true); // a file that is no picture is refused here, not at export
            var file = CraftPictures.ProjectFileFor(row.Token, Path.GetExtension(path));
            var target = Path.GetFullPath(Path.Combine(project.DirectoryPath, file));
            if (!string.Equals(target, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                System.IO.File.Copy(path, target, overwrite: true);
            }

            row.Icon = file;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _services.Notifications.Error(Loc.T("Craftables.Picture"), Loc.F("Craftables.Picture.Unreadable", Path.GetFileName(path)));
            _services.Logger.LogDebug("Craftable picture {Path}: {Message}", path, ex.Message);
        }
    }
}

/// <summary>The row's side of the Picture box: "from its 3D model" and what the chosen picture is.</summary>
public sealed partial class CraftableRowViewModel
{
    /// <summary>The picture is drawn from its 3D model at export, even when the game has one of its own.</summary>
    [RelayCommand]
    private void UseModelPicture() => Icon = CraftPictures.FromModel;

    /// <summary>"Drawn from its 3D model" or "Your picture: icons/x.png" under the box, or null for a game picture or its own.</summary>
    public string? PictureNote =>
        CraftPictures.IsFromModel(Icon) ? Loc.T("Craftables.Picture.FromModel")
        : CraftPictures.IsProjectFile(Icon) ? Loc.F("Craftables.Picture.FromFile", Icon!)
        : null;
}
