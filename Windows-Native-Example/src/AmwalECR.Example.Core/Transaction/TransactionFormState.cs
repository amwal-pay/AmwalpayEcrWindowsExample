using System.Globalization;
using AmwalECR.Example.Core.Ui;
using AmwalPay.Ecr;

namespace AmwalECR.Example.Core.Transaction;

/// <summary>
/// What the operator has entered, and what is wrong with it.
/// <para>
/// Which fields apply is decided by <see cref="EcrTransactionType"/>, so showing a
/// field and requiring it cannot disagree. Validation answers only "is this
/// complete enough to send" — whether the terminal will accept it needs the
/// payment backend, and only the terminal can ask.
/// </para>
/// </summary>
public sealed record TransactionFormState
{
    public const int MerchantReferenceMaxLength = 32;
    public const int ReceiptNumberMaxLength = 6;

    private const string WireDateFormat = "yyyyMMdd";
    private const string DisplayDateFormat = "dd MMM yyyy";

    public EcrTransactionType Type { get; init; } = EcrTransactionType.Sale;

    /// <summary>Raw keyed digits; the amount field renders them as 0.000.</summary>
    public string AmountDigits { get; init; } = "";

    public string ReceiptNumber { get; init; } = "";

    public string OriginalTerminalId { get; init; } = "";

    /// <summary>Date of the original transaction, defaulting to today.</summary>
    public DateTime OriginalDate { get; init; } = DateTime.Today;

    public bool UseOtherTerminal { get; init; }

    /// <summary>
    /// Look the transaction up by the reference this till named it with, rather
    /// than by the receipt number.
    /// <para>
    /// Offered for an inquiry only, and it is the case that matters most: after a
    /// sale whose answer never arrived there is no receipt number to type, because
    /// that number was in the answer.
    /// </para>
    /// </summary>
    public bool LookUpByReference { get; init; }

    public string OriginalReference { get; init; } = "";

    /// <summary>Optional reference for a new sale; blank lets the app generate one.</summary>
    public string MerchantReference { get; init; } = "";

    public FormErrors Errors { get; init; } = new();

    public bool ShowOtherTerminalSwitch => Type.AllowsOtherTerminal();

    public bool ShowOriginalTerminal => Type.AllowsOtherTerminal() && UseOtherTerminal;

    /// <summary>Only an inquiry may look up by reference — see <see cref="LookUpByReference"/>.</summary>
    public bool ShowReferenceSwitch => Type == EcrTransactionType.Inquiry;

    /// <summary>Optional till reference for a new sale.</summary>
    public bool ShowMerchantReference => Type == EcrTransactionType.Sale;

    public bool LooksUpByReference => ShowReferenceSwitch && LookUpByReference;

    /// <summary>The receipt number field gives way to the reference field.</summary>
    public bool ShowReceiptNumber => Type.RequiresOriginalStan() && !LooksUpByReference;

    /// <summary>
    /// A reference is unique in its own right, so the date is not needed to
    /// disambiguate it the way it is for a receipt number.
    /// </summary>
    public bool ShowOriginalDate => Type.RequiresOriginalDate() && !LooksUpByReference;

    /// <summary>The date in the format the terminal looks originals up by.</summary>
    public string OriginalDateOnWire => OriginalDate.ToString(WireDateFormat, CultureInfo.InvariantCulture);

    public string OriginalDateLabel => OriginalDate.ToString(DisplayDateFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Validates and returns what to send, or the same state carrying the reasons
    /// it cannot be sent.
    /// </summary>
    public (TransactionFormState Form, TransactionRequest? Request) Validated(string terminalSerial)
    {
        var amount = AmountEntry.AmountOf(AmountDigits);

        string? amountError;
        if (!Type.RequiresAmount()) amountError = null;
        else if (amount == 0m) amountError = "Required field";
        else if (amount < AmountLimits.Min)
            amountError = $"minimum amount allowed is {AmountLimits.Formatted(AmountLimits.Min)}";
        else if (amount > AmountLimits.Max)
            amountError = $"maximum amount allowed is {AmountLimits.Formatted(AmountLimits.Max)}";
        else amountError = null;

        var receiptError = ShowReceiptNumber && string.IsNullOrWhiteSpace(ReceiptNumber)
            ? "Enter the receipt number of the transaction to act on"
            : null;
        var referenceError = LooksUpByReference && string.IsNullOrWhiteSpace(OriginalReference)
            ? "Enter the reference the transaction was sent with"
            : null;
        var merchantRefError = ShowMerchantReference ? MerchantReferenceError(MerchantReference) : null;
        var terminalError = ShowOriginalTerminal && string.IsNullOrWhiteSpace(OriginalTerminalId)
            ? "Enter the terminal the original was taken on"
            : null;

        var errors = new FormErrors(amountError, receiptError, terminalError, referenceError, merchantRefError);
        if (errors.Any) return (this with { Errors = errors }, null);

        var request = new TransactionRequest
        {
            Type = Type,
            TerminalSerial = terminalSerial,
            Amount = Type.RequiresAmount() ? amount : null,
            ReceiptNumber = LooksUpByReference ? "" : ReceiptNumber,
            OriginalTerminalId = ShowOriginalTerminal ? OriginalTerminalId : "",
            TransactionDate = ShowOriginalDate ? OriginalDateOnWire : "",
            // By-reference inquiry: originalMerchantReference = what to look up;
            // merchantReference = this inquiry's own id (blank → SDK generates).
            // Never copy the original into merchantReference — that made both
            // wire fields the same and broke Hub correlation.
            OriginalReference = LooksUpByReference ? OriginalReference.Trim() : "",
            MerchantReference = LooksUpByReference
                ? ""
                : ShowMerchantReference ? MerchantReference.Trim() : "",
        };
        return (this with { Errors = new FormErrors() }, request);
    }

    /// <summary>The same rule the SDK enforces, so the operator hears it before sending.</summary>
    public static string? MerchantReferenceError(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > MerchantReferenceMaxLength)
        {
            return $"A merchant reference is at most {MerchantReferenceMaxLength} characters";
        }
        // 0x21..0x7E is printable ASCII with the space left out.
        if (trimmed.Any(c => c == ' ' || c == '&' || c == '=' || c < 0x21 || c > 0x7E))
        {
            return "Use printable ASCII without spaces, '&' or '='";
        }
        return null;
    }
}

public sealed record FormErrors(
    string? Amount = null,
    string? ReceiptNumber = null,
    string? OriginalTerminalId = null,
    string? OriginalReference = null,
    string? MerchantReference = null)
{
    public bool Any =>
        Amount != null || ReceiptNumber != null || OriginalTerminalId != null
        || OriginalReference != null || MerchantReference != null;
}
