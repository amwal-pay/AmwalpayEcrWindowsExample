using System.Text.Json.Nodes;
using AmwalPay.Ecr;

namespace AmwalECR.Example.Core.Transaction;

/// <summary>
/// Turns what the SDK returned into what the screen shows. Pure, so the
/// payment-safety rules — an unknown outcome is never a refusal, and never a
/// reason to retry — can be tested without a terminal.
/// </summary>
public static class TransactionOutcomes
{
    /// <summary>
    /// The Web Service HTTP layer reports a transport error (timeout, no route,
    /// connection reset) as a synthesized decline with this response code. It is
    /// not a decline: the request may have reached the backend.
    /// </summary>
    public const string NoResponseCode = "NO_RESPONSE";

    public static TransactionState FromResult(EcrResult result, TransactionRequest request)
    {
        switch (result)
        {
            case EcrResult.Failed failed:
                return FromFailed(failed, request);

            // The Web Service HTTP layer has no exception path for a lost answer.
            case EcrResult.Declined { ResponseCode: NoResponseCode } lost:
                return new TransactionState.Failed(
                    $"No answer from the Web Service: {lost.Reason}",
                    InquirableReferenceOrNull(lost.MerchantReference));

            // The terminal answered and said it does not actually know. Reading that
            // as a refusal and selling again is the double charge this app exists
            // to avoid.
            case EcrResult.Declined { NextStep: NextStep.InquireByMerchantReference } unsure:
                return new TransactionState.Failed(
                    unsure.Reason,
                    InquirableReferenceOrNull(unsure.MerchantReference),
                    "The terminal reports that it does not know whether this completed.");

            default:
                return new TransactionState.Completed(result, request.Type);
        }
    }

    private static TransactionState FromFailed(EcrResult.Failed failed, TransactionRequest request)
    {
        // The SDK asked the terminal what became of it and it knew: no longer an
        // unknown outcome, only a delivery that failed. Shown like an inquiry.
        if (failed.Recovered is EcrInquiry.Found found)
        {
            return new TransactionState.Inquired(
                found,
                request with
                {
                    Type = EcrTransactionType.Inquiry,
                    Amount = null,
                    ReceiptNumber = "",
                    OriginalReference = failed.MerchantReference,
                    // The inquiry's own id stays blank so the SDK mints one: copying
                    // the sale's reference here would put the same value in both
                    // wire fields and break Hub correlation (ECR-simulator 1.0.8).
                    MerchantReference = "",
                },
                RecoveredFromFailure: true);
        }

        var note = failed.Recovered switch
        {
            EcrInquiry.NotFound =>
                "The terminal was asked automatically what became of it and has no record yet. " +
                "That does not prove nothing was charged — look it up again before taking payment again.",
            EcrInquiry.Failed =>
                "The automatic check with the terminal could not be completed either.",
            _ => null,
        };

        // Only when the request actually went out. An unreachable terminal means
        // nothing was sent, so there is nothing to ask about.
        var reference = failed.Failure.OutcomeUnknown
            ? InquirableReferenceOrNull(failed.MerchantReference)
            : null;

        return new TransactionState.Failed(failed.Failure.Message, reference, note);
    }

    public static TransactionState FromInquiry(EcrInquiry inquiry, TransactionRequest request)
    {
        switch (inquiry)
        {
            case EcrInquiry.Failed failed:
                return new TransactionState.Failed(
                    failed.Failure.Message,
                    RetryableReference(request),
                    "An inquiry only reads and changes nothing, so it is safe to try again.");

            // A Web Service transport error arrives as "not found" with this code;
            // showing "Not found" would read as "it did not happen".
            case EcrInquiry.NotFound notFound when IsNoResponse(notFound.Raw):
                return new TransactionState.Failed(
                    $"No answer from the Web Service: {notFound.Reason}",
                    RetryableReference(request),
                    "An inquiry only reads and changes nothing, so it is safe to try again.");

            default:
                return new TransactionState.Inquired(inquiry, request);
        }
    }

    /// <summary>Extra text for an inquiry dialog, or null.</summary>
    public static string? InquiryNote(EcrInquiry inquiry, TransactionRequest request, bool webService)
    {
        _ = request;
        if (inquiry is EcrInquiry.NotFound && webService)
        {
            return "The Web Service did not return this transaction. That does not prove nothing was " +
                   "charged — check the terminal's receipt or the merchant portal before taking payment again.";
        }
        return null;
    }

    public static bool IsNoResponse(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        try
        {
            return JsonNode.Parse(raw) is JsonObject json && EcrWireResponse.Parse(json).ResponseCode == NoResponseCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// When an inquiry that was looking a transaction up BY REFERENCE fails, the reference has
    /// to stay with the result. It is usually one the app generated for a lost sale, so it is
    /// in no form field and nowhere the operator can retype it from: dropping it would leave
    /// "safe to try again" with nothing to try, and the transaction's outcome still unknown.
    /// An inquiry by receipt number has no such reference and needs none.
    /// </summary>
    private static string? RetryableReference(TransactionRequest request) =>
        request.LooksUpByReference ? request.OriginalReference.Trim() : null;

    /// <summary>
    /// A reference to look the transaction up by, or null when there is none — an
    /// inquiry needs something to name.
    /// </summary>
    private static string? InquirableReferenceOrNull(string reference) =>
        string.IsNullOrWhiteSpace(reference) ? null : reference;

    /// <summary>
    /// What to tell the operator when the terminal cannot be reached, before
    /// anything is sent.
    /// </summary>
    public static string UnreachableMessage(string endpoint, int port, string? error)
    {
        var text = $"{endpoint} is not reachable";
        if (!string.IsNullOrEmpty(error)) text += $"\n({error})";
        return text + "\n\n" +
               "Check:\n" +
               "• POS app is in ECR mode (Wi‑Fi) and shows its IP under the card logos\n" +
               $"• Registered IP matches that address (port {port})\n" +
               "• Serial number matches the terminal you selected\n" +
               "• This PC and the terminal are on the same network, and the network profile is Private\n" +
               "• If the terminal runs on this PC (fake_pos_server), try IP 127.0.0.1";
    }
}
