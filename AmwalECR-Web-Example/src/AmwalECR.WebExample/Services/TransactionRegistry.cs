using AmwalECR.Example.Core.Services;
using AmwalECR.Example.Core.Transaction;
using AmwalPay.Ecr;

namespace AmwalECR.WebExample.Services;

/// <summary>
/// One operator flow at a moment, read under the registry's lock so state and version
/// always belong together.
/// </summary>
/// <param name="UsesWebService">Whether the terminal is reached over the Web Service; decides receipt support.</param>
/// <param name="Version">Bumped on every change, so a poller can tell whether anything moved.</param>
/// <param name="TimeoutSeconds">The configured response budget — 130 s on Web Service, 120 s on Wi‑Fi.</param>
/// <param name="RemainingSeconds">
/// What is left of it while the run is busy (counted on the server, so the page needs
/// no clock of its own); null once the run has finished.
/// </param>
public sealed record TransactionSnapshot(
    string Id,
    string TerminalSerial,
    TransactionRequest Request,
    bool UsesWebService,
    TransactionState State,
    long Version,
    int TimeoutSeconds = 120,
    int? RemainingSeconds = null);

/// <summary>One operator flow: the state machine the WPF transaction screen holds, kept on the server.</summary>
internal sealed class TransactionRun
{
    public TransactionRun(
        string id, TransactionRequest request, bool usesWebService, DateTimeOffset now, int timeoutSeconds = 120)
    {
        Id = id;
        Request = request;
        UsesWebService = usesWebService;
        Started = Updated = BusySince = now;
        TimeoutSeconds = Math.Max(1, timeoutSeconds);
    }

    /// <summary>The configured response timeout for the terminal this run uses.</summary>
    public int TimeoutSeconds { get; }

    /// <summary>When the current wait began — the countdown restarts for each follow-up.</summary>
    public DateTimeOffset BusySince { get; set; }

    public string Id { get; }

    public bool UsesWebService { get; }

    public DateTimeOffset Started { get; }

    public DateTimeOffset Updated { get; set; }

    /// <summary>What was asked for. Replaced when an unknown outcome is followed up by an inquiry.</summary>
    public TransactionRequest Request { get; set; }

    public TransactionState State { get; set; } = new TransactionState.Checking();

    public long Version { get; set; }

    public CancellationTokenSource? Cts { get; set; }

    public string TerminalSerial => Request.TerminalSerial;

    public TransactionSnapshot Snapshot(DateTimeOffset now) => new(
        Id,
        TerminalSerial,
        Request,
        UsesWebService,
        State,
        Version,
        TimeoutSeconds,
        // A receipt fetch leaves the state at "inquired" with FetchingReceipt set, and is a
        // wait of its own with its own timeout.
        State.IsBusy || State is TransactionState.Inquired { FetchingReceipt: true }
            ? Math.Max(0, TimeoutSeconds - (int)(now - BusySince).TotalSeconds)
            : null);
}

public abstract record StartResult
{
    private StartResult()
    {
    }

    public sealed record Started(TransactionSnapshot Run) : StartResult;

    /// <summary>Another transaction is already running on this terminal.</summary>
    public sealed record Busy(string ExistingId) : StartResult;
}

public abstract record FollowUp
{
    private FollowUp()
    {
    }

    public sealed record Started(TransactionSnapshot Run) : FollowUp;

    public sealed record NotFound : FollowUp;

    /// <summary>The run is not in a state that offers this step.</summary>
    public sealed record NotApplicable : FollowUp;

    /// <summary>Another transaction is running on the terminal.</summary>
    public sealed record Busy(string ExistingId) : FollowUp;
}

public abstract record CloseReceiptOutcome
{
    private CloseReceiptOutcome()
    {
    }

    public sealed record Done(TransactionSnapshot Run, string Message) : CloseReceiptOutcome;

    public sealed record NotFound : CloseReceiptOutcome;

    public sealed record NotApplicable(string? Message = null) : CloseReceiptOutcome;
}

public sealed class TransactionRegistryOptions
{
    /// <summary>
    /// How long a finished transaction nobody acknowledged is kept. Long on purpose:
    /// an unknown outcome carries the reference that is the only safe next step, and
    /// a reload or a day away from the till must not lose it.
    /// </summary>
    public TimeSpan FinishedRetention { get; set; } = TimeSpan.FromHours(12);
}

