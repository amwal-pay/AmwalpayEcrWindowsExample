using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Services;
using AmwalECR.Example.Core.Transaction;
using AmwalPay.Ecr;
using Xunit;

namespace AmwalECR.Example.Tests;

/// <summary>
/// A POS terminal on loopback that speaks the framed, signed ECR protocol — the
/// C# counterpart of tools/fake_pos_server_signed.py — so the whole flow (plan,
/// probe, sign, send, verify, classify) runs against the real SDK.
/// </summary>
internal sealed class FakeTerminal : IDisposable
{
    public const string Key = "0123456789ABCDEF0123456789ABCDEF";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<JsonObject, JsonObject?> _handler;

    public FakeTerminal(Func<JsonObject, JsonObject?> handler)
    {
        _handler = handler;
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<JsonObject> Requests { get; } = new();

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = new byte[2];
                if (!ReadFully(stream, header)) return; // a reachability probe: connect and leave
                var body = new byte[(header[0] << 8) | header[1]];
                if (!ReadFully(stream, body)) return;

                var request = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(body))!;
                lock (Requests) Requests.Add(request);

                var answer = _handler(request);
                if (answer == null) return; // close without answering: the outcome is unknown

                answer["nonce"] = request["nonce"]!.GetValue<string>();
                answer["secureHash"] = Sign(answer);
                var bytes = Encoding.UTF8.GetBytes(answer.ToJsonString());
                stream.Write(new[] { (byte)(bytes.Length >> 8), (byte)(bytes.Length & 0xFF) });
                stream.Write(bytes);
            }
            catch (IOException) { }
        }
    }

    private static bool ReadFully(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>Sorted top-level scalar key=value pairs joined by '&amp;', HMAC-SHA256 under the hex-decoded key, upper-case hex.</summary>
    public static string Sign(JsonObject json)
    {
        var pairs = json
            .Where(e => e.Key != "secureHash" && e.Value is JsonValue)
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => e.Key + "=" + (e.Value!.GetValue<object>() is string s ? s : e.Value!.ToJsonString()));
        using var hmac = new HMACSHA256(Convert.FromHexString(Key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(string.Join("&", pairs))));
    }

    public static JsonObject Approved(string amount = "1.234") => new()
    {
        ["success"] = true,
        ["responseCode"] = "00",
        ["message"] = "Approved",
        ["data"] = new JsonObject { ["amount"] = amount, ["authCode"] = "123456", ["cardMask"] = "483312******1234", ["rrn"] = "1" },
    };

    public static JsonObject Found(string stan = "000024") => new()
    {
        ["success"] = true,
        ["responseCode"] = "00",
        ["message"] = "Transaction found",
        ["data"] = new JsonObject
        {
            ["transactionId"] = "id", ["stan"] = stan, ["transactionType"] = "Purchase", ["status"] = "Approved",
            ["amount"] = "1.234", ["transactionTime"] = "2026-08-09T16:58:16", ["cardNumber"] = "483312******1234",
            ["canVoid"] = true, ["canRefund"] = true,
        },
    };

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

