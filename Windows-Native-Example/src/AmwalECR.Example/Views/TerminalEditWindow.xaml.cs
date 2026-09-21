using System.Windows;
using System.Windows.Input;
using AmwalECR.Example.ViewModels;

namespace AmwalECR.Example.Views;

public partial class TerminalEditWindow : Window
{
    public TerminalEditWindow()
    {
        InitializeComponent();
    }

    /// <summary>Escape cancels, as Enter saves through the default button.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is TerminalEditViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }
}
