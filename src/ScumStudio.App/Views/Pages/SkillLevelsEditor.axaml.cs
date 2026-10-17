using Avalonia.Controls;

namespace ScumStudio.App.Views.Pages;

/// <summary>An ingredient's five amounts, one per skill level (see <see cref="ViewModels.SkillLevelsViewModel"/>).</summary>
public partial class SkillLevelsEditor : UserControl
{
    /// <summary>Creates the view.</summary>
    public SkillLevelsEditor() => InitializeComponent();
}