public class TransactionEngineTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<string> _log = new();

    public void Dispose() => _dir.Dispose();

    private TransactionEngine Engine(int port, string wifiKey = FakeTerminal.Key, string host = "127.0.0.1", int mode = 2)
    {
        var repo = new TerminalRepository(_dir.File("terminals.json"));
        repo.Save(new Terminal { SerialNumber = "TW1", Name = "Till", EcrModeValue = mode, IpAddress = host, Port = port }, null);
        var settings = new EcrExampleSettings(_dir.File("settings.dat"), new FakeProtector()) { WifiSecureHashKey = wifiKey };
        var logger = EcrLogger.From(m => { lock (_log) _log.Add(m); });
        return new TransactionEngine(repo, settings, logger, logger);
    }

    private static TransactionRequest Sale(decimal amount = 1.234m, string reference = "") => new()
    {
        Type = EcrTransactionType.Sale, TerminalSerial = "TW1", Amount = amount, MerchantReference = reference,
    };

    private static Task<TransactionState> Run(TransactionEngine engine, TransactionRequest request, List<TransactionState>? progress = null, CancellationToken ct = default) =>
        engine.RunAsync(request, s => progress?.Add(s), ct);

    [Fact]
    public async Task A_sale_is_signed_sent_verified_and_read_as_approved()
    {
        using var terminal = new FakeTerminal(_ => FakeTerminal.Approved());
        var progress = new List<TransactionState>();

        var state = await Run(Engine(terminal.Port), Sale(reference: "ORDER-1"), progress);

        var completed = Assert.IsType<TransactionState.Completed>(state);
        var approved = Assert.IsType<EcrResult.Approved>(completed.Result);
        Assert.Equal("1.234", approved.Amount);
        Assert.Equal("ORDER-1", approved.MerchantReference);
        Assert.IsType<TransactionState.InProgress>(Assert.Single(progress));
        var sent = Assert.Single(terminal.Requests);
        Assert.Equal("SALE", sent["messageType"]!.GetValue<string>());
        Assert.Equal("000000001234", sent["amount"]!.GetValue<string>());
        Assert.Equal("TW1", sent["terminalSerial"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_blank_reference_is_generated_by_the_app_so_it_is_known_even_if_the_wait_is_cancelled()
    {
        using var terminal = new FakeTerminal(_ => FakeTerminal.Approved());

        await Run(Engine(terminal.Port), Sale());

        var reference = Assert.Single(terminal.Requests)["merchantReference"]!.GetValue<string>();
        Assert.Equal(12, reference.Length);
    }

    [Fact]
    public async Task An_unreachable_terminal_is_reported_before_anything_is_sent()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var progress = new List<TransactionState>();

        var state = await Run(Engine(port), Sale(), progress);

        var failed = Assert.IsType<TransactionState.Failed>(state);
        Assert.Contains("is not reachable", failed.Message);
        Assert.Contains("Check:", failed.Message);
        Assert.Null(failed.InquirableReference);
        Assert.Empty(progress); // never reached "waiting for the terminal"
    }

    [Fact]
    public async Task A_plan_with_issues_is_refused_without_touching_the_network()
    {
        using var terminal = new FakeTerminal(_ => FakeTerminal.Approved());

        var state = await Run(Engine(terminal.Port, wifiKey: ""), Sale());

        var failed = Assert.IsType<TransactionState.Failed>(state);
        Assert.Contains("Wi-Fi/LAN secure hash key is not configured", failed.Message);
        Assert.Empty(terminal.Requests);
    }

    [Fact]
    public async Task A_usb_terminal_is_refused_as_unsupported()
    {
        var state = await Run(Engine(0, mode: 1), Sale());

        Assert.Contains("USB Cable is not supported in this sample", Assert.IsType<TransactionState.Failed>(state).Message);
    }

    [Fact]
    public async Task A_terminal_that_is_no_longer_registered_is_reported()
    {
        using var terminal = new FakeTerminal(_ => FakeTerminal.Approved());
        var engine = Engine(terminal.Port);

        var state = await Run(engine, Sale() with { TerminalSerial = "GONE" });

        Assert.Equal("Terminal GONE is no longer registered", Assert.IsType<TransactionState.Failed>(state).Message);
    }

    [Fact]
    public async Task A_lost_answer_is_an_unknown_outcome_with_a_reference_to_look_up_never_a_retry()
    {
        // The terminal takes the request and hangs up. The SDK asks what became of it
        // (also unanswered); the screen must end up offering only the lookup.
        using var terminal = new FakeTerminal(_ => null);

        var state = await Run(Engine(terminal.Port), Sale(reference: "ORDER-2"));

        var failed = Assert.IsType<TransactionState.Failed>(state);
        Assert.Equal("ORDER-2", failed.InquirableReference);
        Assert.Contains(terminal.Requests, r => r["messageType"]!.GetValue<string>() == "SALE");
        Assert.Equal(1, terminal.Requests.Count(r => r["messageType"]!.GetValue<string>() == "SALE")); // never sent twice
    }

    [Fact]
    public async Task A_lost_answer_the_terminal_can_account_for_is_shown_as_the_transaction_it_found()
    {
        using var terminal = new FakeTerminal(r => r["messageType"]!.GetValue<string>() == "INQUIRY" ? FakeTerminal.Found() : null);

        var state = await Run(Engine(terminal.Port), Sale(reference: "ORDER-3"));

        var inquired = Assert.IsType<TransactionState.Inquired>(state);
        Assert.True(inquired.RecoveredFromFailure);
        Assert.IsType<EcrInquiry.Found>(inquired.Inquiry);
        Assert.Equal("ORDER-3", inquired.Request.OriginalReference);
    }

    [Fact]
    public async Task An_answer_signed_with_another_key_is_not_believed_and_is_an_unknown_outcome()
    {
        using var terminal = new FakeTerminal(_ => FakeTerminal.Approved());

        var state = await Run(Engine(terminal.Port, wifiKey: "FEDCBA9876543210FEDCBA9876543210"), Sale(reference: "ORDER-4"));

        var failed = Assert.IsType<TransactionState.Failed>(state);
        Assert.Contains("not signed with this terminal's key", failed.Message);
        Assert.Equal("ORDER-4", failed.InquirableReference);
    }

    [Fact]
    public async Task Cancelling_during_the_wait_reports_an_unknown_outcome_not_a_cancelled_sale()
    {
        var reached = new SemaphoreSlim(0);
        var release = new SemaphoreSlim(0);
        using var terminal = new FakeTerminal(_ =>
        {
            reached.Release();
            release.Wait(TimeSpan.FromSeconds(10));
            return null;
        });
        using var cts = new CancellationTokenSource();

        var run = Run(Engine(terminal.Port), Sale(reference: "ORDER-5"), ct: cts.Token);
        Assert.True(await reached.WaitAsync(TimeSpan.FromSeconds(10)));
        cts.Cancel();
        var state = await run;
        release.Release();

        var failed = Assert.IsType<TransactionState.Failed>(state);
        Assert.Equal("ORDER-5", failed.InquirableReference);
        Assert.Contains("does not cancel the transaction", failed.Message);
    }

    [Fact]
    public async Task Cancelling_before_anything_is_sent_returns_to_idle()
    {
        using var terminal = new FakeTerminal(_ => FakeTerminal.Approved());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var state = await Run(Engine(terminal.Port), Sale(), ct: cts.Token);

        Assert.IsType<TransactionState.Idle>(state);
        Assert.DoesNotContain(terminal.Requests, r => r["messageType"]!.GetValue<string>() == "SALE");
    }

    [Fact]
    public async Task An_inquiry_by_receipt_number_sends_the_date_and_reads_the_finding()
    {
        using var terminal = new FakeTerminal(_ => FakeTerminal.Found());
        var request = new TransactionRequest
        {
            Type = EcrTransactionType.Inquiry, TerminalSerial = "TW1", ReceiptNumber = "24", TransactionDate = "20260809",
        };

        var state = await Run(Engine(terminal.Port), request);

        var inquired = Assert.IsType<TransactionState.Inquired>(state);
        Assert.False(inquired.RecoveredFromFailure);
        var found = Assert.IsType<EcrInquiry.Found>(inquired.Inquiry);
        Assert.Equal("000024", found.Transaction.Stan);
        var sent = Assert.Single(terminal.Requests);
        Assert.Equal("000024", sent["stan"]!.GetValue<string>());
        Assert.Equal("20260809", sent["originalTransactionDate"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_inquiry_by_reference_names_the_original_reference()
    {
        using var terminal = new FakeTerminal(_ => FakeTerminal.Found());
        var request = new TransactionRequest
        {
            Type = EcrTransactionType.Inquiry, TerminalSerial = "TW1", OriginalReference = "ORDER-6", MerchantReference = "",
        };

        await Run(Engine(terminal.Port), request);

        Assert.Equal("ORDER-6", Assert.Single(terminal.Requests)["originalMerchantReference"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_void_and_a_refund_send_their_own_fields()
    {
        using var terminal = new FakeTerminal(_ => FakeTerminal.Approved());
        var engine = Engine(terminal.Port);

        await Run(engine, new TransactionRequest { Type = EcrTransactionType.Void, TerminalSerial = "TW1", ReceiptNumber = "24" });
        await Run(engine, new TransactionRequest
        {
            Type = EcrTransactionType.Refund, TerminalSerial = "TW1", Amount = 0.5m, ReceiptNumber = "24", TransactionDate = "20260809",
        });

        Assert.Equal(new[] { "VOID", "REFUND" }, terminal.Requests.Select(r => r["messageType"]!.GetValue<string>()));
        Assert.False(terminal.Requests[0].ContainsKey("amount"));
        Assert.Equal("000000000500", terminal.Requests[1]["amount"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_receipt_is_fetched_by_the_receipt_number_and_day_the_inquiry_found()
    {
        using var terminal = new FakeTerminal(r => r["messageType"]!.GetValue<string>() == "RECEIPT"
            ? new JsonObject
            {
                ["success"] = true, ["responseCode"] = "00", ["message"] = "Receipt ready",
                ["data"] = new JsonObject { ["receiptUrl"] = "https://example.invalid/r?stan=000024" },
            }
            : FakeTerminal.Found());
        var engine = Engine(terminal.Port);
        var inquired = Assert.IsType<TransactionState.Inquired>(await Run(engine, new TransactionRequest
        {
            Type = EcrTransactionType.Inquiry, TerminalSerial = "TW1", ReceiptNumber = "24", TransactionDate = "20260101",
        }));

        var receipt = await engine.FetchReceiptAsync(inquired, CancellationToken.None);

        Assert.Equal("https://example.invalid/r?stan=000024", Assert.IsType<EcrReceipt.Ready>(receipt).Url);
        var sent = terminal.Requests.Last();
        Assert.Equal("RECEIPT", sent["messageType"]!.GetValue<string>());
        Assert.Equal("000024", sent["stan"]!.GetValue<string>());
        Assert.Equal("20260809", sent["originalTransactionDate"]!.GetValue<string>()); // the found day, not the typed one
    }

    [Fact]
    public async Task A_receipt_that_cannot_be_fetched_is_unavailable_not_an_exception()
    {
        var engine = Engine(1);
        var state = new TransactionState.Inquired(
            new EcrInquiry.Found("R", new EcrTransaction("", "", "", "Approved", "1", "1", "", "", "", "", "", "", "", "", false, false, false), "{}"),
            new TransactionRequest { Type = EcrTransactionType.Inquiry, TerminalSerial = "TW1" });

        var receipt = await engine.FetchReceiptAsync(state, CancellationToken.None);

        Assert.Contains("no receipt number", Assert.IsType<EcrReceipt.Unavailable>(receipt).Reason);
    }

    [Fact]
    public void Selecting_a_terminal_logs_its_config_and_issues()
    {
        var engine = Engine(9100, wifiKey: "");
        var config = engine.ResolveConfig(new TerminalRepository(_dir.File("terminals.json")).GetAll()[0]);

        engine.LogSelectedConfig(config);

        Assert.Contains(_log, l => l.Contains("selected TW1"));
        Assert.Contains(_log, l => l.Contains("config issue: Wi-Fi/LAN secure hash key is not configured"));
    }
}
