using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using AmwalECR.Example.ViewModels;

namespace AmwalECR.Example.Services;

/// <summary>
/// Opens the result dialog when the transaction reaches a result, once: the
/// operator acknowledges it, the state returns to idle, and the next result opens
/// the next one. Shown from the dispatcher rather than from inside the state
/// change, so the modal loop never runs inside a binding update.
/// </summary>
public sealed class ResultDialogPresenter
{
    private readonly TransactionViewModel _transaction;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private bool _open;

    public ResultDialogPresenter(TransactionViewModel transaction, IDialogService dialogs, IClipboardService clipboard)
    {
        _transaction = transaction;
        _dialogs = dialogs;
        _clipboard = clipboard;
        transaction.PropertyChanged += OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TransactionViewModel.State) || _open || !_transaction.State.IsResult) return;

        _open = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Show));
    }

    private void Show()
    {
        try
        {
            // The state may have moved on between the change and now.
            if (!_transaction.State.IsResult) return;
            _dialogs.ShowResult(new ResultDialogViewModel(_transaction, _clipboard));
        }
        finally
        {
            _open = false;
        }

        // A result that arrived while the dialog was closing gets its own dialog.
        if (_transaction.State.IsResult)
        {
            _open = true;
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Show));
        }
    }
}
