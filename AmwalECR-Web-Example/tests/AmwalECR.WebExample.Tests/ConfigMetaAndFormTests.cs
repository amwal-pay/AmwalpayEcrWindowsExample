using System.Net;
using AmwalECR.Example.Core.Transaction;
using AmwalECR.Example.Core.Ui;
using AmwalPay.Ecr;

namespace AmwalECR.WebExample.Tests;

public class ConfigCardTests
{
    [Fact]
    public async Task A_ready_wifi_terminal_shows_its_transport_connection_and_key_without_the_key()
    {
        using var h = new Harness();

        var (status, body) = await h.Client.GetJson($"/api/terminals/{Harness.Serial}/config");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["isReady"]!.GetValue<bool>());
        Assert.Equal("Wi‑Fi", body["modeLabel"]!.GetValue<string>());
        Assert.Equal("TCP LAN (sorted key=value HMAC)", body["transportLabel"]!.GetValue<string>());
        Assert.Equal($"127.0.0.1:{h.Terminal.Port}", body["connectionSummary"]!.GetValue<string>());
        Assert.Equal("Wi-Fi/LAN secure hash key", body["keyLabel"]!.GetValue<string>());
        Assert.True(body["keyConfigured"]!.GetValue<bool>());
        Assert.Equal("configured", body["keyStatus"]!.GetValue<string>()); // not even the key's length
        Assert.Empty(body["issues"]!.AsArray());
        Assert.DoesNotContain(FakeTerminal.Key, body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_key_is_an_issue_shown_before_paying()
    {
        using var h = new Harness(register: false);
        await h.RegisterAsync(Harness.Serial, h.Terminal.Port);

        var (_, body) = await h.Client.GetJson($"/api/terminals/{Harness.Serial}/config");

        Assert.False(body!["isReady"]!.GetValue<bool>());
        Assert.False(body["keyConfigured"]!.GetValue<bool>());
        Assert.Equal("not configured — set in Settings", body["keyStatus"]!.GetValue<string>());
        Assert.Contains("Wi-Fi/LAN secure hash key is not configured", body["issues"]!.AsArray().Select(i => i!.GetValue<string>()));
    }

    [Fact]
    public async Task The_config_follows_the_keys_without_reloading_terminals()
    {
        using var h = new Harness();
        await h.Client.DeleteJson("/api/settings/keys/wifi");

        var (_, body) = await h.Client.GetJson($"/api/terminals/{Harness.Serial}/config");

        Assert.False(body!["isReady"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(1, "USB Cable is not supported in this sample")]
    [InlineData(3, "Bluetooth is not supported in this sample")]
    public async Task Usb_and_bluetooth_are_flagged_unsupported(int mode, string issue)
    {
        using var h = new Harness();
        await h.Client.PostJson("/api/terminals", new { name = "X", serialNumber = "X1", ecrMode = mode, ipAddress = "127.0.0.1" });

        var (_, body) = await h.Client.GetJson("/api/terminals/X1/config");

        Assert.False(body!["isSupported"]!.GetValue<bool>());
        Assert.False(body["isReady"]!.GetValue<bool>());
        Assert.Contains(issue, body["issues"]!.AsArray().Select(i => i!.GetValue<string>()));
    }

    [Fact]
    public async Task A_web_service_terminal_uses_the_web_service_key_and_transport()
    {
        using var h = new Harness();
        await h.Client.PostJson("/api/terminals", new { name = "W", serialNumber = "W1", ecrMode = 4, merchantId = "12", terminalId = "34" });

        var (_, body) = await h.Client.GetJson("/api/terminals/W1/config");

        Assert.True(body!["usesWebService"]!.GetValue<bool>());
        Assert.Equal("Web Service secure hash key", body["keyLabel"]!.GetValue<string>());
        Assert.False(body["keyConfigured"]!.GetValue<bool>()); // the LAN key does not stand in for it
        Assert.Equal("HTTPS Web Service (JSON HMAC)", body["transportLabel"]!.GetValue<string>());
        Assert.Equal("SIT · merchant 12 · terminal 34", body["connectionSummary"]!.GetValue<string>()); // the SDK plan's own summary
    }

    [Fact]
    public async Task An_unknown_terminal_is_a_404()
    {
        using var h = new Harness();

        var (status, _) = await h.Client.GetJson("/api/terminals/NOPE/config");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task Announcing_a_selection_logs_it_like_the_windows_sample_and_a_plain_read_does_not()
    {
        using var h = new Harness();
        await h.Client.GetJson($"/api/terminals/{Harness.Serial}/config");
        Assert.DoesNotContain("selected " + Harness.Serial, h.App.Log.Snapshot());

        await h.Client.GetJson($"/api/terminals/{Harness.Serial}/config?announce=true");

        Assert.Contains("selected " + Harness.Serial, h.App.Log.Snapshot());
    }
}

public class MetaTests
{
    [Fact]
    public async Task Operations_carry_the_sdk_flags_and_the_form_layout_core_decides()
    {
        using var h = new Harness(register: false);

        var (_, meta) = await h.Client.GetJson("/api/meta");
        var operations = meta!["operations"]!.AsArray().ToDictionary(o => o!["value"]!.GetValue<string>(), o => o!);

        Assert.Equal(new[] { "Sale", "Void", "Refund", "Inquiry" }, operations.Keys.ToArray()); // no Receipt: not a menu choice
        foreach (var type in EcrTransactionTypes.MenuOptions)
        {
            var op = operations[type.ToString()];
            Assert.Equal(type.RequiresAmount(), op["requiresAmount"]!.GetValue<bool>());
            Assert.Equal(type.RequiresOriginalStan(), op["requiresOriginalStan"]!.GetValue<bool>());
            Assert.Equal(type.RequiresOriginalDate(), op["requiresOriginalDate"]!.GetValue<bool>());
            Assert.Equal(type.AllowsOtherTerminal(), op["allowsOtherTerminal"]!.GetValue<bool>());
            Assert.Equal(type.MovesMoney(), op["movesMoney"]!.GetValue<bool>());
        }

        var sale = operations["Sale"]["layouts"]!["00"]!;
        Assert.True(sale["amount"]!.GetValue<bool>());
        Assert.True(sale["merchantReference"]!.GetValue<bool>());
        Assert.False(sale["receiptNumber"]!.GetValue<bool>());

        var refund = operations["Refund"]["layouts"]!["00"]!;
        Assert.True(refund["amount"]!.GetValue<bool>());
        Assert.True(refund["receiptNumber"]!.GetValue<bool>());
        Assert.True(refund["originalDate"]!.GetValue<bool>());
        Assert.True(refund["otherTerminalSwitch"]!.GetValue<bool>());
        Assert.False(refund["originalTerminal"]!.GetValue<bool>());
        Assert.True(operations["Refund"]["layouts"]!["10"]!["originalTerminal"]!.GetValue<bool>()); // "different terminal" on

        var voided = operations["Void"]["layouts"]!["00"]!;
        Assert.False(voided["amount"]!.GetValue<bool>());
        Assert.False(voided["originalDate"]!.GetValue<bool>());
        Assert.True(voided["receiptNumber"]!.GetValue<bool>());

        var inquiry = operations["Inquiry"]["layouts"]!;
        Assert.True(inquiry["00"]!["referenceSwitch"]!.GetValue<bool>());
        Assert.True(inquiry["00"]!["originalDate"]!.GetValue<bool>());
        Assert.True(inquiry["01"]!["originalReference"]!.GetValue<bool>());
        Assert.False(inquiry["01"]!["receiptNumber"]!.GetValue<bool>()); // the reference field gives way
        Assert.False(inquiry["01"]!["originalDate"]!.GetValue<bool>());

        Assert.Equal("An inquiry only reads; nothing is charged", operations["Inquiry"]["amountHelp"]!.GetValue<string>());
        Assert.Equal("Uses the original transaction's amount", operations["Void"]["amountHelp"]!.GetValue<string>());
        Assert.Equal("", operations["Sale"]["amountHelp"]!.GetValue<string>());
        Assert.Equal("Look up transaction", operations["Inquiry"]["startButtonText"]!.GetValue<string>());
    }

    [Fact]
    public async Task Modes_environments_and_limits_come_from_core()
    {
        using var h = new Harness(register: false);

        var (_, meta) = await h.Client.GetJson("/api/meta");

        Assert.Equal(new[] { "SIT", "UAT", "PROD" }, meta!["environments"]!.AsArray().Select(e => e!.GetValue<string>()).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4 }, meta["modes"]!.AsArray().Select(m => m!["value"]!.GetValue<int>()).ToArray());
        var usb = meta["modes"]!.AsArray().Single(m => m!["value"]!.GetValue<int>() == 1)!;
        Assert.False(usb["supported"]!.GetValue<bool>());
        Assert.Contains("ships no USB channel", usb["hint"]!.GetValue<string>());
        Assert.Equal("0.010", meta["amount"]!["min"]!.GetValue<string>());
        Assert.Equal("100000.000", meta["amount"]!["max"]!.GetValue<string>());
        Assert.Equal(3, meta["amount"]!["fractionDigits"]!.GetValue<int>());
        Assert.Equal(AmountLimits.MaxDigits, meta["amount"]!["maxDigits"]!.GetValue<int>());
        Assert.Equal(32, meta["merchantReferenceMaxLength"]!.GetValue<int>());
        Assert.Equal(9100, meta["defaultPort"]!.GetValue<int>());
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), meta["today"]!.GetValue<string>());
    }
}

public class FormValidationTests
{
    private static async Task<(HttpStatusCode Status, System.Text.Json.Nodes.JsonNode? Body)> Post(Harness h, object form) =>
        await h.StartAsync(form);

