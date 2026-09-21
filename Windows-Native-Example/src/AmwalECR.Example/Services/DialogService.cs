using System.Windows;
using AmwalECR.Example.ViewModels;
using AmwalECR.Example.Views;

namespace AmwalECR.Example.Services;

public sealed class DialogService : IDialogService
{
    public bool ShowTerminalEdit(TerminalEditViewModel viewModel)
    {
        var window = new TerminalEditWindow { DataContext = viewModel, Owner = Owner() };
        viewModel.CloseRequested += saved => window.DialogResult = saved;
        return window.ShowDialog() == true;
    }

    public void ShowResult(ResultDialogViewModel viewModel)
    {
        var window = new ResultDialogWindow { DataContext = viewModel, Owner = Owner() };
        viewModel.CloseRequested += () => window.Close();
        window.Closed += (_, _) => viewModel.OnClosed();
        window.ShowDialog();
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            Owner() ?? Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private static Window? Owner() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
}

public sealed class ClipboardService : IClipboardService
{
    public bool TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process has the clipboard open; the caller says so.
            return false;
        }
    }
}
