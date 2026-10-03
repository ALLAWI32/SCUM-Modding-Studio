using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ScumStudio.App.Services;

/// <summary><see cref="IDialogService"/> over the storage provider and clipboard of a window.</summary>
public sealed class AvaloniaDialogService : IDialogService
{
    private readonly Func<TopLevel?> _topLevel;

    /// <summary>Creates the service for the top level returned by <paramref name="topLevel"/> (evaluated per call).</summary>
    public AvaloniaDialogService(Func<TopLevel?> topLevel) => _topLevel = topLevel;

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(string title, string? startFolder = null)
    {
        if (_topLevel()?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(startFolder) && Directory.Exists(startFolder))
        {
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startFolder).ConfigureAwait(true);
        }

        var result = await storage.OpenFolderPickerAsync(options).ConfigureAwait(true);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension, string filterName)
    {
        if (_topLevel()?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = ["*." + extension] }],
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<string?> OpenFileAsync(string title, string extension, string filterName)
    {
        if (_topLevel()?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = ["*." + extension] }],
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async Task SetClipboardTextAsync(string text)
    {
        if (_topLevel()?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }
}
