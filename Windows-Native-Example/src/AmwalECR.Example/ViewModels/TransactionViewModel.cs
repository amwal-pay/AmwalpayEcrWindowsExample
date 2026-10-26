using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Services;
using AmwalECR.Example.Core.Transaction;
using AmwalECR.Example.Services;
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
    private readonly IDialogService _dialogs;

    private string? _selectedSerial;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _countdownCts;

    public TransactionViewModel(
        TerminalRepository repository,
        EcrExampleSettings settings,
        TransactionEngine engine,
        IDialogService dialogs)
    {
        _repository = repository;
        _settings = settings;
        _engine = engine;
        _dialogs = dialogs;
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
        nameof(CanSettle),
        nameof(CanCheckReachability),
        nameof(CanCancel),
        nameof(StatusText))]
    private TransactionState _state = new TransactionState.Idle();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationTimer), nameof(CountdownLabel), nameof(CountdownCaption))]
    private OperationTimer? _operationTimer;

    public bool HasTerminals => Terminals.Count != 0;

    public bool NoTerminals => Terminals.Count == 0;

    public bool IsBusy => State.IsBusy;

    public bool IsNotBusy => !State.IsBusy;

    public bool IsChecking => State is TransactionState.Checking;

    public bool IsInProgress => State is TransactionState.InProgress;

    public bool CanCancel =>
        State.IsBusy || State is TransactionState.Inquired { FetchingReceipt: true };

    public bool HasOperationTimer => OperationTimer != null;

    public string CountdownLabel => OperationTimer?.Label ?? "";

    public string CountdownCaption => OperationTimer?.Caption ?? "";

    public string StatusText => State is TransactionState.Checking ? "Checking the terminal…" : "Waiting for the POS terminal…";

    /// <summary>Only a ready plan may start: the operator sees what is missing before paying.</summary>
    public bool CanStart => !State.IsBusy && SelectedConfig?.IsReady == true;

    /// <summary>Wi-Fi, USB cable, and Web Service can close a batch.</summary>
    public bool ShowsSettlement =>
        SelectedConfig != null && SelectedConfig.Mode.IsSupportedInExample();

    /// <summary>The Manual settlement button: ready, idle, and a supported terminal.</summary>
    public bool CanSettle => ShowsSettlement && !State.IsBusy && SelectedConfig?.IsReady == true;

    /// <summary>Web Service only. The check is a button, not a step before a sale.</summary>
    public bool ShowsReachability => SelectedConfig?.UsesWebService == true;

    public bool CanCheckReachability =>
        ShowsReachability && !CheckingReachability && !State.IsBusy && SelectedConfig?.IsReady == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckReachability))]
    private bool _checkingReachability;

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
        OnPropertyChanged(nameof(CanSettle));
        OnPropertyChanged(nameof(ShowsSettlement));
        OnPropertyChanged(nameof(ShowsReachability));
        OnPropertyChanged(nameof(CanCheckReachability));
    }

    partial void OnSelectedConfigChanged(SelectedTerminalConfig? value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanSettle));
        OnPropertyChanged(nameof(ShowsSettlement));
        OnPropertyChanged(nameof(ShowsReachability));
        OnPropertyChanged(nameof(CanCheckReachability));
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

    /// <summary>
    /// Manual settlement, as the simulator offers it: confirm first, because the
    /// terminal closes the batch as soon as the message arrives.
    /// </summary>
    [RelayCommand]
    private void Settle()
    {
        if (!CanSettle) return;
        var serial = SelectedConfig?.Terminal.SerialNumber;
        if (serial == null) return;
        if (!_dialogs.Confirm(
                "Manual settlement",
                "It closes the current batch and prints the settlement receipt."))
        {
            return;
        }

        Start(new TransactionRequest
        {
            Type = EcrTransactionType.Settlement,
            TerminalSerial = serial,
        });
    }

    /// <summary>Posts /Ecr/CheckReachability. Does not start a transaction.</summary>
    [RelayCommand]
    private async Task CheckReachability()
    {
        if (!CanCheckReachability) return;
        var serial = SelectedConfig?.Terminal.SerialNumber;
        if (serial == null) return;

        CheckingReachability = true;
        try
        {
            var message = await _engine.CheckWebServiceReachabilityAsync(serial);
            _dialogs.Alert("Check reachability", message.Message);
        }
        finally
        {
            CheckingReachability = false;
        }
    }

    public void Start(TransactionRequest request)
    {
        if (State.IsBusy) return;

        State = new TransactionState.Checking();
        StartOperationCountdown(ResponseTimeoutSeconds());
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunAsync(request, cts);
    }

    private async Task RunAsync(TransactionRequest request, CancellationTokenSource cts)
    {
        try
        {
            State = await _engine.RunAsync(
                request,
                progress =>
                {
                    State = progress;
                    // Align budget if Web Service (130s) differs from the first guess.
                    if (progress is TransactionState.InProgress)
                    {
                        StartOperationCountdown(ResponseTimeoutSeconds());
                    }
                },
                cts.Token);
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
            ClearOperationCountdown();
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
        ClearOperationCountdown();
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
            // Blank so the SDK mints this inquiry's own merchantReference;
            // the looked-up value stays on OriginalReference only.
            MerchantReference = "",
        });
    }

    /// <summary>
    /// Wi‑Fi / USB / Web Service can ask the terminal to clear its receipt screen.
    /// App-to-app is excluded: the payment app already waits for the operator to
    /// close the receipt before answering.
    /// </summary>
    public bool CanCloseTerminalReceipt()
    {
        if (SelectedConfig == null) return false;
        var mode = SelectedConfig.Mode;
        return mode.IsIpBased() || mode.IsUsbCable() || mode == EcrMode.WebService;
    }

    /// <summary>
    /// Tells the terminal the cashier has finished, so it can put its receipt away.
    /// Separate from dismissing the result dialog.
    /// </summary>
    public async Task<string> CloseTerminalReceiptAsync()
    {
        var serial = SelectedConfig?.Terminal.SerialNumber;
        if (serial == null || !CanCloseTerminalReceipt()) return "";

        var webService = SelectedConfig?.UsesWebService == true;
        var originalMerchantReference = "";
        if (webService)
        {
            if (State is TransactionState.Completed completed
                && (completed.Type == EcrTransactionType.Sale
                    || completed.Type == EcrTransactionType.Void
                    || completed.Type == EcrTransactionType.Refund))
            {
                originalMerchantReference = completed.Result.MerchantReference;
            }

            if (string.IsNullOrWhiteSpace(originalMerchantReference))
            {
                return "Close receipt needs the sale / void / refund reference";
            }
        }

        return await _engine.CloseTerminalReceiptAsync(serial, originalMerchantReference);
    }

    public async Task RequestReceiptAsync()
    {
        if (State is not TransactionState.Inquired current || current.FetchingReceipt) return;

        State = current with { FetchingReceipt = true };
        StartOperationCountdown(ResponseTimeoutSeconds());
        var cts = new CancellationTokenSource();
        _cts = cts;

        EcrReceipt receipt;
        try
        {
            receipt = await _engine.FetchReceiptAsync(current, cts.Token);
        }
        catch (OperationCanceledException)
        {
            receipt = new EcrReceipt.Unavailable("", "Receipt fetch cancelled", "");
        }
        catch (Exception e)
        {
            receipt = new EcrReceipt.Unavailable("", e.Message, "");
        }
        finally
        {
            ClearOperationCountdown();
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }

        // The operator may have closed the dialog while this was in flight.
        if (State is TransactionState.Inquired latest)
        {
            State = latest with { Receipt = receipt, FetchingReceipt = false };
        }
    }

    private int ResponseTimeoutSeconds()
    {
        var seconds = (int)(SelectedConfig?.EcrConfig.ResponseTimeout.TotalSeconds ?? 120);
        return Math.Max(1, seconds);
    }

    private void StartOperationCountdown(int totalSeconds)
    {
        _countdownCts?.Cancel();
        _countdownCts?.Dispose();
        var total = Math.Max(1, totalSeconds);
        var cts = new CancellationTokenSource();
        _countdownCts = cts;
        OperationTimer = new OperationTimer(total, total);
        _ = TickCountdownAsync(total, cts);
    }

    private async Task TickCountdownAsync(int total, CancellationTokenSource cts)
    {
        try
        {
            var left = total;
            while (!cts.IsCancellationRequested && left > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(true);
                left -= 1;
                if (ReferenceEquals(_countdownCts, cts))
                {
                    OperationTimer = new OperationTimer(left, total);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Countdown stopped because the operation finished or was cancelled.
        }
    }

    private void ClearOperationCountdown()
    {
        _countdownCts?.Cancel();
        _countdownCts?.Dispose();
        _countdownCts = null;
        OperationTimer = null;
    }
}
