using AmwalECR.Example.Core.Services;
using AmwalECR.Example.Core.Transaction;
using AmwalPay.Ecr;
using Xunit;

namespace AmwalECR.Example.Tests;

public class TransactionOutcomesTests
{
    private static readonly TransactionRequest Sale = new()
    {
        Type = EcrTransactionType.Sale, TerminalSerial = "TW1", Amount = 1.234m, MerchantReference = "REF1",
    };

    private static EcrTransaction Txn(string status = "Approved", bool partial = false) => new(
        TransactionId: "id", Stan: "000024", Type: "Purchase", Status: status, Amount: "961.100", TotalAmount: "961.100",
        Currency: "OMR", TransactionTime: "2026-08-09T16:58:16.804271", MaskedPan: "483312******1234", CardHolderName: "",
        Rrn: "1", AuthCode: "2", BatchId: "3", TerminalId: "4", IsRefunded: false, CanVoid: true, CanRefund: true,
        PartialApproval: partial, AuthorizedAmount: partial ? "50.000" : "961.100");

    private static EcrResult.Approved Approved(bool partial = false) => new(
        "REF1", partial ? "50.000" : "1.234", "00", "rrn", "123456", "483312******1234", partial, "1.234", "{}");

    [Fact]
    public void An_approval_or_a_decline_is_a_completed_result()
    {
        Assert.IsType<TransactionState.Completed>(TransactionOutcomes.FromResult(Approved(), Sale));

        var declined = new EcrResult.Declined("REF1", "51", "Insufficient funds", NextStep.None, "{}");
        var state = Assert.IsType<TransactionState.Completed>(TransactionOutcomes.FromResult(declined, Sale));
        Assert.Same(declined, state.Result);
        Assert.Equal(EcrTransactionType.Sale, state.Type);
    }

    [Theory]
    [InlineData(typeof(Failure.Timeout))]
    [InlineData(typeof(Failure.ConnectionLost))]
    [InlineData(typeof(Failure.Malformed))]
    [InlineData(typeof(Failure.Unauthenticated))]
    public void A_failure_after_the_request_went_out_offers_a_lookup_by_reference_and_never_a_retry(Type failureType)
    {
        var failure = (Failure)Activator.CreateInstance(failureType, "no answer")!;

        var state = Assert.IsType<TransactionState.Failed>(
            TransactionOutcomes.FromResult(new EcrResult.Failed("REF1", failure), Sale));

        Assert.Equal("no answer", state.Message);
        Assert.Equal("REF1", state.InquirableReference);
    }

    [Fact]
    public void An_unreachable_terminal_means_nothing_was_sent_so_there_is_nothing_to_look_up()
    {
        var state = Assert.IsType<TransactionState.Failed>(TransactionOutcomes.FromResult(
            new EcrResult.Failed("REF1", new Failure.Unreachable("refused")), Sale));

        Assert.Null(state.InquirableReference);
    }

    [Fact]
    public void A_failure_the_sdk_recovered_from_is_shown_as_the_transaction_it_found()
    {
        var found = new EcrInquiry.Found("REF1", Txn(), "{}");

        var state = Assert.IsType<TransactionState.Inquired>(TransactionOutcomes.FromResult(
            new EcrResult.Failed("REF1", new Failure.Timeout("no answer"), found), Sale));

        Assert.Same(found, state.Inquiry);
        Assert.True(state.RecoveredFromFailure);
        Assert.Equal(EcrTransactionType.Inquiry, state.Request.Type);
        Assert.Equal("REF1", state.Request.OriginalReference);
        Assert.Null(state.Request.Amount);
    }

    [Fact]
    public void An_automatic_check_that_found_nothing_does_not_prove_nothing_was_charged()
    {
        var state = Assert.IsType<TransactionState.Failed>(TransactionOutcomes.FromResult(
            new EcrResult.Failed("REF1", new Failure.Timeout("no answer"), new EcrInquiry.NotFound("REF1", "none", "{}")), Sale));

        Assert.Equal("REF1", state.InquirableReference);
        Assert.Contains("does not prove nothing was charged", state.Note);
    }

