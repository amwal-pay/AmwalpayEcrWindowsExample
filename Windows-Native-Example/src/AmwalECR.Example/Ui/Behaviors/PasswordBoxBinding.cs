using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AmwalECR.Example.Ui.Behaviors;

/// <summary>
/// <see cref="PasswordBox.Password"/> is not bindable on purpose; the signing-key
/// fields need it to be, so a "Show" button can swap a masked box for a plain one
/// over the same value. Optionally keeps the value to hex digits.
/// </summary>
public static class PasswordBoxBinding
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword",
        typeof(string),
        typeof(PasswordBoxBinding),
        new FrameworkPropertyMetadata(
            "",
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnBoundPasswordChanged));

    public static string GetBoundPassword(DependencyObject element) => (string)element.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject element, string value) => element.SetValue(BoundPasswordProperty, value);

    public static readonly DependencyProperty FilterProperty = DependencyProperty.RegisterAttached(
        "Filter",
        typeof(TextFilterMode),
        typeof(PasswordBoxBinding),
        new PropertyMetadata(TextFilterMode.None));

    public static TextFilterMode GetFilter(DependencyObject element) => (TextFilterMode)element.GetValue(FilterProperty);

    public static void SetFilter(DependencyObject element, TextFilterMode value) => element.SetValue(FilterProperty, value);

    static PasswordBoxBinding()
    {
        // A class handler, not one attached when the bound value first changes: a key
        // that starts out empty never changes until the operator types, and by then
        // there would be nobody listening.
        EventManager.RegisterClassHandler(
            typeof(PasswordBox),
            PasswordBox.PasswordChangedEvent,
            new RoutedEventHandler(OnPasswordChanged));
    }

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;

        var value = (string?)e.NewValue ?? "";
        if (box.Password != value) box.Password = value;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        // Only boxes that opted in by binding a value or asking for a filter.
        if (BindingOperations.GetBindingExpression(box, BoundPasswordProperty) == null
            && GetFilter(box) == TextFilterMode.None)
        {
            return;
        }
        var filtered = TextFilter.Apply(GetFilter(box), box.Password);
        if (filtered != box.Password)
        {
            // Setting Password raises PasswordChanged again with the clean value.
            box.Password = filtered;
            return;
        }
        SetBoundPassword(box, box.Password);
    }
}
