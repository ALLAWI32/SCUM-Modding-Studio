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
            DefaultExtension = BareExtension(extension),
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = PickerPatterns(extension) }],
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<string?> OpenFileAsync(string title, string extension, string filterName, string? startFolder = null)
    {
        if (_topLevel()?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        // Start where the file most likely is (the owner hunted through Documents for his exported pak).
        var start = startFolder is { Length: > 0 } && System.IO.Directory.Exists(startFolder)
            ? await storage.TryGetFolderFromPathAsync(startFolder).ConfigureAwait(true)
            : null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = PickerPatterns(extension) }],
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// The picker patterns for <paramref name="extensions"/> ("json", ".json", "*.json" or several joined with ';'):
    /// always "*.ext". A caller that passed ".json" used to get "*..json", which hid EconomyOverride.json (Игорь).
    /// </summary>
    internal static List<string> PickerPatterns(string extensions) =>
        extensions.Split(';').Select(BareExtension).Where(e => e.Length > 0).Select(e => "*." + e).ToList();

    /// <summary>An extension without its leading "*" and "." ("*.json", ".json" and "json" all give "json").</summary>
    internal static string BareExtension(string extension) => extension.Trim().TrimStart('*').TrimStart('.');

    /// <inheritdoc />
    public async Task SetClipboardTextAsync(string text)
    {
        if (_topLevel()?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }
}