    private static string Error(System.Text.Json.Nodes.JsonNode? body, string field) => body!["errors"]![field]!.GetValue<string>();

    [Theory]
    [InlineData("", "Required field")]
    [InlineData("0", "Required field")]
    [InlineData("1", "minimum amount allowed is 0.010")]
    [InlineData("9", "minimum amount allowed is 0.010")]
    [InlineData("100000001", "maximum amount allowed is 100000.000")]
    public async Task A_sale_amount_is_bounded_with_the_windows_samples_messages(string digits, string message)
    {
        using var h = new Harness();

        var (status, body) = await Post(h, Harness.Sale(digits));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(message, Error(body, "amount"));
        Assert.Empty(h.Terminal.Requests); // nothing reached the terminal
    }

    [Theory]
    [InlineData("10")]
    [InlineData("100000000")]
    public async Task The_bounds_themselves_are_accepted(string digits)
    {
        using var h = new Harness();

        var (status, _) = await Post(h, Harness.Sale(digits));

        Assert.Equal(HttpStatusCode.Accepted, status);
    }

    [Fact]
    public async Task Amount_digits_are_normalised_like_the_amount_field_keeps_them()
    {
        using var h = new Harness();

        var end = await h.RunToEndAsync(Harness.Sale("00 12,34x"));

        Assert.Equal("1.234", end["dialog"]!["amount"]!.GetValue<string>());
        Assert.Equal("000000001234", Assert.Single(h.Terminal.Sent("SALE"))["amount"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("ORDER 1")]
    [InlineData("A&B")]
    [InlineData("A=B")]
    [InlineData("café")]
    public async Task A_merchant_reference_must_be_printable_ascii_without_space_amp_or_equals(string reference)
    {
        using var h = new Harness();

        var (status, body) = await Post(h, Harness.Sale(merchantReference: reference));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("Use printable ASCII without spaces, '&' or '='", Error(body, "merchantReference"));
        Assert.Equal(TransactionFormState.MerchantReferenceError(reference), Error(body, "merchantReference"));
    }

    [Fact]
    public async Task A_merchant_reference_is_at_most_32_characters()
    {
        using var h = new Harness();

        var (status, body) = await Post(h, Harness.Sale(merchantReference: new string('A', 33)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("A merchant reference is at most 32 characters", Error(body, "merchantReference"));
    }

    [Fact]
    public async Task A_void_needs_the_receipt_number_and_optionally_the_other_terminal()
    {
        using var h = new Harness();

        var (_, none) = await Post(h, new { terminalSerial = Harness.Serial, type = "Void" });
        var (_, other) = await Post(h, new { terminalSerial = Harness.Serial, type = "Void", receiptNumber = "24", useOtherTerminal = true });

        Assert.Equal("Enter the receipt number of the transaction to act on", Error(none, "receiptNumber"));
        Assert.False(none!["errors"]!.AsObject().ContainsKey("amount")); // a void takes the original's amount
        Assert.Equal("Enter the terminal the original was taken on", Error(other, "originalTerminalId"));
    }

    [Fact]
    public async Task An_inquiry_asks_for_a_receipt_number_or_when_by_reference_the_reference()
    {
        using var h = new Harness();

        var (_, byReceipt) = await Post(h, new { terminalSerial = Harness.Serial, type = "Inquiry" });
        var (_, byReference) = await Post(h, new { terminalSerial = Harness.Serial, type = "Inquiry", lookUpByReference = true });

        Assert.Equal("Enter the receipt number of the transaction to act on", Error(byReceipt, "receiptNumber"));
        Assert.Equal("Enter the reference the transaction was sent with", Error(byReference, "originalReference"));
        Assert.False(byReference!["errors"]!.AsObject().ContainsKey("receiptNumber")); // hidden fields are not validated
    }

    [Fact]
    public async Task Errors_match_core_form_state_field_for_field()
    {
        using var h = new Harness();
        var form = new TransactionFormState { Type = EcrTransactionType.Refund, UseOtherTerminal = true };
        var (core, _) = form.Validated(Harness.Serial);

        var (_, body) = await Post(h, new { terminalSerial = Harness.Serial, type = "Refund", useOtherTerminal = true });

        Assert.Equal(core.Errors.Amount, Error(body, "amount"));
        Assert.Equal(core.Errors.ReceiptNumber, Error(body, "receiptNumber"));
        Assert.Equal(core.Errors.OriginalTerminalId, Error(body, "originalTerminalId"));
        Assert.Equal(3, body!["errors"]!.AsObject().Count);
    }

    [Fact]
    public async Task Fields_the_type_does_not_show_are_ignored_not_sent()
    {
        using var h = new Harness();

        // A sale carrying leftovers from another form: none of it may travel with the sale.
        var end = await h.RunToEndAsync(new
        {
            terminalSerial = Harness.Serial, type = "Sale", amountDigits = "1234",
            receiptNumber = "999999", originalTerminalId = "42", useOtherTerminal = true, originalReference = "OLD",
        });

        Assert.Equal("completed", end["state"]!.GetValue<string>());
        var sent = Assert.Single(h.Terminal.Sent("SALE"));
        Assert.False(sent.ContainsKey("stan"));
        Assert.False(sent.ContainsKey("originalTerminalId"));
    }

    [Fact]
    public async Task An_unknown_type_or_terminal_is_refused()
    {
        using var h = new Harness();

        var (_, type) = await Post(h, new { terminalSerial = Harness.Serial, type = "Receipt" }); // not a menu choice
        var (_, gone) = await Post(h, new { terminalSerial = "GONE", type = "Sale", amountDigits = "1234" });
        var (_, none) = await Post(h, new { type = "Sale", amountDigits = "1234" });

        Assert.Equal("Choose a transaction type", Error(type, "type"));
        Assert.Equal("Terminal GONE is no longer registered", Error(gone, "terminal"));
        Assert.Equal("Choose a POS terminal", Error(none, "terminal"));
    }

    [Fact]
    public async Task A_terminal_whose_plan_has_issues_cannot_start_and_says_why()
    {
        using var h = new Harness();
        await h.Client.DeleteJson("/api/settings/keys/wifi");
        await h.Client.PostJson("/api/terminals", new { name = "U", serialNumber = "U1", ecrMode = 1 });

        var (_, noKey) = await Post(h, Harness.Sale());
        var (status, usb) = await Post(h, Harness.Sale(serial: "U1"));

        Assert.Contains("Wi-Fi/LAN secure hash key is not configured", noKey!["message"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("USB Cable is not supported in this sample", usb!["message"]!.GetValue<string>());
        Assert.Empty(h.Terminal.Requests);
    }

    [Fact]
    public async Task The_original_date_defaults_to_today_is_sent_as_yyyyMMdd_and_never_from_the_future()
    {
        using var h = new Harness();

        await h.RunToEndAsync(new { terminalSerial = Harness.Serial, type = "Refund", amountDigits = "500", receiptNumber = "24", originalDate = "2026-08-09" });
        await h.RunToEndAsync(new { terminalSerial = Harness.Serial, type = "Refund", amountDigits = "500", receiptNumber = "24" });
        await h.RunToEndAsync(new { terminalSerial = Harness.Serial, type = "Refund", amountDigits = "500", receiptNumber = "24", originalDate = "2999-01-01" });

        var dates = h.Terminal.Sent("REFUND").Select(r => r["originalTransactionDate"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "20260809", DateTime.Today.ToString("yyyyMMdd"), DateTime.Today.ToString("yyyyMMdd") }, dates);
        Assert.Equal("000000000500", h.Terminal.Sent("REFUND")[0]["amount"]!.GetValue<string>());
    }

    [Fact]
    public async Task Receipt_number_and_terminal_id_are_digits_only_like_the_form_fields()
    {
        using var h = new Harness(r => r["messageType"]!.GetValue<string>() == "VOID" ? FakeTerminal.Approved() : FakeTerminal.Found());

        await h.RunToEndAsync(new { terminalSerial = Harness.Serial, type = "Void", receiptNumber = "0-2 4x", useOtherTerminal = true, originalTerminalId = "31a629" });

        var sent = Assert.Single(h.Terminal.Sent("VOID"));
        Assert.Equal("000024", sent["stan"]!.GetValue<string>());
        Assert.Equal("31629", sent["originalTerminalId"]!.GetValue<string>());
    }
}