/// <summary>
/// Runs transactions on the server, keyed by an id the browser polls.
/// <para>
/// <b>A payment is not tied to the HTTP request.</b> A card payment takes as long
/// as the cardholder does and a browser tab can close at any moment, so every run
/// owns its own <see cref="CancellationTokenSource"/>. Nothing here ever looks at
/// <c>HttpContext.RequestAborted</c>: the only way to stop waiting is the explicit
/// Cancel, which — as in the WPF app — cannot un-send a request and shows an
/// unknown outcome, never "cancelled".
/// </para>
/// <para>
/// <b>One running transaction per terminal serial</b>, enforced here and not by
/// disabling a button: a double click, a retry from a second tab or a client that
/// simply repeats a POST must not put two sales on one terminal.
/// </para>
/// </summary>
public sealed class TransactionRegistry
{
    private readonly TransactionEngine _engine;
    private readonly TransactionRegistryOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, TransactionRun> _runs = new();
    private readonly Dictionary<string, string> _runningBySerial = new();

    public TransactionRegistry(TransactionEngine engine, TransactionRegistryOptions options, TimeProvider time)
    {
        _engine = engine;
        _options = options;
        _time = time;
    }

    public StartResult Start(TransactionRequest request, bool usesWebService, int responseTimeoutSeconds = 120)
    {
        lock (_gate)
        {
            Sweep();
            if (_runningBySerial.TryGetValue(request.TerminalSerial, out var existing)) return new StartResult.Busy(existing);

            var run = new TransactionRun(
                Guid.NewGuid().ToString("N"), request, usesWebService, _time.GetUtcNow(), responseTimeoutSeconds);
            _runs[run.Id] = run;
            Begin(run, request);
            return new StartResult.Started(run.Snapshot(_time.GetUtcNow()));
        }
    }

    public TransactionSnapshot? Find(string id)
    {
        lock (_gate)
        {
            Sweep();
            return _runs.GetValueOrDefault(id)?.Snapshot(_time.GetUtcNow());
        }
    }

    /// <summary>Runs the operator has not acknowledged, newest first: what a reloaded page picks back up.</summary>
    public IReadOnlyList<TransactionSnapshot> Pending()
    {
        lock (_gate)
        {
            Sweep();
            return _runs.Values.OrderByDescending(r => r.Started).Select(r => r.Snapshot(_time.GetUtcNow())).ToList();
        }
    }

