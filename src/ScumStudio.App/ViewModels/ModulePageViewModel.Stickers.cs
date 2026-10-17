namespace ScumStudio.App.ViewModels;

/// <content>
/// Owner (2026-10-17): "adding a sticker must work reliably". A picture or drawing added with "Add sticker…" is copied into
/// the project (<c>decals/stickers</c>) and listed under the Stickers chip like a library design: click it and click the
/// car, or drag it onto the car; it stays there for the next decal. A file the studio cannot read says so.
/// </content>
public abstract partial class ModulePageViewModel
{
    private IReadOnlyList<DecalDesignItem> _stickerItems = [];
    private string? _stickersOf;

    /// <summary>The project's stickers (newest last), read from its <c>decals/stickers</c> folder.</summary>
    public IReadOnlyList<DecalDesignItem> StickerItems
    {
        get
        {
            if (_services.Projects.Current?.DirectoryPath != _stickersOf)
            {
                RefreshStickerItems();
            }

            return _stickerItems;
        }
    }

    /// <summary>Reads the project's stickers again (after one was added, or another project opened).</summary>
    internal void RefreshStickerItems()
    {
        var project = _services.Projects.Current?.DirectoryPath;
        _stickersOf = project;
        var folder = project is null ? null : Path.Combine(project, "decals", "stickers");
        var files = folder is not null && Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || IsSvg(f))
                .OrderBy(File.GetCreationTimeUtc).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        _stickerItems = [.. files.Select((f, i) =>
        {
            var relative = Path.GetRelativePath(project!, f).Replace('\\', '/');
            return new DecalDesignItem(relative, i + 1, IsSvg(f) ? SvgSticker(relative) : null, IsSvg(f) ? null : f);
        })];
        OnPropertyChanged(nameof(StickerItems));
        OnPropertyChanged(nameof(DecalDesigns));
    }
}
