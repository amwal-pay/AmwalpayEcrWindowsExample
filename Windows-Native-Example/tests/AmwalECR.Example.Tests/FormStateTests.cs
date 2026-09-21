using AmwalECR.Example.Core.Transaction;
using AmwalPay.Ecr;
using Xunit;

namespace AmwalECR.Example.Tests;

public class TransactionFormStateTests
{
    private const string Serial = "TW123";
    private static readonly DateTime Day = new(2026, 8, 9);

    private static TransactionFormState Form(EcrTransactionType type) =>
        new() { Type = type, OriginalDate = Day };

    [Theory]
    [InlineData(EcrTransactionType.Sale, true, false, false)]
    [InlineData(EcrTransactionType.Void, false, true, false)]
    [InlineData(EcrTransactionType.Refund, true, true, true)]
    [InlineData(EcrTransactionType.Inquiry, false, true, true)]
    public void The_fields_shown_follow_the_type_flags(EcrTransactionType type, bool amount, bool receipt, bool date)
    {
        var form = Form(type);

        Assert.Equal(amount, type.RequiresAmount());
        Assert.Equal(receipt, form.ShowReceiptNumber);
        Assert.Equal(date, form.ShowOriginalDate);
        Assert.Equal(type != EcrTransactionType.Sale, form.ShowOtherTerminalSwitch);
        Assert.Equal(type == EcrTransactionType.Sale, form.ShowMerchantReference);
        Assert.Equal(type == EcrTransactionType.Inquiry, form.ShowReferenceSwitch);
    }

    [Fact]
    public void Looking_up_by_reference_swaps_the_receipt_and_date_fields_for_the_reference()
    {
        var form = Form(EcrTransactionType.Inquiry) with { LookUpByReference = true };

        Assert.True(form.LooksUpByReference);
        Assert.False(form.ShowReceiptNumber);
        Assert.False(form.ShowOriginalDate);
    }

    [Fact]
    public void Only_an_inquiry_can_look_up_by_reference()
    {
        var form = Form(EcrTransactionType.Refund) with { LookUpByReference = true };

        Assert.False(form.LooksUpByReference);
        Assert.True(form.ShowReceiptNumber);
    }

    [Fact]
    public void The_original_terminal_needs_the_switch_and_a_type_that_allows_it()
    {
        Assert.False((Form(EcrTransactionType.Void) with { UseOtherTerminal = false }).ShowOriginalTerminal);
        Assert.True((Form(EcrTransactionType.Void) with { UseOtherTerminal = true }).ShowOriginalTerminal);
        Assert.False((Form(EcrTransactionType.Sale) with { UseOtherTerminal = true }).ShowOriginalTerminal);
    }

    [Fact]
    public void The_date_defaults_to_today_and_goes_on_the_wire_as_yyyyMMdd()
    {
        Assert.Equal(DateTime.Today, new TransactionFormState().OriginalDate);
        Assert.Equal("20260809", Form(EcrTransactionType.Refund).OriginalDateOnWire);
        Assert.Equal("09 Aug 2026", Form(EcrTransactionType.Refund).OriginalDateLabel);
    }

    [Fact]
    public void An_empty_sale_amount_is_required()
    {
        var (form, request) = Form(EcrTransactionType.Sale).Validated(Serial);

        Assert.Null(request);
        Assert.Equal("Required field", form.Errors.Amount);
    }

    [Theory]
    [InlineData("9", "minimum amount allowed is 0.010")]
    [InlineData("100000001", "maximum amount allowed is 100000.000")]
    public void The_amount_must_be_within_the_limits(string digits, string error)
    {
        var (form, request) = (Form(EcrTransactionType.Sale) with { AmountDigits = digits }).Validated(Serial);

        Assert.Null(request);
        Assert.Equal(error, form.Errors.Amount);
    }

    [Theory]
    [InlineData("10", 0.010)]
    [InlineData("1234", 1.234)]
    [InlineData("100000000", 100000.000)]
    public void An_amount_within_the_limits_is_sent(string digits, double expected)
    {
        var (form, request) = (Form(EcrTransactionType.Sale) with { AmountDigits = digits }).Validated(Serial);

        Assert.False(form.Errors.Any);
        Assert.NotNull(request);
        Assert.Equal(EcrTransactionType.Sale, request!.Type);
        Assert.Equal(Serial, request.TerminalSerial);
        Assert.Equal((decimal)expected, request.Amount);
    }

    [Fact]
    public void A_void_needs_a_receipt_number_but_no_amount()
    {
        var (form, request) = Form(EcrTransactionType.Void).Validated(Serial);
        Assert.Null(request);
        Assert.Null(form.Errors.Amount);
        Assert.Equal("Enter the receipt number of the transaction to act on", form.Errors.ReceiptNumber);

        (form, request) = (Form(EcrTransactionType.Void) with { ReceiptNumber = "24", AmountDigits = "5000" }).Validated(Serial);
        Assert.NotNull(request);
        Assert.Null(request!.Amount);
        Assert.Equal("24", request.ReceiptNumber);
        Assert.Equal("", request.TransactionDate);
    }

