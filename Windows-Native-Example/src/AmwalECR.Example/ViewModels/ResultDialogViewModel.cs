using System.ComponentModel;
using AmwalECR.Example.Core.Transaction;
using AmwalECR.Example.Services;
using AmwalPay.Ecr;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmwalECR.Example.ViewModels;

/// <summary>
/// The port of <c>TransactionResultDialog</c>: how a transaction ended, the fields
/// a cashier reads, the terminal's raw answer for when it does not add up, and —
/// after an inquiry — the e-receipt QR.
/// <para>
/// It follows the transaction view-model while open, so the receipt appearing (or
/// the operator starting a look-up from an unknown outcome) updates or closes it.
/// </para>
/// </summary>
public sealed partial class ResultDialogViewModel : ObservableObject
{
    private readonly TransactionViewModel _transaction;
    private readonly IClipboardService _clipboard;
    private bool _handedOff;

    public ResultDialogViewModel(TransactionViewModel transaction, IClipboardService clipboard)
    {
        _transaction = transaction;
        _clipboard = clipboard;
        _model = Build() ?? throw new InvalidOperationException("No result to show");
        _transaction.PropertyChanged += OnTransactionChanged;
    }

    /// <summary>Asks the window to close itself.</summary>
    public event Action? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(Headline), nameof(Amount), nameof(HasAmount), nameof(Message), nameof(HasMessage),
        nameof(Fields), nameof(HasFields), nameof(Hints), nameof(HasHints), nameof(Note), nameof(HasNote),
        nameof(RawPayload), nameof(HasRawPayload), nameof(IsApproved), nameof(IsDeclined),
        nameof(IsUnknownOutcome), nameof(InquirableReference), nameof(OffersReceipt),
        nameof(ShowReceiptButton), nameof(ReceiptNote), nameof(HasReceiptNote))]
    private ResultDialogModel _model;

    [ObservableProperty]
    private string _copyStatus = "";

    [ObservableProperty]
    private string _closeReceiptStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloseReceipt))]
    private bool _closeReceiptBusy;

    public string Headline => Model.Headline;

    public string Amount => Model.Amount;

    public bool HasAmount => Model.Amount.Length != 0;

    public string Message => Model.Message ?? "";

    public bool HasMessage => Message.Length != 0;

    public IReadOnlyList<ResultField> Fields => Model.Fields;

    public bool HasFields => Model.Fields.Count != 0;

    public IReadOnlyList<string> Hints => Model.Hints;

    public bool HasHints => Model.Hints.Count != 0;

    public string Note => Model.Note ?? "";

    public bool HasNote => Note.Length != 0;

    public string RawPayload => Model.RawPayload ?? "";

    public bool HasRawPayload => RawPayload.Length != 0;

    public bool IsApproved => Model.Tone == ResultTone.Approved;

    public bool IsDeclined => Model.Tone == ResultTone.Declined;

    public bool IsUnknownOutcome => Model.IsUnknownOutcome;

    public string InquirableReference => Model.InquirableReference ?? "";

    public string UnknownOutcomeWarning => ResultPresenter.UnknownOutcomeWarning;

    // E-receipt. Offered on an inquiry result over Wi-Fi: the customer scans the
    // QR and reads the receipt on their own phone, so nothing is printed.

    public bool OffersReceipt => Model.OffersReceipt;

    private TransactionState.Inquired? Inquired => _transaction.State as TransactionState.Inquired;

    public bool ReceiptFetching => Inquired?.FetchingReceipt == true;

    public bool ShowReceiptButton => Model.OffersReceipt && Inquired is { Receipt: null, FetchingReceipt: false };

    public string ReceiptUrl => (Inquired?.Receipt as EcrReceipt.Ready)?.Url ?? "";

    public bool HasReceiptQr => ReceiptUrl.Length != 0;

    public string ReceiptProblem => Inquired?.Receipt switch
    {
        EcrReceipt.Unavailable u => u.Reason.Length != 0 ? u.Reason : "No receipt is available for this transaction",
        EcrReceipt.Failed f => f.Failure.Message,
        _ => "",
    };

    public bool HasReceiptProblem => ReceiptProblem.Length != 0;

    public string ReceiptNote => Model.ReceiptNote ?? "";

    public bool HasReceiptNote => ReceiptNote.Length != 0;

    /// <summary>
    /// Offered on a completed sale / void / refund over Wi‑Fi or Web Service.
    /// The dialog stays open so the cashier can keep reading the result.
    /// </summary>
    public bool ShowCloseReceipt =>
        !CloseReceiptBusy
        && _transaction.State is TransactionState.Completed or TransactionState.Settled
        && _transaction.CanCloseTerminalReceipt();

    private ResultDialogModel? Build() =>
        ResultPresenter.For(_transaction.State, _transaction.SelectedConfig?.UsesWebService == true);

    private void OnTransactionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TransactionViewModel.OperationTimer)
            or nameof(TransactionViewModel.HasOperationTimer)
            or nameof(TransactionViewModel.CountdownLabel)
            or nameof(TransactionViewModel.CountdownCaption))
        {
            OnPropertyChanged(nameof(HasOperationTimer));
            OnPropertyChanged(nameof(CountdownLabel));
            OnPropertyChanged(nameof(CountdownCaption));
            return;
        }

        if (e.PropertyName != nameof(TransactionViewModel.State)) return;

        var state = _transaction.State;
        if (!state.IsResult)
        {
            // Another operation has started (the look-up offered here): this
            // result is no longer the one on screen.
            CloseRequested?.Invoke();
            return;
        }

        if (Build() is { } next) Model = next;
        OnPropertyChanged(nameof(ReceiptFetching));
        OnPropertyChanged(nameof(ShowReceiptButton));
        OnPropertyChanged(nameof(ReceiptUrl));
        OnPropertyChanged(nameof(HasReceiptQr));
        OnPropertyChanged(nameof(ReceiptProblem));
        OnPropertyChanged(nameof(HasReceiptProblem));
        OnPropertyChanged(nameof(HasOperationTimer));
        OnPropertyChanged(nameof(CountdownLabel));
        OnPropertyChanged(nameof(CountdownCaption));
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private async Task CloseReceipt()
    {
        if (!ShowCloseReceipt) return;
        CloseReceiptBusy = true;
        CloseReceiptStatus = "";
        try
        {
            CloseReceiptStatus = await _transaction.CloseTerminalReceiptAsync();
        }
        finally
        {
            CloseReceiptBusy = false;
            OnPropertyChanged(nameof(ShowCloseReceipt));
        }
    }

    /// <summary>The window has closed, however it was closed.</summary>
    public void OnClosed()
    {
        _transaction.PropertyChanged -= OnTransactionChanged;
        // Handing off to a look-up must not reset the screen under it.
        if (!_handedOff) _transaction.ResultAcknowledged();
    }

    [RelayCommand]
    private void InquireByReference()
    {
        if (!IsUnknownOutcome) return;
        _handedOff = true;
        _transaction.InquireAboutReference(InquirableReference);
    }

    [RelayCommand]
    private async Task RequestReceipt() => await _transaction.RequestReceiptAsync();

    [RelayCommand]
    private void CancelReceipt() => _transaction.CancelCommand.Execute(null);

    public bool HasOperationTimer => _transaction.HasOperationTimer;

    public string CountdownLabel => _transaction.CountdownLabel;

    public string CountdownCaption => _transaction.CountdownCaption;

    [RelayCommand]
    private void CopyRaw() => CopyStatus = _clipboard.TryCopy(RawPayload)
        ? "Copied to clipboard"
        : "Could not copy: the clipboard is in use";

    [RelayCommand]
    private void CopyReference() => CopyStatus = _clipboard.TryCopy(InquirableReference)
        ? "Reference copied to clipboard"
        : "Could not copy: the clipboard is in use";
}
