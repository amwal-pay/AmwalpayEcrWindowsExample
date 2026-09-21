using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Services;
using AmwalECR.Example.Core.Transaction;
using AmwalPay.Ecr;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmwalECR.Example.ViewModels;

/// <summary>
/// Drives the transaction screen: the port of <c>TransactionViewModel</c>.
/// <para>
/// Holds the in-flight transaction and its <see cref="CancellationTokenSource"/>.
/// The flow itself (config, probe, execute, classify) is
/// <see cref="TransactionEngine"/>; this owns the state the screen shows.
/// </para>
/// </summary>
public sealed partial class TransactionViewModel : ObservableObject
{
    private readonly TerminalRepository _repository;
    private readonly EcrExampleSettings _settings;
    private readonly TransactionEngine _engine;

    private string? _selectedSerial;
    private CancellationTokenSource? _cts;

    public TransactionViewModel(
        TerminalRepository repository,
        EcrExampleSettings settings,
        TransactionEngine engine)
    {
        _repository = repository;
        _settings = settings;
        _engine = engine;
        Form = new TransactionFormViewModel();

        _terminals = repository.GetAll();
        EnsureDefaultSelection();
        _selectedConfig = ResolveSelected();

        repository.Changed += OnRepositoryChanged;
        // Keys edited on the Terminals screen must show up in the config card, and
        // gate the Start button, the moment the operator is back.
        settings.Changed += RefreshConfig;
    }

    public TransactionFormViewModel Form { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTerminals), nameof(NoTerminals))]
    private IReadOnlyList<Terminal> _terminals;

    [ObservableProperty]
    private SelectedTerminalConfig? _selectedConfig;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsBusy),
        nameof(IsNotBusy),
        nameof(IsChecking),
        nameof(IsInProgress),
        nameof(CanStart),
        nameof(StatusText))]
    private TransactionState _state = new TransactionState.Idle();

    public bool HasTerminals => Terminals.Count != 0;

    public bool NoTerminals => Terminals.Count == 0;

    public bool IsBusy => State.IsBusy;

    public bool IsNotBusy => !State.IsBusy;

    public bool IsChecking => State is TransactionState.Checking;

    public bool IsInProgress => State is TransactionState.InProgress;

    public string StatusText => State is TransactionState.Checking ? "Checking the terminal…" : "Waiting for the POS terminal…";

    /// <summary>Only a ready plan may start: the operator sees what is missing before paying.</summary>
    public bool CanStart => !State.IsBusy && SelectedConfig?.IsReady == true;

    /// <summary>Terminal picked in the dropdown; bound by serial so a refreshed list keeps the selection.</summary>
    public string? SelectedSerial
    {
        get => _selectedSerial;
        set
        {
            // A list being rebuilt reports null for a moment; that is not a choice.
            if (value == null || value == _selectedSerial) return;
            _selectedSerial = value;
            RefreshConfig();
            if (SelectedConfig != null) _engine.LogSelectedConfig(SelectedConfig);
        }
    }

    /// <summary>Web Service finds transactions by receipt number and date; the view says so beside the by-reference switch.</summary>
    public bool IsWebServiceSelected => SelectedConfig?.UsesWebService == true;

    public string ReferenceLookupHint =>
        "Over Web Service a lookup by reference is best-effort: the Web Service finds transactions by receipt number and date.";

    private void OnRepositoryChanged()
    {
        Terminals = _repository.GetAll();
        EnsureDefaultSelection();
        RefreshConfig();
        // Re-announce the selection after the dropdown's list was replaced.
        OnPropertyChanged(nameof(SelectedSerial));
    }

    private void EnsureDefaultSelection()
    {
        var stillRegistered = Terminals.Any(t => t.SerialNumber == _selectedSerial);
        if (!stillRegistered) _selectedSerial = Terminals.FirstOrDefault()?.SerialNumber;
    }

    private SelectedTerminalConfig? ResolveSelected()
    {
        var terminal = Terminals.FirstOrDefault(t => t.SerialNumber == _selectedSerial) ?? Terminals.FirstOrDefault();
        return terminal == null ? null : _engine.ResolveConfig(terminal);
    }

    /// <summary>Re-resolves the plan from the current terminals and settings.</summary>
    public void RefreshConfig()
    {
        SelectedConfig = ResolveSelected();
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsWebServiceSelected));
    }

    partial void OnSelectedConfigChanged(SelectedTerminalConfig? value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsWebServiceSelected));
    }

    /// <summary>The Start button: validate, then run.</summary>
    [RelayCommand]
    private void Submit()
    {
        if (!CanStart) return;
        var serial = SelectedConfig?.Terminal.SerialNumber;
        if (serial == null) return;

        var request = Form.Validate(serial);
        if (request != null) Start(request);
    }

    public void Start(TransactionRequest request)
    {
        if (State.IsBusy) return;

        State = new TransactionState.Checking();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunAsync(request, cts);
    }

    private async Task RunAsync(TransactionRequest request, CancellationTokenSource cts)
    {
        try
        {
            State = await _engine.RunAsync(request, progress => State = progress, cts.Token);
        }
        catch (Exception e)
        {
            // The engine answers every failure itself; this is the last line, so
            // that the screen can never be left spinning.
            State = request.Type.MovesMoney()
                ? new TransactionState.Failed($"Unexpected error: {e.Message}", TransactionEngine.NewReference())
                : new TransactionState.Failed($"Unexpected error: {e.Message}");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Stops waiting. This cannot un-send a request: once it has reached the
    /// terminal the transaction may still complete, and the result says so rather
    /// than reporting a cancellation.
    /// </summary>
    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>The operator has read the result; back to idle and start the next one fresh.</summary>
    public void ResultAcknowledged()
    {
        if (State.IsResult) State = new TransactionState.Idle();
        Form.ClearAfterResult();
    }

    /// <summary>
    /// After an unknown outcome: look the transaction up by the reference it was
    /// sent with. The only safe next step — never a plain retry.
    /// </summary>
    public void InquireAboutReference(string reference)
    {
        if (State.IsBusy) return;
        var serial = SelectedConfig?.Terminal.SerialNumber;
        if (serial == null) return;

        Start(new TransactionRequest
        {
            Type = EcrTransactionType.Inquiry,
            TerminalSerial = serial,
            Amount = null,
            OriginalReference = reference,
            MerchantReference = reference,
        });
    }

    public async Task RequestReceiptAsync()
    {
        if (State is not TransactionState.Inquired current || current.FetchingReceipt) return;

        State = current with { FetchingReceipt = true };

        EcrReceipt receipt;
        try
        {
            receipt = await _engine.FetchReceiptAsync(current, CancellationToken.None);
        }
        catch (Exception e)
        {
            receipt = new EcrReceipt.Unavailable("", e.Message, "");
        }

        // The operator may have closed the dialog while this was in flight.
        if (State is TransactionState.Inquired latest)
        {
            State = latest with { Receipt = receipt, FetchingReceipt = false };
        }
    }
}