    [Fact]
    public void A_web_service_no_response_decline_is_an_unknown_outcome_not_a_refusal()
    {
        var lost = new EcrResult.Declined("REF1", "NO_RESPONSE", "Timed out after 2m 10s", NextStep.None, "{}");

        var state = Assert.IsType<TransactionState.Failed>(TransactionOutcomes.FromResult(lost, Sale));

        Assert.Equal("REF1", state.InquirableReference);
        Assert.Contains("Timed out", state.Message);
    }

    [Fact]
    public void A_decline_that_says_inquire_is_an_unknown_outcome_too()
    {
        var unsure = new EcrResult.Declined("REF1", "96", "System error", NextStep.InquireByMerchantReference, "{}");

        var state = Assert.IsType<TransactionState.Failed>(TransactionOutcomes.FromResult(unsure, Sale));

        Assert.Equal("REF1", state.InquirableReference);
    }

    [Fact]
    public void An_ordinary_web_service_decline_stays_a_decline()
    {
        var declined = new EcrResult.Declined("REF1", "05", "Do not honour", NextStep.None, "{}");

        Assert.IsType<TransactionState.Completed>(TransactionOutcomes.FromResult(declined, Sale));
    }

    [Fact]
    public void An_inquiry_answer_is_an_inquiry_state_even_when_it_finds_a_declined_sale()
    {
        var request = Sale with { Type = EcrTransactionType.Inquiry };

        var state = TransactionOutcomes.FromInquiry(new EcrInquiry.Found("REF", Txn("Declined"), "{}"), request);

        Assert.IsType<TransactionState.Inquired>(state);
    }

    [Fact]
    public void A_failed_inquiry_is_safe_to_repeat_and_says_so()
    {
        var state = Assert.IsType<TransactionState.Failed>(TransactionOutcomes.FromInquiry(
            new EcrInquiry.Failed("REF", new Failure.Timeout("no answer")), Sale));

        Assert.Null(state.InquirableReference);
        Assert.Contains("safe to try again", state.Note);
    }

    [Fact]
    public void A_failed_look_up_by_reference_keeps_the_reference_so_it_can_be_tried_again()
    {
        // The reference was generated for a lost sale, so it is in no form field.
        var byReference = Sale with { Type = EcrTransactionType.Inquiry, OriginalReference = "ABC123DEF456" };

        var state = Assert.IsType<TransactionState.Failed>(TransactionOutcomes.FromInquiry(
            new EcrInquiry.Failed("REF", new Failure.Timeout("no answer")), byReference));

        Assert.Equal("ABC123DEF456", state.InquirableReference);
        Assert.Equal("Outcome unknown", ResultPresenter.ForFailure(state).Headline);
    }

    [Fact]
    public void A_web_service_transport_error_during_a_look_up_by_reference_keeps_the_reference_too()
    {
        var raw = "{\"success\":false,\"responseCode\":\"NO_RESPONSE\",\"message\":\"Timed out\"}";
        var byReference = Sale with { Type = EcrTransactionType.Inquiry, OriginalReference = " ABC123DEF456 " };

        var state = Assert.IsType<TransactionState.Failed>(
            TransactionOutcomes.FromInquiry(new EcrInquiry.NotFound("REF", "Timed out", raw), byReference));

        Assert.Equal("ABC123DEF456", state.InquirableReference);
    }

