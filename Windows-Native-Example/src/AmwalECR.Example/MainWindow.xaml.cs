using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AmwalECR.Example.ViewModels;

namespace AmwalECR.Example;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>Escape goes back from the terminals page, like the Android back gesture.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel is { IsTerminalsPage: true } vm && vm.BackCommand.CanExecute(null))
        {
            vm.BackCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Closing does not stop a transaction the terminal has already started; say so
    /// before the answer that would have said how it ended is thrown away.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (ViewModel?.Transaction.IsBusy != true) return;

        var answer = MessageBox.Show(
            this,
            "A transaction is in progress. Closing now does not cancel it on the terminal, " +
            "and the outcome will not be shown here.\n\nClose anyway?",
            "Transaction in progress",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) e.Cancel = true;
    }
}
