using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;

namespace ScumStudio.App.Views;

/// <summary>Main editor window.</summary>
public partial class MainWindow : Window
{
    /// <summary>Creates the window.</summary>
    public MainWindow()
    {
        InitializeComponent();

        // Ctrl+K jumps to the command field in the title strip.
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.K, KeyModifiers.Control),
            Command = new RelayCommand(FocusSearch),
        });
#if DEBUG
        this.AttachDevTools(); // F12 opens Avalonia DevTools (Debug builds only).
#endif
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            vm.IsCompactHeader = e.NewSize.Width < ViewModels.MainWindowViewModel.CompactHeaderWidth;
        }
    }

    private void FocusSearch()
    {
        GlobalSearch.Focus(NavigationMethod.Tab);
        GlobalSearch.SelectAll();
    }

    private void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopyLogAsync();
            e.Handled = true;
        }
    }

    private void OnCopyLogClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = CopyLogAsync();

    /// <summary>Copies the picked console lines (every line when none is picked) as text, in the order they are shown.</summary>
    private async Task CopyLogAsync()
    {
        var picked = LogList.SelectedItems?.OfType<ViewModels.LogLineViewModel>().ToHashSet() ?? [];
        var lines = LogList.Items.OfType<ViewModels.LogLineViewModel>().Where(l => picked.Count == 0 || picked.Contains(l));
        var text = string.Join(Environment.NewLine, lines.Select(l => $"{l.Time} {l.Level} {l.Category} {l.Message}"));
        if (Clipboard is { } clipboard && text.Length > 0)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