    [Fact]
    public void A_web_service_transport_error_is_not_shown_as_not_found()
    {
        var raw = "{\"success\":false,\"responseCode\":\"NO_RESPONSE\",\"message\":\"Timed out\"}";

        var state = TransactionOutcomes.FromInquiry(new EcrInquiry.NotFound("REF", "Timed out", raw), Sale);

        Assert.IsType<TransactionState.Failed>(state);
        Assert.True(TransactionOutcomes.IsNoResponse(raw));
        Assert.False(TransactionOutcomes.IsNoResponse("{\"success\":false,\"responseCode\":\"25\"}"));
        Assert.False(TransactionOutcomes.IsNoResponse("not json"));
        Assert.False(TransactionOutcomes.IsNoResponse(""));
    }

    [Fact]
    public void A_genuine_not_found_stays_not_found()
    {
        var state = TransactionOutcomes.FromInquiry(
            new EcrInquiry.NotFound("REF", "No transactions found", "{\"success\":false,\"responseCode\":\"25\"}"), Sale);

        Assert.IsType<TransactionState.Inquired>(state);
    }

    [Fact]
    public void The_unreachable_message_lists_what_to_check()
    {
        var text = TransactionOutcomes.UnreachableMessage("192.168.1.50:9100", 9100, "Connection refused");

        Assert.StartsWith("192.168.1.50:9100 is not reachable\n(Connection refused)", text);
        Assert.Contains("port 9100", text);
        Assert.Contains("127.0.0.1", text);
    }

    [Fact]
    public void Stopping_a_wait_never_reads_as_a_cancelled_sale()
    {
        var state = Assert.IsType<TransactionState.Failed>(TransactionEngine.CancelledState(Sale, "REF1"));

        Assert.Equal("REF1", state.InquirableReference);
        Assert.Contains("does not cancel the transaction", state.Message);
        Assert.IsType<TransactionState.Idle>(
            TransactionEngine.CancelledState(Sale with { Type = EcrTransactionType.Inquiry }, "REF1"));
    }

    [Theory]
    [InlineData("2026-08-09T16:58:16.804271", "20260809")]
    [InlineData("20260809165816", "20260809")]
    [InlineData("soon", "")]
    [InlineData("", "")]
    public void The_day_is_read_off_the_backend_timestamp(string time, string day) =>
        Assert.Equal(day, TransactionEngine.DayOf(time));

    [Fact]
    public void A_generated_reference_is_a_valid_merchant_reference()
    {
        var reference = TransactionEngine.NewReference();

        Assert.Equal(12, reference.Length);
        Assert.Null(TransactionFormState.MerchantReferenceError(reference));
    }

    [Fact]
    public void State_flags()
    {
        Assert.True(new TransactionState.Checking().IsBusy);
        Assert.True(new TransactionState.InProgress().IsBusy);
        Assert.False(new TransactionState.Idle().IsBusy);
        Assert.True(new TransactionState.Failed("x").IsResult);
        Assert.False(new TransactionState.Idle().IsResult);
    }
}

public class ResultPresenterTests
{
    private static EcrResult.Approved Approved(bool partial = false, string auth = "123456") => new(
        "REF1", partial ? "50.000" : "1.234", "00", "rrn", auth, "483312******1234", partial, "961.100", "{\"a\":1}");

    [Fact]
    public void An_approval_shows_amount_auth_code_card_and_reference()
    {
        var model = ResultPresenter.ForResult(Approved(), EcrTransactionType.Sale);

        Assert.Equal("Approved", model.Headline);
        Assert.Equal(ResultTone.Approved, model.Tone);
        Assert.Equal("1.234", model.Amount);
        Assert.Null(model.Message);
        Assert.Equal(new[] { "Auth code", "Card", "Reference" }, model.Fields.Select(f => f.Label));
        Assert.False(model.IsUnknownOutcome);
    }

    [Fact]
    public void A_partial_approval_is_called_out_with_both_figures()
    {
        var model = ResultPresenter.ForResult(Approved(partial: true), EcrTransactionType.Sale);

        Assert.Equal("Partially approved", model.Headline);
        Assert.Equal("50.000", model.Amount);
        Assert.Equal("Only 50.000 of 961.100 was approved. Collect the difference by another means.", model.Message);
    }

