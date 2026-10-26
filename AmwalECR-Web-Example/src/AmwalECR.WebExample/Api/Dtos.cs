using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Services;
using AmwalECR.Example.Core.Terminals;
using AmwalECR.Example.Core.Transaction;
using AmwalECR.WebExample.Services;
using AmwalPay.Ecr;

namespace AmwalECR.WebExample.Api;

/// <summary>What the API sends for a validation failure: per-field messages, as the WPF form shows them.</summary>
public sealed record FieldErrors(IReadOnlyDictionary<string, string> Errors, string? Message = null);

public sealed record ErrorBody(string Error, string? TransactionId = null);

// Terminals ---------------------------------------------------------------------------

public sealed record TerminalView(
    string SerialNumber,
    string Name,
    int EcrMode,
    string ModeLabel,
    string IpAddress,
    int Port,
    string TerminalId,
    string MerchantId,
    string ComPort,
    string DisplayName,
    string ConnectionSummary,
    bool Supported)
{
    public static TerminalView From(Terminal t) => new(
        t.SerialNumber, t.Name, t.EcrModeValue, t.ModeLabel, t.IpAddress, t.Port, t.TerminalId, t.MerchantId,
        t.ComPort, t.DisplayName, t.ConnectionSummary(), t.Mode.IsSupportedInExample());
}

/// <summary>The terminal edit form as typed: everything is text, validation is Core's.</summary>
public sealed record TerminalForm(
    string? Name, string? SerialNumber, int? EcrMode, string? IpAddress, string? Port, string? TerminalId, string? MerchantId, string? ComPort);

/// <summary>The config card: what the SDK plan makes of the selected terminal, before anyone pays.</summary>
public sealed record ConfigView(
    string SerialNumber,
    string ModeLabel,
    string TransportLabel,
    string ConnectionSummary,
    string KeyLabel,
    bool KeyConfigured,
    string KeyStatus,
    IReadOnlyList<string> Issues,
    bool IsReady,
    bool IsSupported,
    bool UsesWebService,
    bool SupportsSettlement)
{
    public static ConfigView From(SelectedTerminalConfig c) => new(
        c.Terminal.SerialNumber,
        c.ModeLabel,
        c.TransportLabel,
        c.ConnectionSummary,
        c.HashKeyLabel,
        c.HashKeyConfigured,
        // Core's HashKeyStatus also says how many characters the key has; the web app
        // sends nothing about a stored key beyond whether one is set.
        c.HashKeyConfigured ? "configured" : "not configured — set in Settings",
        c.Issues,
        c.IsReady,
        c.IsSupported,
        c.UsesWebService,
        c.Mode.IsSupportedInExample());
}

// Settings ----------------------------------------------------------------------------

/// <summary>
/// The settings the browser may see. Deliberately no key value: only whether one is
/// stored, and its label.
/// </summary>
public sealed record SettingsView(
    string Environment,
    KeyView Wifi,
    KeyView WebService,
    string? Notice);

public sealed record KeyView(string Label, bool Configured);

public sealed record EnvironmentBody(string? Environment);

public sealed record KeyBody(string? Value);

// Transactions ------------------------------------------------------------------------

/// <summary>The transaction form as typed — validated by <see cref="TransactionFormState.Validated"/>.</summary>
/// <summary>Manual settlement: the selected terminal, nothing else. The page confirms first.</summary>
public sealed record SettlementBody(string? TerminalSerial);

public sealed record TransactionFormBody(
    string? TerminalSerial,
    string? Type,
    string? AmountDigits,
    string? ReceiptNumber,
    string? OriginalTerminalId,
    string? OriginalDate,
    bool UseOtherTerminal,
    bool LookUpByReference,
    string? OriginalReference,
    string? MerchantReference);

public sealed record ReceiptView(bool ShowButton, bool Fetching, string? Url, string? Problem);

/// <summary><see cref="ResultDialogModel"/> plus the receipt bits the WPF dialog view-model adds.</summary>
public sealed record DialogView(
    string Headline,
    string Tone,
    string Amount,
    string? Message,
    string? Note,
    IReadOnlyList<ResultField> Fields,
    IReadOnlyList<string> Hints,
    string? RawPayload,
    bool IsUnknownOutcome,
    string? InquirableReference,
    string? UnknownOutcomeWarning,
    bool OffersReceipt,
    string? ReceiptNote,
    ReceiptView Receipt,
    bool CanCloseReceipt);

