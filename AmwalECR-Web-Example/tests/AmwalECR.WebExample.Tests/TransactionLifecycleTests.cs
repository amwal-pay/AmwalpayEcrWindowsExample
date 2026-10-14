using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using AmwalECR.Example.Core.Transaction;

namespace AmwalECR.WebExample.Tests;

public class TransactionLifecycleTests
{
    private static string MessageType(JsonObject r) => r["messageType"]!.GetValue<string>();

    private static string State(JsonNode v) => v["state"]!.GetValue<string>();

    private static string Headline(JsonNode v) => v["dialog"]!["headline"]!.GetValue<string>();

    [Fact]
    public async Task A_sale_runs_to_approved_and_the_dialog_carries_what_a_cashier_reads()
    {
        using var h = new Harness();

        var id = await h.StartOkAsync(Harness.Sale("1234", merchantReference: "ORDER-1"));
        var end = await h.WaitFinishedAsync(id);

        Assert.Equal("completed", State(end));
        Assert.True(end["isResult"]!.GetValue<bool>());
        Assert.Equal("Sale", end["operation"]!.GetValue<string>());
        Assert.Equal("Approved", Headline(end));
        Assert.Equal("approved", end["dialog"]!["tone"]!.GetValue<string>());
        Assert.Equal("1.234", end["dialog"]!["amount"]!.GetValue<string>());
        var fields = end["dialog"]!["fields"]!.AsArray().ToDictionary(f => f!["label"]!.GetValue<string>(), f => f!["value"]!.GetValue<string>());
        Assert.Equal("123456", fields["Auth code"]);
        Assert.Equal("483312******1234", fields["Card"]);
        Assert.Equal("ORDER-1", fields["Reference"]);
        Assert.Contains("\"success\": true", end["dialog"]!["rawPayload"]!.GetValue<string>());

        var sent = Assert.Single(h.Terminal.Sent("SALE"));
        Assert.Equal("000000001234", sent["amount"]!.GetValue<string>());
        Assert.Equal(Harness.Serial, sent["terminalSerial"]!.GetValue<string>());
        Assert.Equal("ORDER-1", sent["merchantReference"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_running_transaction_reports_the_configured_timeout_and_what_is_left_of_it()
    {
        using var h = new Harness();

        var (status, started) = await h.StartAsync(Harness.Sale());

        Assert.Equal(HttpStatusCode.Accepted, status);
        // Wi-Fi keeps the LAN default; a Web Service terminal is given 130 s by the SDK.
        Assert.Equal(120, started!["timeoutSeconds"]!.GetValue<int>());
        var remaining = started["remainingSeconds"]!.GetValue<int>();
        Assert.InRange(remaining, 0, 120);

        var end = await h.WaitFinishedAsync(started["id"]!.GetValue<string>());
        Assert.Null(end["remainingSeconds"]);
    }

    [Fact]
    public async Task A_blank_reference_is_generated_by_the_app()
    {
        using var h = new Harness();

        await h.RunToEndAsync();

        Assert.Equal(12, Assert.Single(h.Terminal.Sent("SALE"))["merchantReference"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task A_decline_is_shown_as_declined_not_as_an_error()
    {
        using var h = new Harness(_ => FakeTerminal.Declined("51"));

        var end = await h.RunToEndAsync();

        Assert.Equal("completed", State(end));
        Assert.Equal("Declined", Headline(end));
        Assert.Equal("declined", end["dialog"]!["tone"]!.GetValue<string>());
        Assert.False(end["dialog"]!["isUnknownOutcome"]!.GetValue<bool>());
        Assert.Null(end["dialog"]!["inquirableReference"]);
    }

    [Fact]
    public async Task A_partial_approval_says_how_much_was_taken_and_what_is_owed()
    {
        using var h = new Harness(_ => FakeTerminal.Partial(requested: "1.000", settled: "0.500"));

        var end = await h.RunToEndAsync(Harness.Sale("1000"));

        Assert.Equal("Partially approved", Headline(end));
        Assert.Equal("0.500", end["dialog"]!["amount"]!.GetValue<string>());
        Assert.Equal("Only 0.500 of 1.000 was approved. Collect the difference by another means.", end["dialog"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unreachable_terminal_is_reported_before_anything_is_sent_and_is_not_an_unknown_outcome()
    {
        using var h = new Harness(register: false);
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        await h.RegisterAsync(Harness.Serial, port);
        await h.SaveKeyAsync();

        var end = await h.RunToEndAsync();

        Assert.Equal("failed", State(end));
        Assert.Equal("Not completed", Headline(end));
        Assert.Contains("is not reachable", end["dialog"]!["message"]!.GetValue<string>());
        Assert.Contains("Check:", end["dialog"]!["message"]!.GetValue<string>());
        Assert.False(end["dialog"]!["isUnknownOutcome"]!.GetValue<bool>()); // nothing was sent, so nothing to look up
        Assert.Empty(h.Terminal.Requests);
    }

    [Fact]
    public async Task A_lost_answer_the_terminal_can_account_for_is_recovered_by_the_automatic_inquiry()
    {
        using var h = new Harness(r => MessageType(r) == "INQUIRY" ? FakeTerminal.Found() : null);

        var end = await h.RunToEndAsync(Harness.Sale(merchantReference: "ORDER-3"));

        Assert.Equal("inquired", State(end));
        Assert.Equal("Approved", Headline(end)); // the transaction's own status, not the inquiry's
        Assert.Contains("Do not send it again.", end["dialog"]!["note"]!.GetValue<string>());
        Assert.Single(h.Terminal.Sent("SALE")); // never sent twice
        Assert.Equal("ORDER-3", h.Terminal.Sent("INQUIRY").First()["originalMerchantReference"]!.GetValue<string>());
        Assert.True(end["dialog"]!["offersReceipt"]!.GetValue<bool>()); // Wi‑Fi: the e-receipt can be asked for
    }

    [Fact]
    public async Task A_lost_answer_and_a_lost_follow_up_is_an_unknown_outcome_whose_only_next_step_is_a_lookup()
    {
        var inquiryAnswers = false;
        using var h = new Harness(r => MessageType(r) == "INQUIRY" && Volatile.Read(ref inquiryAnswers) ? FakeTerminal.Found() : null);

        var id = await h.StartOkAsync(Harness.Sale(merchantReference: "ORDER-2"));
        var end = await h.WaitFinishedAsync(id);

        Assert.Equal("failed", State(end));
        Assert.Equal("Outcome unknown", Headline(end));
        var dialog = end["dialog"]!;
        Assert.True(dialog["isUnknownOutcome"]!.GetValue<bool>());
        Assert.Equal("ORDER-2", dialog["inquirableReference"]!.GetValue<string>());
        Assert.Equal(ResultPresenter.UnknownOutcomeWarning, dialog["unknownOutcomeWarning"]!.GetValue<string>());
        Assert.Contains("charge the cardholder twice", dialog["unknownOutcomeWarning"]!.GetValue<string>());
        Assert.False(dialog["receipt"]!["showButton"]!.GetValue<bool>());
        Assert.Single(h.Terminal.Sent("SALE"));

        // The follow-up is a lookup by that reference — and the terminal knows it now.
        Volatile.Write(ref inquiryAnswers, true);
        var (status, started) = await h.Client.PostJson($"/api/transactions/{id}/inquire");
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(id, started!["id"]!.GetValue<string>()); // same flow, same id

        var found = await h.WaitAsync(id, v => State(v) == "inquired");
        Assert.Equal("Approved", Headline(found));
        Assert.Equal("Inquiry", found["operation"]!.GetValue<string>());
        Assert.Single(h.Terminal.Sent("SALE")); // still exactly one sale: never a retry
        Assert.Equal("ORDER-2", h.Terminal.Sent("INQUIRY").Last()["originalMerchantReference"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_lookup_that_fails_too_can_be_tried_again_because_the_reference_is_kept()
    {
        // Sale answer lost, its automatic follow-up lost, and then the operator's own look-up
        // fails as well. The reference was generated by the app, so it is in no form field: if
        // the failed look-up dropped it, "safe to try again" would have nothing left to try.
        var inquiryAnswers = false;
        using var h = new Harness(r => MessageType(r) == "INQUIRY" && Volatile.Read(ref inquiryAnswers) ? FakeTerminal.Found() : null);

        var id = await h.StartOkAsync(Harness.Sale(merchantReference: "ORDER-3"));
        await h.WaitFinishedAsync(id);

        var (first, _) = await h.Client.PostJson($"/api/transactions/{id}/inquire");
        Assert.Equal(HttpStatusCode.Accepted, first);
        var stillUnknown = await h.WaitAsync(id, v => State(v) == "failed" && h.Terminal.Sent("INQUIRY").Count >= 2);

        Assert.Equal("Outcome unknown", Headline(stillUnknown));
        Assert.Equal("ORDER-3", stillUnknown["dialog"]!["inquirableReference"]!.GetValue<string>());

        Volatile.Write(ref inquiryAnswers, true);
        var (second, _) = await h.Client.PostJson($"/api/transactions/{id}/inquire");
        Assert.Equal(HttpStatusCode.Accepted, second); // was 409 while the reference was dropped

        var found = await h.WaitAsync(id, v => State(v) == "inquired");
        Assert.Equal("Approved", Headline(found));
        Assert.Single(h.Terminal.Sent("SALE")); // never a retry of the sale
    }

    [Fact]
    public async Task Inquire_is_only_offered_for_an_unknown_outcome()
    {
        using var h = new Harness();
        var id = await h.StartOkAsync();
        await h.WaitFinishedAsync(id); // approved

        var (approved, _) = await h.Client.PostJson($"/api/transactions/{id}/inquire");
        var (missing, _) = await h.Client.PostJson("/api/transactions/nope/inquire");

        Assert.Equal(HttpStatusCode.Conflict, approved);
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Empty(h.Terminal.Sent("INQUIRY"));
    }

    [Fact]
    public async Task Cancelling_during_the_wait_is_an_unknown_outcome_never_a_cancelled_sale()
    {
        var reached = new SemaphoreSlim(0);
        var release = new SemaphoreSlim(0);
        using var h = new Harness(r =>
        {
            if (MessageType(r) != "SALE") return null;
            reached.Release();
            release.Wait(TimeSpan.FromSeconds(15));
            return null;
        });

        var id = await h.StartOkAsync(Harness.Sale(merchantReference: "ORDER-5"));
        Assert.True(await reached.WaitAsync(TimeSpan.FromSeconds(15)));
        var running = await h.WaitAsync(id, v => State(v) == "inProgress");
        Assert.True(running["busy"]!.GetValue<bool>());
        Assert.Equal("Waiting for the POS terminal…", running["statusText"]!.GetValue<string>());

        var (status, _) = await h.Client.PostJson($"/api/transactions/{id}/cancel");
        var end = await h.WaitFinishedAsync(id);
        release.Release();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("failed", State(end));
        Assert.Equal("Outcome unknown", Headline(end));
        Assert.DoesNotContain("ancel", Headline(end));
        Assert.Contains("does not cancel the transaction", end["dialog"]!["message"]!.GetValue<string>());
        Assert.Equal("ORDER-5", end["dialog"]!["inquirableReference"]!.GetValue<string>());
        Assert.True(end["dialog"]!["isUnknownOutcome"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Cancelling_an_unknown_or_finished_transaction_is_harmless()
    {
        using var h = new Harness();
        var id = await h.StartOkAsync();
        await h.WaitFinishedAsync(id);

        var (finished, body) = await h.Client.PostJson($"/api/transactions/{id}/cancel");
        var (unknown, _) = await h.Client.PostJson("/api/transactions/nope/cancel");

        Assert.Equal(HttpStatusCode.OK, finished);
        Assert.Equal("completed", State(body!)); // a result is not undone by a late Cancel
        Assert.Equal(HttpStatusCode.NotFound, unknown);
    }

    [Fact]
    public async Task A_second_transaction_on_the_same_terminal_is_refused_while_one_runs()
    {
        var release = new SemaphoreSlim(0);
        using var h = new Harness(r =>
        {
            release.Wait(TimeSpan.FromSeconds(15));
            return FakeTerminal.Approved();
        });

        var first = await h.StartOkAsync();
        var (status, body) = await h.StartAsync();
        var (again, _) = await h.StartAsync(); // a double click

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Equal(first, body!["transactionId"]!.GetValue<string>()); // it says which one is running
        Assert.Equal("A transaction is already running on this terminal", body["error"]!.GetValue<string>());

        release.Release(2);
        await h.WaitFinishedAsync(first);
        Assert.Single(h.Terminal.Sent("SALE"));

        // Once it has finished, the terminal is free again.
        var next = await h.StartOkAsync();
        await h.WaitFinishedAsync(next);
        Assert.Equal(2, h.Terminal.Sent("SALE").Count);
    }

    [Fact]
    public async Task Concurrent_starts_on_one_terminal_let_exactly_one_through()
    {
        var release = new SemaphoreSlim(0);
        using var h = new Harness(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(15));
            return FakeTerminal.Approved();
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.StartAsync()));
        release.Release(8);

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Accepted));
        Assert.Equal(7, results.Count(r => r.Status == HttpStatusCode.Conflict));
        await h.WaitFinishedAsync(results.Single(r => r.Status == HttpStatusCode.Accepted).Body!["id"]!.GetValue<string>());
        Assert.Single(h.Terminal.Sent("SALE"));
    }

    [Fact]
    public async Task Different_terminals_run_side_by_side()
    {
        var release = new SemaphoreSlim(0);
        using var h = new Harness(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(15));
            return FakeTerminal.Approved();
        });
        await h.RegisterAsync("TW2", h.Terminal.Port, name: "Second");

        var one = await h.StartOkAsync();
        var two = await h.StartOkAsync(Harness.Sale(serial: "TW2"));
        release.Release(2);

        Assert.NotEqual(one, two);
        await h.WaitFinishedAsync(one);
        await h.WaitFinishedAsync(two);
    }

    [Fact]
    public async Task A_payment_does_not_depend_on_the_page_that_started_it()
    {
        // The page closes: nothing polls, and a poll that is dropped mid-flight is aborted.
        // Neither may cancel the payment, which is owned by the server.
        var release = new SemaphoreSlim(0);
        using var h = new Harness(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(15));
            return FakeTerminal.Approved();
        });
        var id = await h.StartOkAsync(Harness.Sale(merchantReference: "ORDER-7"));

        using (var abort = new CancellationTokenSource())
        {
            var poll = h.Client.Send(HttpMethod.Get, $"/api/transactions/{id}", ct: abort.Token);
            abort.Cancel();
            try { await poll; } catch (OperationCanceledException) { }
        }
        await Task.Delay(400); // the tab is gone for a while
        release.Release();

        var end = await h.WaitFinishedAsync(id);
        Assert.Equal("completed", State(end));
        Assert.Equal("Approved", Headline(end));
        Assert.Equal("ORDER-7", Assert.Single(h.Terminal.Sent("SALE"))["merchantReference"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_reloaded_page_finds_the_transaction_it_left_running_and_the_result_nobody_read()
    {
        var release = new SemaphoreSlim(0);
        using var h = new Harness(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(15));
            return FakeTerminal.Approved();
        });
        var id = await h.StartOkAsync();

        var (_, running) = await h.Client.GetJson("/api/transactions");
        Assert.Equal(id, running!["transactions"]![0]!["id"]!.GetValue<string>());
        Assert.True(running["transactions"]![0]!["busy"]!.GetValue<bool>());

        release.Release();
        await h.WaitFinishedAsync(id);
        var (_, result) = await h.Client.GetJson("/api/transactions");
        Assert.Equal("completed", result!["transactions"]![0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Acknowledging_frees_the_transaction_and_is_refused_while_it_runs()
    {
        var release = new SemaphoreSlim(0);
        using var h = new Harness(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(15));
            return FakeTerminal.Approved();
        });
        var id = await h.StartOkAsync();

        var (whileRunning, _) = await h.Client.DeleteJson($"/api/transactions/{id}");
        release.Release();
        await h.WaitFinishedAsync(id);
        var (done, _) = await h.Client.DeleteJson($"/api/transactions/{id}");
        var (gone, _) = await h.Client.GetJson($"/api/transactions/{id}");
        var (again, _) = await h.Client.DeleteJson($"/api/transactions/{id}");
        var (_, pending) = await h.Client.GetJson("/api/transactions");

        Assert.Equal(HttpStatusCode.Conflict, whileRunning);
        Assert.Equal(HttpStatusCode.NoContent, done);
        Assert.Equal(HttpStatusCode.NotFound, gone);
        Assert.Equal(HttpStatusCode.NoContent, again); // idempotent: closing twice is fine
        Assert.Empty(pending!["transactions"]!.AsArray());
    }

    [Fact]
    public async Task Finished_transactions_nobody_acknowledged_are_evicted_after_a_long_while()
    {
        var time = new ManualTime();
        using var h = new Harness(time: time);
        var id = await h.StartOkAsync();
        await h.WaitFinishedAsync(id);

        time.Advance(TimeSpan.FromHours(11));
        var (stillThere, _) = await h.Client.GetJson($"/api/transactions/{id}");
        time.Advance(TimeSpan.FromHours(2));
        var (evicted, _) = await h.Client.GetJson($"/api/transactions/{id}");

        Assert.Equal(HttpStatusCode.OK, stillThere); // an unknown outcome must outlive a night away from the till
        Assert.Equal(HttpStatusCode.NotFound, evicted);
    }

    [Fact]
    public async Task An_inquiry_that_finds_nothing_says_not_found_and_a_signature_mismatch_is_not_believed()
    {
        using (var h = new Harness(_ => FakeTerminal.NotFound()))
        {
            var end = await h.RunToEndAsync(new { terminalSerial = Harness.Serial, type = "Inquiry", lookUpByReference = true, originalReference = "ORDER-9" });

            Assert.Equal("inquired", State(end));
            Assert.Equal("Not found", Headline(end));
            Assert.Equal("ORDER-9", Assert.Single(h.Terminal.Sent("INQUIRY"))["originalMerchantReference"]!.GetValue<string>());
        }

        using var wrongKey = new Harness();
        await wrongKey.SaveKeyAsync("FEDCBA9876543210FEDCBA9876543210");
        var bad = await wrongKey.RunToEndAsync(Harness.Sale(merchantReference: "ORDER-4"));

        Assert.Equal("failed", State(bad));
        Assert.Contains("not signed with this terminal's key", bad["dialog"]!["message"]!.GetValue<string>());
        Assert.Equal("ORDER-4", bad["dialog"]!["inquirableReference"]!.GetValue<string>());
    }

    [Fact]
    public async Task Void_and_refund_run_through_the_same_flow()
    {
        using var h = new Harness();

        var voided = await h.RunToEndAsync(new { terminalSerial = Harness.Serial, type = "Void", receiptNumber = "24" });
        var refunded = await h.RunToEndAsync(new { terminalSerial = Harness.Serial, type = "Refund", amountDigits = "500", receiptNumber = "24" });

        Assert.Equal("Approved", Headline(voided));
        Assert.Equal("Approved", Headline(refunded));
        Assert.DoesNotContain(voided["dialog"]!["fields"]!.AsArray(), f => f!["label"]!.GetValue<string>() == "Auth code"); // a void has no auth of its own
        Assert.Equal(new[] { "VOID", "REFUND" }, h.Terminal.Requests.Select(MessageType).ToArray());
    }
}