    [Fact]
    public void A_void_shows_no_auth_code()
    {
        var model = ResultPresenter.ForResult(Approved(), EcrTransactionType.Void);

        Assert.DoesNotContain(model.Fields, f => f.Label == "Auth code");
    }

    [Fact]
    public void Empty_fields_are_left_out()
    {
        var model = ResultPresenter.ForResult(Approved(auth: ""), EcrTransactionType.Sale);

        Assert.DoesNotContain(model.Fields, f => f.Label == "Auth code");
    }

    [Fact]
    public void A_decline_reads_the_terminals_own_words_from_the_answer()
    {
        var raw = "{\"success\":false,\"responseCode\":\"51\",\"message\":\"Declined\",\"errorList\":[\"Insufficient funds\"]}";
        var model = ResultPresenter.ForResult(new EcrResult.Declined("REF1", "51", "Declined", NextStep.None, raw), EcrTransactionType.Sale);

        Assert.Equal("Declined", model.Headline);
        Assert.Equal(ResultTone.Declined, model.Tone);
        Assert.Equal("Insufficient funds", model.Message);
        Assert.Contains("\"errorList\"", model.RawPayload);
    }

    [Fact]
    public void An_unknown_outcome_says_outcome_unknown_and_offers_only_the_lookup()
    {
        var model = ResultPresenter.ForFailure(new TransactionState.Failed("timed out", "REF1"));

        Assert.Equal("Outcome unknown", model.Headline);
        Assert.Equal("REF1", model.InquirableReference);
        Assert.True(model.IsUnknownOutcome);
        Assert.Contains("charge the cardholder twice", ResultPresenter.UnknownOutcomeWarning);
    }

    [Fact]
    public void A_failure_with_nothing_sent_says_not_completed()
    {
        var model = ResultPresenter.ForFailure(new TransactionState.Failed("unreachable"));

        Assert.Equal("Not completed", model.Headline);
        Assert.Null(model.InquirableReference);
    }

    private static TransactionState.Inquired Inquired(EcrTransaction txn, bool recovered = false) => new(
        new EcrInquiry.Found("REF", txn, "{\"x\":1}"),
        new TransactionRequest { Type = EcrTransactionType.Inquiry, TerminalSerial = "TW1", ReceiptNumber = "24" },
        RecoveredFromFailure: recovered);

    private static EcrTransaction Txn(string status = "Approved", bool partial = false, bool canVoid = true, bool canRefund = false, bool refunded = false) => new(
        "id", "000024", "Purchase", status, "961.100", "961.100", "OMR", "2026-08-09T16:58:16.804271", "483312******1234", "",
        "1", "2", "3", "4", refunded, canVoid, canRefund, partial, partial ? "50.000" : "961.100");

    [Fact]
    public void An_inquiry_summarises_receipt_number_type_card_amount_status_and_time()
    {
        var model = ResultPresenter.ForInquiry(Inquired(Txn()), receiptSupported: true, webService: false);

        Assert.Equal("Approved", model.Headline); // the transaction's status, not the lookup's
        Assert.Equal("961.100", model.Amount);
        Assert.Equal(
            new[] { ("Receipt number", "000024"), ("Type", "Purchase"), ("Card", "483312******1234"), ("Date/Time", "09 Aug 2026 16:58") },
            model.Fields.Select(f => (f.Label, f.Value)));
        Assert.True(model.OffersReceipt);
    }

    [Fact]
    public void An_inquiry_that_finds_a_declined_sale_reads_declined()
    {
        var model = ResultPresenter.ForInquiry(Inquired(Txn("Declined")), true, false);

        Assert.Equal("Declined", model.Headline);
        Assert.Equal(ResultTone.Declined, model.Tone);
    }

