namespace ScumStudio.App.Services;

/// <summary>File/folder pickers and clipboard, abstracted so view models stay testable.</summary>
public interface IDialogService
{
    /// <summary>Lets the user pick a folder; returns its local path or null when cancelled/unavailable.</summary>
    Task<string?> PickFolderAsync(string title, string? startFolder = null);

    /// <summary>Asks for a file to save; returns its local path or null when cancelled.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="suggestedName">Suggested file name including extension.</param>
    /// <param name="extension">Extension without dot, e.g. <c>png</c>.</param>
    /// <param name="filterName">File type name shown in the dialog, e.g. "PNG image".</param>
    Task<string?> SaveFileAsync(string title, string suggestedName, string extension, string filterName);

    /// <summary>Asks for a file to open; returns its local path or null when cancelled.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="extension">Extension without dot, e.g. <c>pak</c>.</param>
    /// <param name="filterName">File type name shown in the dialog.</param>
    Task<string?> OpenFileAsync(string title, string extension, string filterName);

    /// <summary>Copies text to the clipboard (no-op when unavailable).</summary>
    Task SetClipboardTextAsync(string text);
}

/// <summary>A dialog service that never shows anything (headless runs before the window exists).</summary>
public sealed class NullDialogService : IDialogService
{
    /// <inheritdoc />
    public Task<string?> PickFolderAsync(string title, string? startFolder = null) => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<string?> SaveFileAsync(string title, string suggestedName, string extension, string filterName) => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<string?> OpenFileAsync(string title, string extension, string filterName) => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task SetClipboardTextAsync(string text) => Task.CompletedTask;
}
