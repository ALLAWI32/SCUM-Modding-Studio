using Avalonia.Controls;
using ScumStudio.App.ViewModels;

namespace ScumStudio.App.Views;

/// <summary>The Lua editor (see <see cref="LuaEditorViewModel"/>): a page of the Server page, also shown in its own window.</summary>
public partial class LuaEditorView : UserControl
{
    /// <summary>Creates the view.</summary>
    public LuaEditorView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LuaEditorViewModel vm)
        {
            vm.InsertRequested += (_, text) => InsertAtCaret(text);
        }
    }

    /// <summary>Puts <paramref name="text"/> at the caret of the code box (at the end when the caret is still at the start).</summary>
    private void InsertAtCaret(string text)
    {
        if (DataContext is not LuaEditorViewModel vm)
        {
            return;
        }

        var code = vm.Code ?? string.Empty;
        // A caret that never left the very start means the box was not used yet: the piece goes to the end.
        var at = CodeBox.CaretIndex == 0 ? code.Length : Math.Clamp(CodeBox.CaretIndex, 0, code.Length);
        if (at > 0 && code[at - 1] != '\n')
        {
            text = "\n" + text;
        }

        vm.Code = code.Insert(at, text);
        CodeBox.CaretIndex = at + text.Length;
        CodeBox.Focus();
    }

    private void OnModPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is LuaEditorViewModel vm && ModPicker.SelectedItem is string name)
        {
            vm.OpenCommand.Execute(name);
        }
    }
}