    [Fact]
    public void A_partial_approval_read_back_from_an_inquiry_reads_the_same_as_the_sale()
    {
        var model = ResultPresenter.ForInquiry(Inquired(Txn(partial: true)), true, false);

        Assert.Equal("Partially approved", model.Headline);
        Assert.Equal("50.000", model.Amount);
        Assert.Equal("Only 50.000 of 961.100 was approved. Collect the difference by another means.", model.Message);
    }

    [Fact]
    public void The_can_void_and_can_refund_hints_come_from_the_backend()
    {
        var model = ResultPresenter.ForInquiry(Inquired(Txn(canVoid: true, canRefund: true, refunded: true)), true, false);

        Assert.Equal(new[] { "Already refunded", "Can be voided", "Can be refunded" }, model.Hints);
        Assert.Empty(ResultPresenter.ForInquiry(Inquired(Txn(canVoid: false, canRefund: false)), true, false).Hints);
    }

    [Fact]
    public void A_finding_recovered_after_a_lost_answer_says_so()
    {
        var model = ResultPresenter.ForInquiry(Inquired(Txn(), recovered: true), true, false);

        Assert.Contains("Do not send it again", model.Note);
    }

    [Fact]
    public void The_receipt_qr_is_offered_over_wifi_only()
    {
        var web = ResultPresenter.ForInquiry(Inquired(Txn()), receiptSupported: false, webService: true);

        Assert.False(web.OffersReceipt);
        Assert.Contains("Wi‑Fi only", web.ReceiptNote);
    }

    [Fact]
    public void Not_found_carries_the_terminals_reason()
    {
        var state = new TransactionState.Inquired(
            new EcrInquiry.NotFound("REF", "No transactions found", "{\"success\":false,\"message\":\"No transactions found\"}"),
            new TransactionRequest { Type = EcrTransactionType.Inquiry, TerminalSerial = "TW1" });

        var model = ResultPresenter.ForInquiry(state, true, false);

        Assert.Equal("Not found", model.Headline);
        Assert.Equal("No transactions found", model.Message);
    }

    [Fact]
    public void A_web_service_not_found_by_reference_carries_the_caveat()
    {
        var request = new TransactionRequest { Type = EcrTransactionType.Inquiry, TerminalSerial = "TW1", OriginalReference = "REF" };
        var state = new TransactionState.Inquired(new EcrInquiry.NotFound("REF", "none", "{}"), request);

        var model = ResultPresenter.ForInquiry(state, receiptSupported: false, webService: true);

        Assert.Contains("does not prove nothing was charged", model.Note);
    }

    [Theory]
    [InlineData("2026-08-09T16:58:16.804271", "09 Aug 2026 16:58")]
    [InlineData("2026-08-09T16:58:16", "09 Aug 2026 16:58")]
    [InlineData("yesterday", "yesterday")]
    [InlineData("", "")]
    public void Backend_times_are_made_readable(string time, string shown) =>
        Assert.Equal(shown, ResultPresenter.ReadableTime(time));

    [Fact]
    public void Raw_json_is_indented_for_reading_and_other_text_is_left_alone()
    {
        Assert.Contains("\n", ResultPresenter.Pretty("{\"a\":1,\"b\":{\"c\":2}}"));
        Assert.Equal("not json", ResultPresenter.Pretty("not json"));
        Assert.Equal("", ResultPresenter.Pretty(""));
    }

    [Fact]
    public void The_presenter_picks_the_model_for_each_state()
    {
        Assert.Null(ResultPresenter.For(new TransactionState.Idle(), false));
        Assert.Null(ResultPresenter.For(new TransactionState.Checking(), false));
        Assert.Equal("Approved", ResultPresenter.For(
            new TransactionState.Completed(Approved(), EcrTransactionType.Sale), false)!.Headline);
        Assert.Equal("Outcome unknown", ResultPresenter.For(new TransactionState.Failed("x", "REF"), false)!.Headline);
        Assert.Equal("Approved", ResultPresenter.For(Inquired(Txn()), usesWebService: false)!.Headline);
    }
}
