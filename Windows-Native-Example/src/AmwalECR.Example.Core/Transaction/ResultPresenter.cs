using System.Globalization;
using System.Text.Json;
using AmwalPay.Ecr;

namespace AmwalECR.Example.Core.Transaction;

public enum ResultTone
{
    Approved,
    Declined,
}

public sealed record ResultField(string Label, string Value);

/// <summary>Everything the result dialog shows, decided here so it can be tested.</summary>
public sealed record ResultDialogModel
{
    public required string Headline { get; init; }

    public required ResultTone Tone { get; init; }

    /// <summary>The figure under the headline, empty when there is none.</summary>
    public string Amount { get; init; } = "";

    public string? Message { get; init; }

    public IReadOnlyList<ResultField> Fields { get; init; } = Array.Empty<ResultField>();

    /// <summary>Short informational lines, e.g. "Can be voided".</summary>
    public IReadOnlyList<string> Hints { get; init; } = Array.Empty<string>();

    /// <summary>Extra context under the message.</summary>
    public string? Note { get; init; }

    public string? RawPayload { get; init; }

    /// <summary>
    /// Set when the outcome is genuinely unknown: the reference to look the
    /// transaction up by. It is the only safe next step; a plain retry is never offered.
    /// </summary>
    public string? InquirableReference { get; init; }

    /// <summary>Whether the e-receipt QR can be requested from this dialog.</summary>
    public bool OffersReceipt { get; init; }

    /// <summary>Why no e-receipt is offered, when the transport cannot provide one.</summary>
    public string? ReceiptNote { get; init; }

    public bool IsUnknownOutcome => InquirableReference != null;
}

/// <summary>
/// How a transaction ended: the outcome at a glance, the fields that matter to a
/// cashier, and the terminal's full answer tucked away for when it does not add up.
/// </summary>
public static class ResultPresenter
{
    public const string PartialApprovalHeadline = "Partially approved";

    public static ResultDialogModel? For(TransactionState state, bool usesWebService) => state switch
    {
        TransactionState.Completed c => ForResult(c.Result, c.Type),
        TransactionState.Inquired i => ForInquiry(i, receiptSupported: !usesWebService, webService: usesWebService),
        TransactionState.Failed f => ForFailure(f),
        _ => null,
    };

