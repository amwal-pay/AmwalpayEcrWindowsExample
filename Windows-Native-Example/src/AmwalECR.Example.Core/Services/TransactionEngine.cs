using System.Globalization;
using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Transaction;
using AmwalPay.Ecr;

namespace AmwalECR.Example.Core.Services;

/// <summary>
/// Runs one operation against a registered terminal: resolve the config, open the
/// session through <see cref="EcrSessions.Open"/>, probe, execute, and classify
/// the answer. The transaction view-model owns the screen state and the
/// cancellation source; this owns the flow, so it can be exercised without a UI.
/// <para>
/// Transport choice (LAN / Web Service) is owned by <see cref="EcrSessions.Open"/>
/// so sale, inquiry recovery and receipt share one dispatch.
/// </para>
/// </summary>
public sealed class TransactionEngine
{
    private readonly TerminalRepository _repository;
    private readonly EcrExampleSettings _settings;
    private readonly IEcrLogger _lanLogger;
    private readonly IEcrLogger _webServiceLogger;

    public TransactionEngine(
        TerminalRepository repository,
        EcrExampleSettings settings,
        IEcrLogger lanLogger,
        IEcrLogger webServiceLogger)
    {
        _repository = repository;
        _settings = settings;
        _lanLogger = lanLogger;
        _webServiceLogger = webServiceLogger;
    }

    public SelectedTerminalConfig ResolveConfig(Terminal terminal) =>
        SelectedTerminalConfig.Resolve(terminal, _settings.Environment, _settings.SecureHashKeyFor(terminal.Mode));

    public void LogSelectedConfig(SelectedTerminalConfig config)
    {
        var logger = LoggerFor(config);
        var prefix = config.UsesWebService ? "[Web Service]" : "[Wi-Fi/LAN]";
        logger.Debug($"{prefix} selected {config.Terminal.SerialNumber} ({config.Mode.Label()})");
        logger.Debug($"{prefix} SDK connection: {config.ConnectionSummary}");
        foreach (var issue in config.Issues)
        {
            logger.Debug($"{prefix} config issue: {issue}");
        }
    }