public sealed record TransactionView(
    string Id,
    string TerminalSerial,
    string Operation,
    string State, // idle | checking | inProgress | completed | inquired | failed
    bool Busy,
    bool IsResult,
    string StatusText,
    long Version,
    DialogView? Dialog,
    int TimeoutSeconds = 120,
    int? RemainingSeconds = null)
{
    public static TransactionView From(TransactionSnapshot run)
    {
        var state = run.State;
        var model = ResultPresenter.For(state, run.UsesWebService);

        DialogView? dialog = null;
        if (model != null)
        {
            var inquired = state as TransactionState.Inquired;
            var receipt = inquired?.Receipt;
            dialog = new DialogView(
                model.Headline,
                model.Tone == ResultTone.Approved ? "approved" : "declined",
                model.Amount,
                model.Message,
                model.Note,
                model.Fields,
                model.Hints,
                model.RawPayload,
                model.IsUnknownOutcome,
                model.InquirableReference,
                model.IsUnknownOutcome ? ResultPresenter.UnknownOutcomeWarning : null,
                model.OffersReceipt,
                model.ReceiptNote,
                new ReceiptView(
                    ShowButton: model.OffersReceipt && inquired is { Receipt: null, FetchingReceipt: false },
                    Fetching: inquired?.FetchingReceipt == true,
                    Url: (receipt as EcrReceipt.Ready)?.Url,
                    Problem: receipt switch
                    {
                        EcrReceipt.Unavailable u => u.Reason.Length != 0 ? u.Reason : "No receipt is available for this transaction",
                        EcrReceipt.Failed f => f.Failure.Message,
                        _ => null,
                    }),
                // A finished sale, or a settlement summary the terminal is still showing.
                CanCloseReceipt: state is TransactionState.Completed or TransactionState.Settled);
        }

        return new TransactionView(
            run.Id,
            run.TerminalSerial,
            run.Request.Type.DisplayName(),
            state switch
            {
                TransactionState.Idle => "idle",
                TransactionState.Checking => "checking",
                TransactionState.InProgress => "inProgress",
                TransactionState.Completed => "completed",
                TransactionState.Inquired => "inquired",
                TransactionState.Failed => "failed",
                TransactionState.Settled => "settled",
                _ => "idle",
            },
            state.IsBusy,
            state.IsResult,
            state is TransactionState.Checking ? "Checking the terminal…" : "Waiting for the POS terminal…",
            run.Version,
            dialog,
            run.TimeoutSeconds,
            run.RemainingSeconds);
    }
}

// Meta --------------------------------------------------------------------------------

/// <summary>Which fields of the transaction form exist, for one combination of the two switches.</summary>
public sealed record FieldLayout(
    bool Amount,
    bool MerchantReference,
    bool ReferenceSwitch,
    bool OriginalReference,
    bool ReceiptNumber,
    bool OriginalDate,
    bool OtherTerminalSwitch,
    bool OriginalTerminal);

public sealed record OperationMeta(
    string Value,
    string Label,
    bool RequiresAmount,
    bool RequiresOriginalStan,
    bool RequiresOriginalDate,
    bool AllowsOtherTerminal,
    bool MovesMoney,
    string AmountHelp,
    string StartButtonText,
    // Keyed "<otherTerminal><byReference>" as 0/1, e.g. "10".
    IReadOnlyDictionary<string, FieldLayout> Layouts);

public sealed record ModeMeta(int Value, string Label, bool IpBased, bool Supported, string Hint, string Note);

public sealed record AmountMeta(string Min, string Max, int FractionDigits, int MaxDigits);

public sealed record Meta(
    IReadOnlyList<OperationMeta> Operations,
    IReadOnlyList<ModeMeta> Modes,
    IReadOnlyList<string> Environments,
    AmountMeta Amount,
    int MerchantReferenceMaxLength,
    int ReceiptNumberMaxLength,
    int DefaultPort,
    string Today);

public sealed record LogView(string Text, int Count, int Capacity);