    public static ResultDialogModel ForResult(EcrResult result, EcrTransactionType type)
    {
        switch (result)
        {
            case EcrResult.Approved approved:
                // A partial approval is an approval the till must act on: the
                // customer still owes the difference, so it is called out rather
                // than shown as a plain "Approved" with a quietly smaller figure.
                var fields = new List<ResultField>();
                // A void carries no authorisation of its own — the terminal returns
                // a placeholder 000000 — so showing one would suggest an approval
                // that never took place.
                if (type != EcrTransactionType.Void) fields.Add(new ResultField("Auth code", approved.AuthCode));
                fields.Add(new ResultField("Card", approved.MaskedPan));
                fields.Add(new ResultField("Reference", approved.MerchantReference));
                return new ResultDialogModel
                {
                    Headline = approved.PartialApproval ? PartialApprovalHeadline : "Approved",
                    Tone = ResultTone.Approved,
                    Amount = approved.Amount,
                    Message = approved.PartialApproval ? PartialMessage(approved.Amount, approved.RequestedAmount) : null,
                    Fields = fields.Where(f => f.Value.Length != 0).ToList(),
                    RawPayload = Pretty(approved.Raw),
                };

            case EcrResult.Declined declined:
                return new ResultDialogModel
                {
                    Headline = "Declined",
                    Tone = ResultTone.Declined,
                    Message = OperationMessage(declined.Raw, declined.Reason),
                    RawPayload = declined.Raw.Length != 0 ? Pretty(declined.Raw) : null,
                };

            case EcrResult.Failed failed:
                return ForFailure(new TransactionState.Failed(failed.Failure.Message));

            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    /// <summary>
    /// What the terminal knows about an earlier transaction.
    /// <para>
    /// The headline is the transaction's own status, not the inquiry's: an inquiry
    /// that finds a declined sale succeeded, and the till needs to read "Declined".
    /// </para>
    /// </summary>
    public static ResultDialogModel ForInquiry(TransactionState.Inquired state, bool receiptSupported, bool webService)
    {
        switch (state.Inquiry)
        {
            case EcrInquiry.Failed failed:
                return ForFailure(new TransactionState.Failed(failed.Failure.Message));

            case EcrInquiry.NotFound notFound:
                return new ResultDialogModel
                {
                    Headline = "Not found",
                    Tone = ResultTone.Declined,
                    Message = OperationMessage(notFound.Raw, notFound.Reason),
                    Note = TransactionOutcomes.InquiryNote(notFound, state.Request, webService),
                    RawPayload = Pretty(notFound.Raw),
                };

            case EcrInquiry.Found found:
                var txn = found.Transaction;
                var settled = string.Equals(txn.Status, "Approved", StringComparison.OrdinalIgnoreCase);
                // A partial approval read back from an inquiry is the same fact the
                // sale reported, and has to read the same: showing the requested
                // amount alone said "Approved 961.100" for a transaction where
                // 50.000 was taken.
                var partial = settled && txn.PartialApproval;
                var hints = new List<string>();
                if (txn.IsRefunded) hints.Add("Already refunded");
                if (txn.CanVoid) hints.Add("Can be voided");
                if (txn.CanRefund) hints.Add("Can be refunded");

                string? message = null;
                if (partial) message = PartialMessage(txn.AuthorizedAmount, txn.Amount);
                else if (!settled) message = OperationMessage(found.Raw, txn.Status);

                return new ResultDialogModel
                {
                    Headline = partial ? PartialApprovalHeadline : txn.Status.Length == 0 ? "Found" : txn.Status,
                    Tone = settled ? ResultTone.Approved : ResultTone.Declined,
                    Amount = partial ? txn.AuthorizedAmount : txn.Amount,
                    Message = message,
                    // What a till needs to recognise the transaction. The rest of the
                    // record is a tap away under the raw response.
                    Fields = new[]
                    {
                        new ResultField("Receipt number", txn.Stan),
                        new ResultField("Type", txn.Type),
                        new ResultField("Card", txn.MaskedPan),
                        new ResultField("Date/Time", ReadableTime(txn.TransactionTime)),
                    }.Where(f => f.Value.Length != 0).ToList(),
                    Hints = hints,
                    Note = state.RecoveredFromFailure
                        ? "The terminal's answer to this request was lost, but it was asked automatically " +
                          "and knows this transaction. Do not send it again."
                        : null,
                    RawPayload = Pretty(found.Raw),
                    OffersReceipt = receiptSupported,
                    ReceiptNote = receiptSupported ? null : "The e-receipt QR is available over Wi\u2011Fi and USB cable.",
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    /// <summary>The exchange itself failed: unreachable, timed out, malformed.</summary>
    public static ResultDialogModel ForFailure(TransactionState.Failed failed) =>
        new()
        {
            // Not "Declined": no answer came back, so nobody knows yet. The wording
            // has to stop an operator concluding the payment did not happen.
            Headline = failed.InquirableReference == null ? "Not completed" : "Outcome unknown",
            Tone = ResultTone.Declined,
            Message = failed.Message,
            Note = failed.Note,
            InquirableReference = failed.InquirableReference,
        };

    public const string UnknownOutcomeWarning =
        "The transaction may have completed. Check before taking payment again — " +
        "sending it a second time would charge the cardholder twice.";

    public static string PartialMessage(string approved, string requested) =>
        $"Only {approved} of {requested} was approved. Collect the difference by another means.";

    /// <summary>
    /// The backend records times as <c>2026-08-09T16:58:16.804271</c>, which is not
    /// what anyone at a till wants to read. Anything else is shown as it arrived.
    /// </summary>
    public static string ReadableTime(string value)
    {
        var head = value.Length > 19 ? value[..19] : value;
        return DateTime.TryParseExact(head, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)
            : value;
    }

    private static string OperationMessage(string? raw, string fallback) =>
        EcrWireResponse.DisplayMessageFromRaw(raw ?? "", fallback);

    /// <summary>Indents JSON for reading; anything that is not JSON is returned as it arrived.</summary>
    public static string Pretty(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (JsonException)
        {
            return raw;
        }
    }
}
