using AmwalPay.Ecr;

namespace AmwalECR.Example.Core.Transaction;

/// <summary>What the transaction screen is currently showing.</summary>
public abstract record TransactionState
{
    private TransactionState()
    {
    }

    /// <summary>Nothing running; the screen is waiting for the operator.</summary>
    public sealed record Idle : TransactionState;

    /// <summary>Probing the terminal, before anything is sent to it.</summary>
    public sealed record Checking : TransactionState;

    /// <summary>Sent, waiting on the cardholder and the terminal.</summary>
    public sealed record InProgress : TransactionState;

    /// <summary>
    /// The terminal answered, approved or declined. The operation is kept
    /// alongside the result: what is worth showing differs by type, and the result
    /// itself does not say which was asked for.
    /// </summary>
    public sealed record Completed(EcrResult Result, EcrTransactionType Type) : TransactionState;

    /// <summary>
    /// An inquiry was answered. Kept apart from <see cref="Completed"/> because it
    /// reports on a transaction rather than one just performed — nothing moved.
    /// <para>
    /// <paramref name="Request"/> is kept so the receipt can be asked for from
    /// here: it names the same transaction, and re-keying it to fetch a receipt
    /// would be absurd.
    /// </para>
    /// </summary>
    /// <param name="Receipt">The e-receipt, once fetched. Null until the operator asks for one.</param>
    /// <param name="RecoveredFromFailure">
    /// True when this finding came from the SDK's automatic inquiry after a sale
    /// whose answer was lost, rather than from an inquiry the operator ran.
    /// </param>
    public sealed record Inquired(
        EcrInquiry Inquiry,
        TransactionRequest Request,
        EcrReceipt? Receipt = null,
        bool FetchingReceipt = false,
        bool RecoveredFromFailure = false) : TransactionState;

    /// <summary>
    /// The exchange itself failed: unreachable, timed out, malformed, or an answer
    /// that could not be trusted.
    /// <para>
    /// <paramref name="InquirableReference"/> is set when the outcome is genuinely
    /// <b>unknown</b> — the request went out and no answer came back, so the
    /// transaction may well have completed. The screen offers to look it up by that
    /// reference, which is the only thing to do that is not a second charge. It is
    /// null when nothing was ever sent, and then there is nothing to ask about.
    /// </para>
    /// </summary>
    /// <param name="Note">Extra context worth showing under the message, e.g. what an automatic check found.</param>
    public sealed record Failed(
        string Message,
        string? InquirableReference = null,
        string? Note = null) : TransactionState;

    public bool IsBusy => this is Checking or InProgress;

    /// <summary>A state the operator has to acknowledge before doing anything else.</summary>
    public bool IsResult => this is Completed or Inquired or Failed;
}

/// <summary>One operation, as the screen asks for it.</summary>
public sealed record TransactionRequest
{
    public required EcrTransactionType Type { get; init; }

    public required string TerminalSerial { get; init; }

    /// <summary>Null for operations that take the original transaction's amount.</summary>
    public decimal? Amount { get; init; }

    public string ReceiptNumber { get; init; } = "";

    public string OriginalTerminalId { get; init; } = "";

    /// <summary><c>yyyyMMdd</c>, for operations that look an original up by date.</summary>
    public string TransactionDate { get; init; } = "";

    /// <summary>
    /// Look the original up by the reference the till named it with, rather than
    /// by its receipt number.
    /// <para>
    /// The only route open after an outcome that never arrived: a receipt number
    /// comes back <em>in</em> the answer, so a till that lost the answer has none
    /// to quote, while the reference it chose is still its own.
    /// </para>
    /// </summary>
    public string OriginalReference { get; init; } = "";

    /// <summary>Optional reference for a new sale; blank lets the app generate one.</summary>
    public string MerchantReference { get; init; } = "";

    /// <summary>Whether this names its original by reference rather than receipt number.</summary>
    public bool LooksUpByReference => !string.IsNullOrWhiteSpace(OriginalReference);
}
