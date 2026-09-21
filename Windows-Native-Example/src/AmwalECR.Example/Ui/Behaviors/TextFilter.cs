using System.Windows;
using System.Windows.Controls;

namespace AmwalECR.Example.Ui.Behaviors;

public enum TextFilterMode
{
    None,
    /// <summary>ASCII digits only: receipt numbers, ports, backend IDs.</summary>
    Digits,
    /// <summary>ASCII digits and dots: an IPv4 address while it is typed.</summary>
    DigitsAndDots,
    /// <summary>Hex digits: a signing key.</summary>
    Hex,
}

/// <summary>
/// Keeps a <see cref="TextBox"/> to the characters a field can hold, like the
/// <c>filter { isDigit }</c> on the Android fields. Works on the resulting text,
/// so typing, pasting and drag-and-drop are all covered by one rule, and the caret
/// stays where the operator put it.
/// </summary>
public static class TextFilter
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode",
        typeof(TextFilterMode),
        typeof(TextFilter),
        new PropertyMetadata(TextFilterMode.None, OnModeChanged));

    public static TextFilterMode GetMode(DependencyObject element) => (TextFilterMode)element.GetValue(ModeProperty);

    public static void SetMode(DependencyObject element, TextFilterMode value) => element.SetValue(ModeProperty, value);

    public static bool Allows(TextFilterMode mode, char c) => mode switch
    {
        TextFilterMode.Digits => c >= '0' && c <= '9',
        TextFilterMode.DigitsAndDots => (c >= '0' && c <= '9') || c == '.',
        TextFilterMode.Hex => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'),
        _ => true,
    };

    public static string Apply(TextFilterMode mode, string text)
    {
        if (mode == TextFilterMode.None) return text;
        return new string(text.Where(c => Allows(mode, c)).ToArray());
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.TextChanged -= OnTextChanged;
        if ((TextFilterMode)e.NewValue != TextFilterMode.None) box.TextChanged += OnTextChanged;
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        var mode = GetMode(box);
        var text = box.Text;
        var filtered = Apply(mode, text);
        if (filtered == text) return;

        // Characters removed before the caret pull it back by as many places.
        var caret = box.CaretIndex;
        var removedBeforeCaret = text.Take(Math.Min(caret, text.Length)).Count(c => !Allows(mode, c));
        box.Text = filtered;
        box.CaretIndex = Math.Max(0, Math.Min(filtered.Length, caret - removedBeforeCaret));
    }
}
