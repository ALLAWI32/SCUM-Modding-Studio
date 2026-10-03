using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ScumStudio.App.Localization;

/// <summary>
/// Gives each text block the reading direction of its own text (the Unicode "first strong character" rule), so an
/// Arabic sentence that contains Latin words (AES, Paks, Ctrl+Z ...) reads in the right order. Avalonia only takes the
/// paragraph direction from <see cref="Visual.FlowDirection"/> and the layout stays left-to-right, so only text blocks
/// whose text starts with a right-to-left letter switch, and they stay left-aligned like the rest of the layout.
/// </summary>
internal static class TextDirection
{
    private static bool _registered;

    /// <summary>Watches every <see cref="TextBlock"/> (labels, button and menu texts, tooltips, watermarks). Call once.</summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => Apply(block));
    }

    /// <summary>True when the first letter of <paramref name="text"/> is Hebrew or Arabic.</summary>
    public static bool StartsRightToLeft(string? text)
    {
        foreach (var c in text ?? string.Empty)
        {
            if (char.IsLetter(c))
            {
                return c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';
            }
        }

        return false;
    }

    private static void Apply(TextBlock block)
    {
        if (StartsRightToLeft(block.Text))
        {
            block.FlowDirection = FlowDirection.RightToLeft;
            if (block.TextAlignment == TextAlignment.Start)
            {
                block.TextAlignment = TextAlignment.Left;
            }
        }
        else if (block.FlowDirection == FlowDirection.RightToLeft)
        {
            block.ClearValue(Visual.FlowDirectionProperty); // Left alignment equals Start again in left-to-right text
        }
    }
}
