using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmwalECR.Example.ViewModels;

/// <summary>
/// The window: header, the current page (transaction or terminals — the two
/// destinations of the Android sample's NavHost) and the debug log panel.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(TransactionViewModel transaction, TerminalsViewModel terminals, LogViewModel log)
    {
        Transaction = transaction;
        Terminals = terminals;
        Log = log;
        _currentPage = transaction;
    }

    public TransactionViewModel Transaction { get; }

    public TerminalsViewModel Terminals { get; }

    public LogViewModel Log { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTerminalsPage), nameof(IsTransactionPage), nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(ShowTerminalsCommand))]
    private object _currentPage;

    public bool IsTerminalsPage => ReferenceEquals(CurrentPage, Terminals);

    public bool IsTransactionPage => ReferenceEquals(CurrentPage, Transaction);

    public string Title => IsTerminalsPage ? "POS terminals" : "ECR";

    [RelayCommand(CanExecute = nameof(IsTransactionPage))]
    private void ShowTerminals() => CurrentPage = Terminals;

    [RelayCommand(CanExecute = nameof(IsTerminalsPage))]
    private void Back()
    {
        CurrentPage = Transaction;
        // Terminals or keys may have changed while away.
        Transaction.RefreshConfig();
    }
}
