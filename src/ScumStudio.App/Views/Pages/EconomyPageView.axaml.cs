using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views.Pages;

/// <summary>The Economy page (see <see cref="ViewModels.EconomyPageViewModel"/>).</summary>
public partial class EconomyPageView : UserControl
{
    /// <summary>Creates the view.</summary>
    public EconomyPageView()
    {
        InitializeComponent();

        // Ctrl+Z / Ctrl+Y in the code box are its own text undo and redo. Key bindings are matched from the focused control
        // up before the key reaches it, so without these the main window's Ctrl+Z undid the project's last step under the
        // text being typed (found headless).
        var undo = new RelayCommand(() => CodeBox.Undo());
        var redo = new RelayCommand(() => CodeBox.Redo());
        CodeBox.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Control), Command = undo });
        CodeBox.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Y, KeyModifiers.Control), Command = redo });
        CodeBox.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift), Command = redo });
    }

    /// <summary>Enter in a value box commits it as leaving the box does (the boxes are read whole, not per keystroke).</summary>
    private void OnValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is TextBox box)
        {
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    /// <summary>Leaving the code box applies a valid text at once instead of after the pause.</summary>
    private void OnCodeLostFocus(object? sender, RoutedEventArgs e) => (DataContext as EconomyPageViewModel)?.ApplyCodeNow();

    /// <summary>A problem clicked: the caret goes to its line and column in the code box.</summary>
    private void OnProblemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: EconomyProblemRow problem } list || CodeBox.Text is not { } text)
        {
            return;
        }

        var index = EconomyProblemRow.IndexOf(text, problem.Line, problem.Column);
        CodeBox.Focus();
        CodeBox.SelectionStart = index;
        CodeBox.SelectionEnd = index;
        CodeBox.CaretIndex = index;
        list.SelectedItem = null;
    }
}