    /// <summary>
    /// Runs <paramref name="request"/> and returns the state to show. <paramref name="report"/>
    /// is called with <see cref="TransactionState.InProgress"/> once the request is
    /// about to go out. Returns <see cref="TransactionState.Idle"/> when the operator
    /// cancelled and nothing can have happened on the terminal.
    /// </summary>
    public async Task<TransactionState> RunAsync(
        TransactionRequest request,
        Action<TransactionState> report,
        CancellationToken cancellationToken)
    {
        var registered = _repository.FindBySerial(request.TerminalSerial);
        if (registered == null)
        {
            return new TransactionState.Failed($"Terminal {request.TerminalSerial} is no longer registered");
        }

        var active = ResolveConfig(registered);
        if (!active.IsReady)
        {
            return new TransactionState.Failed(string.Join("\n", active.Issues));
        }

        EcrOpenedSession session;
        try
        {
            session = OpenSession(registered, active);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return new TransactionState.Failed(e.Message);
        }

        // Only money-moving operations need a reference of their own. It is made
        // here rather than left to the SDK so that it is known even if the wait is
        // cancelled: the SDK returns it on a result, and a cancelled call has none.
        var reference = request.Type.MovesMoney() && request.MerchantReference.Length == 0
            ? NewReference()
            : request.MerchantReference;

        try
        {
            var probe = await session.ProbeReachabilityAsync(cancellationToken).ConfigureAwait(true);
            if (probe != null && !probe.Reachable)
            {
                LoggerFor(active).Debug(
                    $"[{probe.Endpoint}] probe failed" + (probe.Error != null ? $" — {probe.Error}" : ""));
                return new TransactionState.Failed(
                    TransactionOutcomes.UnreachableMessage(probe.Endpoint, probe.Port, probe.Error));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Nothing has been sent, so stopping here changes nothing anywhere.
            return new TransactionState.Idle();
        }

        report(new TransactionState.InProgress());

        try
        {
            return await ExecuteAsync(session, request, reference, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoggerFor(active).Debug($"[App] wait cancelled by the operator ({request.Type.DisplayName()} {reference})");
            return CancelledState(request, reference);
        }
        catch (ArgumentException e)
        {
            // Thrown before anything is built or sent — e.g. a reference the wire
            // format cannot carry — so this is a plain "not completed".
            return new TransactionState.Failed(e.Message);
        }
        catch (Exception e)
        {
            LoggerFor(active).Debug($"[App] unexpected error: {e}");
            return request.Type.MovesMoney()
                ? new TransactionState.Failed($"Unexpected error: {e.Message}", reference)
                : new TransactionState.Failed($"Unexpected error: {e.Message}");
        }
    }

    /// <summary>
    /// What to show after the operator stopped waiting. Stopping here does not stop
    /// the terminal: if the request already reached it, the transaction can still
    /// complete, so a money-moving operation is an unknown outcome — never
    /// "cancelled".
    /// </summary>
    public static TransactionState CancelledState(TransactionRequest request, string reference) =>
        request.Type.MovesMoney()
            ? new TransactionState.Failed(
                "You stopped waiting for the terminal. This does not cancel the transaction: " +
                "if the request had already reached the terminal, it may still complete.",
                reference)
            : new TransactionState.Idle();

    private async Task<TransactionState> ExecuteAsync(
        EcrOpenedSession session,
        TransactionRequest request,
        string reference,
        CancellationToken ct)
    {
        if (request.Type == EcrTransactionType.Inquiry)
        {
            var inquiry = request.LooksUpByReference
                ? await session.InquireByReferenceAsync(
                    originalReference: request.OriginalReference,
                    transactionDate: DateForReferenceLookup(session, request),
                    originalTerminalId: request.OriginalTerminalId,
                    merchantReference: request.MerchantReference,
                    cancellationToken: ct).ConfigureAwait(true)
                : await session.InquireAsync(
                    receiptNumber: request.ReceiptNumber,
                    transactionDate: request.TransactionDate,
                    originalTerminalId: request.OriginalTerminalId,
                    cancellationToken: ct).ConfigureAwait(true);
            return TransactionOutcomes.FromInquiry(inquiry, request);
        }

        var result = request.Type switch
        {
            EcrTransactionType.Sale => await session.SaleAsync(
                request.Amount!.Value, reference, ct).ConfigureAwait(true),
            EcrTransactionType.Void => await session.VoidAsync(
                request.ReceiptNumber, request.OriginalTerminalId, reference, ct).ConfigureAwait(true),
            EcrTransactionType.Refund => await session.RefundAsync(
                request.Amount!.Value,
                request.ReceiptNumber,
                request.TransactionDate,
                request.OriginalTerminalId,
                reference,
                ct).ConfigureAwait(true),
            _ => throw new InvalidOperationException($"{request.Type.DisplayName()} is not run from here"),
        };

        return TransactionOutcomes.FromResult(result, request);
    }

    /// <summary>
    /// The Web Service asks for a date on every status lookup; a lookup by
    /// reference has none typed (a reference is not scoped to a day on the LAN), so
    /// today's is used — the case is a transaction that has just gone unanswered.
    /// </summary>
    private static string DateForReferenceLookup(EcrOpenedSession session, TransactionRequest request) =>
        request.TransactionDate.Length == 0 && session.UsesWebService
            ? DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : request.TransactionDate;

    /// <summary>Fetches the e-receipt of an inquired transaction. Never throws except for cancellation.</summary>
    public async Task<EcrReceipt> FetchReceiptAsync(TransactionState.Inquired current, CancellationToken ct)
    {
        var registered = _repository.FindBySerial(current.Request.TerminalSerial);
        if (registered == null)
        {
            return Unavailable($"Terminal {current.Request.TerminalSerial} is no longer registered");
        }

        var found = current.Inquiry as EcrInquiry.Found;
        var receiptNumber = !string.IsNullOrWhiteSpace(found?.Transaction.Stan)
            ? found!.Transaction.Stan
            : current.Request.ReceiptNumber;

        if (string.IsNullOrWhiteSpace(receiptNumber))
        {
            return Unavailable("This transaction has no receipt number to fetch a receipt by");
        }

        var day = found != null ? DayOf(found.Transaction.TransactionTime) : "";
        var transactionDate = day.Length != 0 ? day : current.Request.TransactionDate;

        var active = ResolveConfig(registered);
        if (!active.IsReady)
        {
            return Unavailable(string.Join("\n", active.Issues));
        }

        try
        {
            return await OpenSession(registered, active).ReceiptAsync(
                receiptNumber, transactionDate, current.Request.OriginalTerminalId, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // A receipt reads and nothing more, so failing to get one is never
            // worse than not having asked.
            LoggerFor(active).Debug($"[App] receipt failed: {e.Message}");
            return Unavailable(e.Message);
        }
    }

    private static EcrReceipt Unavailable(string reason) =>
        new EcrReceipt.Unavailable(MerchantReference: "", Reason: reason, Raw: "");

    private EcrOpenedSession OpenSession(Terminal registered, SelectedTerminalConfig active) =>
        EcrSessions.Open(
            terminalSerial: registered.SerialNumber,
            plan: active.Plan,
            // The Windows SDK ships no USB channel; USB terminals never get here
            // because SelectedTerminalConfig marks them unsupported.
            usbChannel: null,
            logger: LoggerFor(active));

    private IEcrLogger LoggerFor(SelectedTerminalConfig config) =>
        config.UsesWebService ? _webServiceLogger : _lanLogger;

    /// <summary>The backend's <c>2026-08-09T16:58:16</c> as <c>yyyyMMdd</c>, or empty.</summary>
    public static string DayOf(string transactionTime)
    {
        var digits = new string(transactionTime.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 8 ? digits[..8] : "";
    }

    /// <summary>Same shape the SDK generates: 12 upper-case hex characters.</summary>
    public static string NewReference() => Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
}
