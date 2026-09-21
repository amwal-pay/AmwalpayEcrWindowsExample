using System.Windows;

namespace AmwalECR.Example.Ui.Behaviors;

/// <summary>
/// Marks a field as invalid so the shared control templates can draw the error
/// outline. The message itself sits under the field and is what a screen reader
/// is pointed at through <c>AutomationProperties.HelpText</c>.
/// </summary>
public static class FieldState
{
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
        "HasError",
        typeof(bool),
        typeof(FieldState),
        new FrameworkPropertyMetadata(false));

    public static bool GetHasError(DependencyObject element) => (bool)element.GetValue(HasErrorProperty);

    public static void SetHasError(DependencyObject element, bool value) => element.SetValue(HasErrorProperty, value);
}
