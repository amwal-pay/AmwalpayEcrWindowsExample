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
/// Transport choice (Wi-Fi / Web Service) is owned by <see cref="EcrSessions.Open"/>
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
        var prefix = config.UsesWebService ? "[Web Service]" : config.Plan.UsesUsbCable ? "[USB]" : "[Wi-Fi]";
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
            // Same as ECR-simulator: by-reference leaves the date blank (a
            // reference is not day-scoped); by-STAN sends the form date. Do not
            // invent today's date for Web Service — Hub/MMA filter by that day
            // and a synthetic date misses older transactions.
            var inquiry = request.LooksUpByReference
                ? await session.InquireByReferenceAsync(
                    originalReference: request.OriginalReference,
                    transactionDate: request.TransactionDate,
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
    /// Asks the terminal to put its receipt away. Call from the Close receipt action
    /// (not when the result dialog is dismissed). Never throws.
    /// </summary>
    /// <param name="terminalSerial">Selected terminal.</param>
    /// <param name="originalMerchantReference">
    /// Required for Web Service — the sale / void / refund reference. Ignored on Wi-Fi.
    /// </param>
    public async Task<string> CloseTerminalReceiptAsync(
        string terminalSerial,
        string originalMerchantReference = "",
        CancellationToken ct = default)
    {
        var registered = _repository.FindBySerial(terminalSerial);
        if (registered == null) return $"Terminal {terminalSerial} is no longer registered";

        var mode = registered.Mode;
        if (!(mode.IsIpBased() || mode.IsUsbCable() || mode == EcrMode.WebService))
        {
            return "Close receipt is not available for this terminal mode";
        }

        var active = ResolveConfig(registered);
        if (!active.IsReady) return string.Join("\n", active.Issues);

        try
        {
            var closed = await OpenSession(registered, active).CloseReceiptAsync(
                originalMerchantReference: originalMerchantReference,
                cancellationToken: ct).ConfigureAwait(true);
            return closed switch
            {
                EcrReceiptClosed.Idle => "Terminal is back on its idle screen",
                EcrReceiptClosed.Refused refused => $"Terminal refused: {refused.Reason}",
                EcrReceiptClosed.Failed failed => $"Could not reach the terminal: {failed.Failure.Message}",
                _ => "Could not close the receipt",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            LoggerFor(active).Debug($"[App] close receipt failed: {e.Message}");
            return $"Could not close the receipt: {e.Message}";
        }
    }

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
            usbChannel: active.Plan.UsesUsbCable ? () => OpenCable(registered) : null,
            logger: LoggerFor(active));

    /// <summary>
    /// The COM port to open. A remembered COM name is replaced with the USB
    /// serial of the device on that port, so the next sale follows the device
    /// when Windows assigns a different COM number.
    /// </summary>
    private IEcrChannel OpenCable(Terminal registered)
    {
        var port = CablePort(registered);
        _lanLogger.Debug(
            port.Length == 0
                ? "[USB] no COM port to open. Plug the terminal in. Windows assigns the number from the USB serial."
                : $"[USB] this sale opens {port} at 115200 8N1");
        return new LoggingCable(new SerialEcrChannel(port), _lanLogger);
    }

    private string CablePort(Terminal registered)
    {
        var saved = (registered.ComPort ?? "").Trim();
        var open = SerialPorts.PortFor(saved);
        _lanLogger.Debug(
            open.Port.Length == 0
                ? $"[USB] saved cable '{saved}' is not plugged in"
                : $"[USB] saved cable '{saved}' is {open.Port} right now");
        if (open.Port.Length == 0) return "";
        if (!string.Equals(open.Remember, saved, StringComparison.OrdinalIgnoreCase))
        {
            _repository.Save(registered with { ComPort = open.Remember }, registered.SerialNumber);
        }

        return open.Port;
    }

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

    /// <summary>
    /// Writes the cable steps the serial port itself does not: which COM port
    /// was opened, how many bytes went out, and whether any came back.
    /// </summary>
    private sealed class LoggingCable : IEcrChannel
    {
        private readonly SerialEcrChannel _inner;
        private readonly IEcrLogger _logger;

        public LoggingCable(SerialEcrChannel inner, IEcrLogger logger)
        {
            _inner = inner;
            _logger = logger;
        }

        public string Endpoint => _inner.Endpoint;

        public string Host => _inner.Host;

        public int Port => _inner.Port;

        public async Task<string?> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var problem = await _inner.ProbeAsync(timeout, cancellationToken).ConfigureAwait(false);
            _logger.Debug(problem == null
                ? $"[USB] {_inner.Endpoint} is listed. The COM port stays closed until the sale is written."
                : $"[USB] probe: {problem}");
            return problem;
        }

        public async Task<byte[]> ExchangeAsync(byte[] body, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            _logger.Debug($"[USB] writing {body.Length} bytes to {_inner.Endpoint}");
            try
            {
                var answer = await _inner.ExchangeAsync(body, timeout, cancellationToken).ConfigureAwait(false);
                _logger.Debug($"[USB] read {answer.Length} bytes from {_inner.Endpoint}");
                return answer;
            }
            catch (Exception e)
            {
                _logger.Debug($"[USB] {_inner.Endpoint} — {e.GetType().Name}: {e.Message}");
                throw;
            }
        }
    }
}