    /// <summary>Stops waiting, like the WPF Cancel button. A no-op once the run has finished.</summary>
    public TransactionSnapshot? Cancel(string id)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var run)) return null;
            cts = run.Cts;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished between reading its token and using it: nothing left to stop.
        }

        return Find(id);
    }

    /// <summary>
    /// After an unknown outcome: look the transaction up by the reference it was sent
    /// with. The only follow-up offered — never a plain retry, which would charge the
    /// cardholder twice.
    /// </summary>
    public FollowUp Inquire(string id)
    {
        lock (_gate)
        {
            Sweep();
            if (!_runs.TryGetValue(id, out var run)) return new FollowUp.NotFound();
            if (run.State is not TransactionState.Failed { InquirableReference: { } reference }) return new FollowUp.NotApplicable();
            if (_runningBySerial.TryGetValue(run.TerminalSerial, out var existing)) return new FollowUp.Busy(existing);

            var inquiry = new TransactionRequest
            {
                Type = EcrTransactionType.Inquiry,
                TerminalSerial = run.TerminalSerial,
                Amount = null,
                OriginalReference = reference,
                // Blank so the SDK mints this inquiry's own merchantReference.
                MerchantReference = "",
            };
            run.Request = inquiry;
            Begin(run, inquiry);
            return new FollowUp.Started(run.Snapshot(_time.GetUtcNow()));
        }
    }

    /// <summary>
    /// Asks the terminal to put its receipt away. Does not change the run's state —
    /// the result dialog stays open.
    /// </summary>
    public async Task<CloseReceiptOutcome> CloseReceiptAsync(string id)
    {
        string serial;
        string originalMerchantReference;
        TransactionSnapshot snapshot;
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var run)) return new CloseReceiptOutcome.NotFound();
            if (run.State is not TransactionState.Completed and not TransactionState.Settled)
            {
                return new CloseReceiptOutcome.NotApplicable();
            }

            serial = run.TerminalSerial;
            originalMerchantReference = "";
            if (run.State is TransactionState.Completed completed
                && run.UsesWebService
                && (completed.Type == EcrTransactionType.Sale
                    || completed.Type == EcrTransactionType.Void
                    || completed.Type == EcrTransactionType.Refund))
            {
                originalMerchantReference = completed.Result.MerchantReference;
            }

            if (run.UsesWebService && string.IsNullOrWhiteSpace(originalMerchantReference))
            {
                return new CloseReceiptOutcome.NotApplicable(
                    "Close receipt needs the sale / void / refund reference");
            }

            snapshot = run.Snapshot(_time.GetUtcNow());
        }

        var message = await _engine.CloseTerminalReceiptAsync(serial, originalMerchantReference)
            .ConfigureAwait(false);
        return new CloseReceiptOutcome.Done(snapshot, message);
    }

    /// <summary>Fetches the e-receipt of an inquired transaction (Wi‑Fi and USB cable).</summary>
    public FollowUp RequestReceipt(string id)
    {
        TransactionState.Inquired current;
        TransactionRun run;
        TransactionSnapshot snapshot;
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out run!)) return new FollowUp.NotFound();
            if (run.State is not TransactionState.Inquired { Receipt: null, FetchingReceipt: false } inquired || run.UsesWebService)
            {
                return new FollowUp.NotApplicable();
            }

            current = inquired;
            run.BusySince = _time.GetUtcNow();
            Update(run, current with { FetchingReceipt = true });
            snapshot = run.Snapshot(_time.GetUtcNow());
        }

        // Read-only, so it may outlive the request that asked for it.
        _ = Task.Run(async () =>
        {
            EcrReceipt receipt;
            try
            {
                receipt = await _engine.FetchReceiptAsync(current, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                receipt = new EcrReceipt.Unavailable("", e.Message, "");
            }

            lock (_gate)
            {
                // The operator may have moved on while this was in flight.
                if (run.State is TransactionState.Inquired latest) Update(run, latest with { Receipt = receipt, FetchingReceipt = false });
            }
        });

        return new FollowUp.Started(snapshot);
    }

    /// <summary>
    /// The operator has read the result. A running transaction cannot be dismissed;
    /// returns false for it.
    /// </summary>
    public bool Acknowledge(string id)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var run)) return true;
            if (run.State.IsBusy) return false;
            _runs.Remove(id);
            return true;
        }
    }

    /// <summary>Must hold <c>_gate</c>. Claims the terminal and runs <paramref name="request"/> on a server-owned token.</summary>
    private void Begin(TransactionRun run, TransactionRequest request)
    {
        var cts = new CancellationTokenSource();
        run.Cts = cts;
        _runningBySerial[run.TerminalSerial] = run.Id;
        run.BusySince = _time.GetUtcNow();
        Update(run, new TransactionState.Checking());

        _ = Task.Run(async () =>
        {
            TransactionState final;
            try
            {
                final = await _engine.RunAsync(request, progress => Report(run, progress), cts.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // The engine answers every failure itself; this is the last line, so
                // that a run can never be left spinning.
                final = request.Type.MovesMoney()
                    ? new TransactionState.Failed($"Unexpected error: {e.Message}", TransactionEngine.NewReference())
                    : new TransactionState.Failed($"Unexpected error: {e.Message}");
            }

            lock (_gate)
            {
                // Result and release together: a poller that sees the outcome may
                // start the next one at once, and one that still sees "running"
                // must not find the terminal free.
                run.Cts = null;
                _runningBySerial.Remove(run.TerminalSerial);
                Update(run, final);
            }

            cts.Dispose();
        });
    }

    private void Report(TransactionRun run, TransactionState progress)
    {
        lock (_gate)
        {
            if (run.State.IsBusy) Update(run, progress);
        }
    }

    private void Update(TransactionRun run, TransactionState state)
    {
        run.State = state;
        run.Version++;
        run.Updated = _time.GetUtcNow();
    }

    /// <summary>Must hold <c>_gate</c>. Drops finished runs nobody acknowledged for too long.</summary>
    private void Sweep()
    {
        var cutoff = _time.GetUtcNow() - _options.FinishedRetention;
        foreach (var id in _runs.Values.Where(r => !r.State.IsBusy && r.Updated < cutoff).Select(r => r.Id).ToList())
        {
            _runs.Remove(id);
        }
    }
}
