using System.Windows;
using System.Windows.Input;
using AmwalECR.Example.ViewModels;

namespace AmwalECR.Example.Views;

public partial class ResultDialogWindow : Window
{
    public ResultDialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Escape closes, as Enter does through the default button. Handled here rather
    /// than with IsCancel, which would also try to close a window the command is
    /// already closing.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ResultDialogViewModel vm)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }
}
