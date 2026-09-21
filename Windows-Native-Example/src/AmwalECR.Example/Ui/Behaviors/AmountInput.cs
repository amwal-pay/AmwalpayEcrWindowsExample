using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AmwalECR.Example.Core.Ui;

namespace AmwalECR.Example.Ui.Behaviors;

/// <summary>
/// POS-style amount entry on a plain <see cref="TextBox"/> — the port of the
/// Android <c>AmountField</c>. Digits shift in from the right against three fixed
/// decimals (1 gives 0.001, 1234 gives 1.234), backspace shifts them out, and the
/// box reads 0.000 when empty. No decimal point is typed.
/// <para>
/// The attached <see cref="DigitsProperty"/> is the source of truth: the raw keyed
/// digits. Everything shown is derived from them, all typing is handled here rather
/// than by the TextBox, and the caret is pinned to the end, so a click in the middle
/// of the number cannot make the next digit land there.
/// </para>
/// </summary>
public static class AmountInput
{
    public static readonly DependencyProperty DigitsProperty = DependencyProperty.RegisterAttached(
        "Digits",
        typeof(string),
        typeof(AmountInput),
        new FrameworkPropertyMetadata(
            "",
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnDigitsChanged));

    public static string GetDigits(DependencyObject element) => (string)element.GetValue(DigitsProperty);

    public static void SetDigits(DependencyObject element, string value) => element.SetValue(DigitsProperty, value);

    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active",
        typeof(bool),
        typeof(AmountInput),
        new PropertyMetadata(false, OnActiveChanged));

    public static bool GetActive(DependencyObject element) => (bool)element.GetValue(ActiveProperty);

    public static void SetActive(DependencyObject element, bool value) => element.SetValue(ActiveProperty, value);

    private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;

        if (e.NewValue is true)
        {
            box.PreviewTextInput += OnPreviewTextInput;
            box.PreviewKeyDown += OnPreviewKeyDown;
            box.GotKeyboardFocus += OnGotKeyboardFocus;
            box.SelectionChanged += OnSelectionChanged;
            box.PreviewMouseUp += OnPreviewMouseUp;
            DataObject.AddPastingHandler(box, OnPasting);
            CommandManager.AddPreviewCanExecuteHandler(box, OnPreviewCanExecute);
            // An input method would compose text the handlers below never see.
            InputMethod.SetIsInputMethodEnabled(box, false);
            box.IsUndoEnabled = false;
            box.AllowDrop = false;
            Render(box);
        }
        else
        {
            box.PreviewTextInput -= OnPreviewTextInput;
            box.PreviewKeyDown -= OnPreviewKeyDown;
            box.GotKeyboardFocus -= OnGotKeyboardFocus;
            box.SelectionChanged -= OnSelectionChanged;
            box.PreviewMouseUp -= OnPreviewMouseUp;
            DataObject.RemovePastingHandler(box, OnPasting);
            CommandManager.RemovePreviewCanExecuteHandler(box, OnPreviewCanExecute);
        }
    }

    private static void OnDigitsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox box && GetActive(box)) Render(box);
    }

    private static void Render(TextBox box)
    {
        var text = AmountEntry.Format(GetDigits(box) ?? "");
        if (box.Text != text) box.Text = text;
        PinCaret(box);
    }

    private static void PinCaret(TextBox box)
    {
        if (box.SelectionLength != 0 || box.CaretIndex != box.Text.Length)
        {
            box.Select(box.Text.Length, 0);
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        e.Handled = true;
        foreach (var c in e.Text)
        {
            if (c >= '0' && c <= '9') SetDigits(box, AmountEntry.Append(GetDigits(box), c));
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        switch (e.Key)
        {
            case Key.Back:
                SetDigits(box, AmountEntry.Backspace(GetDigits(box)));
                e.Handled = true;
                break;
            // Delete and Space would edit the text in place; there is nothing to
            // delete to the right of a caret that is always at the end.
            case Key.Delete:
            case Key.Space:
                e.Handled = true;
                break;
            case Key.Escape when GetDigits(box).Length != 0:
                SetDigits(box, "");
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Focusing the field starts a fresh amount: there is no cursor to place, so a
    /// half-typed figure would only be confusing. Only a move from another control
    /// counts — the window being re-activated with focus already here does not.
    /// </summary>
    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.OldFocus is UIElement old && !ReferenceEquals(old, box) && GetDigits(box).Length != 0)
        {
            SetDigits(box, "");
        }
        PinCaret(box);
    }

    private static void OnSelectionChanged(object sender, RoutedEventArgs e) => PinCaret((TextBox)sender);

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e) => PinCaret((TextBox)sender);

    /// <summary>A paste keys its digits in, as if typed.</summary>
    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var box = (TextBox)sender;
        e.CancelCommand();
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText) && e.DataObject.GetData(DataFormats.UnicodeText) is string text)
        {
            foreach (var c in text)
            {
                if (c >= '0' && c <= '9') SetDigits(box, AmountEntry.Append(GetDigits(box), c));
            }
        }
    }

    /// <summary>Cut would remove text the box derives, so it is switched off.</summary>
    private static void OnPreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Cut)
        {
            e.CanExecute = false;
            e.Handled = true;
        }
    }
}
