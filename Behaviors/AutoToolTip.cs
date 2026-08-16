using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace ClankerExplorer.Behaviors;

public static class AutoToolTip
{
    public static readonly AttachedProperty<bool> ShowWhenTrimmedProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("ShowWhenTrimmed", typeof(AutoToolTip));

    static AutoToolTip()
    {
        ShowWhenTrimmedProperty.Changed.AddClassHandler<TextBlock>((tb, e) =>
        {
            if (e.NewValue is true)
            {
                tb.PointerEntered += OnPointerEntered;
            }
            else
            {
                tb.PointerEntered -= OnPointerEntered;
            }
        });
    }

    public static bool GetShowWhenTrimmed(TextBlock element) => element.GetValue(ShowWhenTrimmedProperty);
    public static void SetShowWhenTrimmed(TextBlock element, bool value) => element.SetValue(ShowWhenTrimmedProperty, value);

    public static bool IsTextTrimmed(TextBlock tb)
    {
        if (string.IsNullOrEmpty(tb.Text) || tb.Bounds.Width <= 0)
        {
            return false;
        }

        try
        {
            var formatted = new FormattedText(
                tb.Text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight),
                tb.FontSize,
                null);

            return formatted.Width > (tb.Bounds.Width + 0.5);
        }
        catch
        {
            return false;
        }
    }

    private static void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        
        if (IsTextTrimmed(tb))
        {
            ToolTip.SetTip(tb, tb.Text);
        }
        else
        {
            ToolTip.SetTip(tb, null);
        }
    }
}