    [Fact]
    public void A_refund_carries_amount_receipt_number_and_date()
    {
        var (_, request) = (Form(EcrTransactionType.Refund) with { AmountDigits = "500", ReceiptNumber = "7" }).Validated(Serial);

        Assert.NotNull(request);
        Assert.Equal(0.5m, request!.Amount);
        Assert.Equal("7", request.ReceiptNumber);
        Assert.Equal("20260809", request.TransactionDate);
    }

    [Fact]
    public void A_refund_missing_everything_reports_every_missing_field()
    {
        var (form, request) = Form(EcrTransactionType.Refund).Validated(Serial);

        Assert.Null(request);
        Assert.NotNull(form.Errors.Amount);
        Assert.NotNull(form.Errors.ReceiptNumber);
    }

    [Fact]
    public void A_different_terminal_must_be_named_when_the_switch_is_on()
    {
        var form = Form(EcrTransactionType.Void) with { ReceiptNumber = "24", UseOtherTerminal = true };

        var (invalid, request) = form.Validated(Serial);
        Assert.Null(request);
        Assert.Equal("Enter the terminal the original was taken on", invalid.Errors.OriginalTerminalId);

        (_, request) = (form with { OriginalTerminalId = "31629" }).Validated(Serial);
        Assert.Equal("31629", request!.OriginalTerminalId);
    }

    [Fact]
    public void An_original_terminal_left_over_from_a_hidden_switch_is_not_sent()
    {
        var form = Form(EcrTransactionType.Void) with { ReceiptNumber = "24", OriginalTerminalId = "31629", UseOtherTerminal = false };

        Assert.Equal("", form.Validated(Serial).Request!.OriginalTerminalId);
    }

    [Fact]
    public void An_inquiry_by_receipt_number_needs_the_number_and_sends_the_date()
    {
        var (form, request) = Form(EcrTransactionType.Inquiry).Validated(Serial);
        Assert.Null(request);
        Assert.NotNull(form.Errors.ReceiptNumber);

        (_, request) = (Form(EcrTransactionType.Inquiry) with { ReceiptNumber = "24" }).Validated(Serial);
        Assert.Equal("24", request!.ReceiptNumber);
        Assert.Equal("20260809", request.TransactionDate);
        Assert.False(request.LooksUpByReference);
    }

    [Fact]
    public void An_inquiry_by_reference_needs_the_reference_and_sends_no_receipt_number_or_date()
    {
        var form = Form(EcrTransactionType.Inquiry) with { LookUpByReference = true, ReceiptNumber = "24" };

        var (invalid, request) = form.Validated(Serial);
        Assert.Null(request);
        Assert.Equal("Enter the reference the transaction was sent with", invalid.Errors.OriginalReference);
        Assert.Null(invalid.Errors.ReceiptNumber);

        (_, request) = (form with { OriginalReference = "  ORDER-77 " }).Validated(Serial);
        Assert.True(request!.LooksUpByReference);
        Assert.Equal("ORDER-77", request.OriginalReference);
        Assert.Equal("ORDER-77", request.MerchantReference);
        Assert.Equal("", request.ReceiptNumber);
        Assert.Equal("", request.TransactionDate);
    }

    [Fact]
    public void A_sale_reference_is_optional_and_trimmed()
    {
        var sale = Form(EcrTransactionType.Sale) with { AmountDigits = "1000" };

        Assert.Equal("", sale.Validated(Serial).Request!.MerchantReference);
        Assert.Equal("ORDER-1", (sale with { MerchantReference = " ORDER-1 " }).Validated(Serial).Request!.MerchantReference);
    }

    [Theory]
    [InlineData("ABC-123_x.y", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("has space", "Use printable ASCII without spaces, '&' or '='")]
    [InlineData("a&b", "Use printable ASCII without spaces, '&' or '='")]
    [InlineData("a=b", "Use printable ASCII without spaces, '&' or '='")]
    [InlineData("café", "Use printable ASCII without spaces, '&' or '='")]
    [InlineData("tab\there", "Use printable ASCII without spaces, '&' or '='")]
    [InlineData("123456789012345678901234567890123", "A merchant reference is at most 32 characters")]
    [InlineData("12345678901234567890123456789012", null)]
    public void The_merchant_reference_follows_the_sdks_rules(string reference, string? error) =>
        Assert.Equal(error, TransactionFormState.MerchantReferenceError(reference));

    [Fact]
    public void An_invalid_sale_reference_blocks_the_sale()
    {
        var (form, request) = (Form(EcrTransactionType.Sale) with { AmountDigits = "1000", MerchantReference = "a b" }).Validated(Serial);

        Assert.Null(request);
        Assert.NotNull(form.Errors.MerchantReference);
    }

    [Fact]
    public void Validation_returns_the_form_with_errors_and_clears_them_once_fixed()
    {
        var sale = Form(EcrTransactionType.Sale);
        var (withError, _) = sale.Validated(Serial);
        Assert.True(withError.Errors.Any);

        var (fixedForm, request) = (withError with { AmountDigits = "1000" }).Validated(Serial);
        Assert.False(fixedForm.Errors.Any);
        Assert.NotNull(request);
    }
}
