using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AmwalECR.WebExample.Api;

namespace AmwalECR.WebExample.Tests;

public class ReceiptTests
{
    private const string ReceiptUrl = "https://example.invalid/r?stan=000024";

    private static string MessageType(JsonObject r) => r["messageType"]!.GetValue<string>();

    private static object InquiryByReceipt => new
    {
        terminalSerial = Harness.Serial,
        type = "Inquiry",
        receiptNumber = "24",
        originalDate = "2026-08-09",
    };

    [Fact]
    public async Task An_inquired_transaction_can_fetch_its_receipt_and_the_qr_renders_it()
    {
        using var h = new Harness(r => MessageType(r) == "RECEIPT" ? FakeTerminal.Receipt(ReceiptUrl) : FakeTerminal.Found());
        var id = await h.StartOkAsync(InquiryByReceipt);
        var inquired = await h.WaitFinishedAsync(id);
        Assert.True(inquired["dialog"]!["receipt"]!["showButton"]!.GetValue<bool>());
        Assert.Contains("Can be voided", inquired["dialog"]!["hints"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("Can be refunded", inquired["dialog"]!["hints"]!.AsArray().Select(x => x!.GetValue<string>()));

        var (status, _) = await h.Client.PostJson($"/api/transactions/{id}/receipt");
        var ready = await h.WaitAsync(id, v => v["dialog"]!["receipt"]!["url"] != null);

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(ReceiptUrl, ready["dialog"]!["receipt"]!["url"]!.GetValue<string>());
        Assert.False(ready["dialog"]!["receipt"]!["fetching"]!.GetValue<bool>());
        Assert.False(ready["dialog"]!["receipt"]!["showButton"]!.GetValue<bool>());
        Assert.Equal("000024", Assert.Single(h.Terminal.Sent("RECEIPT"))["stan"]!.GetValue<string>());

        var response = await h.Client.GetAsync("/api/qr?text=" + Uri.EscapeDataString(ReceiptUrl));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        var svg = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("<svg", svg.TrimStart());
        Assert.Contains("<rect", svg);
    }

    [Fact]
    public async Task A_receipt_the_terminal_cannot_produce_is_reported_not_thrown()
    {
        using var h = new Harness(r => MessageType(r) == "RECEIPT"
            ? new JsonObject { ["success"] = false, ["responseCode"] = "91", ["message"] = "The receipt could not be generated", ["data"] = null }
            : FakeTerminal.Found());
        var id = await h.StartOkAsync(InquiryByReceipt);
        await h.WaitFinishedAsync(id);

        await h.Client.PostJson($"/api/transactions/{id}/receipt");
        var end = await h.WaitAsync(id, v => v["dialog"]!["receipt"]!["problem"] != null);

        Assert.NotEmpty(end["dialog"]!["receipt"]!["problem"]!.GetValue<string>());
        Assert.Null(end["dialog"]!["receipt"]!["url"]);
    }

    [Fact]
    public async Task A_receipt_is_only_offered_for_an_inquiry_result()
    {
        using var h = new Harness();
        var id = await h.StartOkAsync(); // a sale
        var sale = await h.WaitFinishedAsync(id);

        var (status, _) = await h.Client.PostJson($"/api/transactions/{id}/receipt");
        var (missing, _) = await h.Client.PostJson("/api/transactions/nope/receipt");

        Assert.False(sale["dialog"]!["offersReceipt"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Empty(h.Terminal.Sent("RECEIPT"));
    }

    [Fact]
    public async Task A_second_receipt_request_while_one_is_being_fetched_is_not_sent()
    {
        var gate = new SemaphoreSlim(0);
        using var h = new Harness(r =>
        {
            if (MessageType(r) != "RECEIPT") return FakeTerminal.Found();
            gate.Wait(TimeSpan.FromSeconds(15));
            return FakeTerminal.Receipt(ReceiptUrl);
        });
        var id = await h.StartOkAsync(InquiryByReceipt);
        await h.WaitFinishedAsync(id);

        var (first, _) = await h.Client.PostJson($"/api/transactions/{id}/receipt");
        var (second, _) = await h.Client.PostJson($"/api/transactions/{id}/receipt");
        gate.Release(2);
        await h.WaitAsync(id, v => v["dialog"]!["receipt"]!["url"] != null);

        Assert.Equal(HttpStatusCode.Accepted, first);
        Assert.Equal(HttpStatusCode.Conflict, second);
        Assert.Single(h.Terminal.Sent("RECEIPT"));
    }
}

public class QrTests
{
    [Fact]
    public async Task The_qr_is_an_svg()
    {
        using var h = new Harness(register: false);

        var response = await h.Client.GetAsync("/api/qr?text=https%3A%2F%2Fexample.invalid%2Freceipt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<svg", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Nothing_to_encode_is_a_bad_request(string text)
    {
        using var h = new Harness(register: false);

        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/qr?text=" + Uri.EscapeDataString(text))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/qr")).StatusCode);
    }

    [Fact]
    public async Task Over_long_text_is_refused_and_the_limit_itself_is_accepted()
    {
        using var h = new Harness(register: false);

        var over = await h.Client.GetAsync("/api/qr?text=" + new string('a', LogAndQrEndpoints.MaxQrTextLength + 1));
        var limit = await h.Client.GetAsync("/api/qr?text=" + new string('a', LogAndQrEndpoints.MaxQrTextLength));

        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Equal(HttpStatusCode.OK, limit.StatusCode);
    }
}

public class LogTests
{
    [Fact]
    public async Task The_log_shows_the_wire_traffic_and_never_a_key()
    {
        using var h = new Harness();
        await h.RunToEndAsync(Harness.Sale(merchantReference: "ORDER-L"));

        var (_, log) = await h.Client.GetJson("/api/log");
        var text = log!["text"]!.GetValue<string>();

        Assert.Contains("Sending SALE ORDER-L", text);
        Assert.Contains("signing key (Wi-Fi secure hash key): configured", text);
        Assert.DoesNotContain(FakeTerminal.Key, text, StringComparison.OrdinalIgnoreCase);
        Assert.True(log["count"]!.GetValue<int>() > 5);
        Assert.Equal(500, log["capacity"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_log_is_bounded_to_500_lines_and_keeps_the_newest()
    {
        using var h = new Harness(register: false);
        for (var i = 0; i < 620; i++) h.App.Log.Append("Test", $"line {i}");

        var (_, log) = await h.Client.GetJson("/api/log");
        var text = log!["text"]!.GetValue<string>();

        Assert.Equal(500, log["count"]!.GetValue<int>());
        Assert.Equal(500, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("line 119", text);
        Assert.Contains("line 120", text);
        Assert.Contains("line 619", text);
    }

    [Fact]
    public async Task The_log_can_be_cleared()
    {
        using var h = new Harness();
        await h.RunToEndAsync();

        var (status, _) = await h.Client.DeleteJson("/api/log");
        var (_, log) = await h.Client.GetJson("/api/log");

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal(0, log!["count"]!.GetValue<int>());
        Assert.Equal("", log["text"]!.GetValue<string>());
    }
}

public class SecurityTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/app.css")]
    [InlineData("/api/meta")]
    [InlineData("/api/nope")]
    public async Task Every_response_carries_the_hardening_headers(string path)
    {
        using var h = new Harness(register: false);

        var response = await h.Client.GetAsync(path);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("img-src 'self' data:", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        if (path.StartsWith("/api")) Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
    }

    [Fact]
    public async Task No_cors_is_offered()
    {
        using var h = new Harness(register: false);

        var get = await h.Client.GetAsync("/api/meta");
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/transactions");
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        var options = await h.Client.SendAsync(preflight);

        Assert.False(get.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(options.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.NotEqual(HttpStatusCode.OK, options.StatusCode);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    public async Task Mutating_calls_must_be_json(string contentType)
    {
        using var h = new Harness();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions")
        {
            Content = new StringContent("{\"terminalSerial\":\"TW1234567890\",\"type\":\"Sale\",\"amountDigits\":\"1234\"}"),
        };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

        var response = await h.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(h.Terminal.Requests); // a cross-site form post cannot take a payment
        var (_, pending) = await h.Client.GetJson("/api/transactions");
        Assert.Empty(pending!["transactions"]!.AsArray());
    }

    [Fact]
    public async Task A_body_less_mutating_call_without_json_is_refused_too()
    {
        using var h = new Harness();

        var response = await h.Client.PostAsync("/api/transactions/x/cancel", content: null);
        var delete = await h.Client.DeleteAsync("/api/log");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, delete.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:6666")]
    [InlineData("null")]
    public async Task A_request_another_origin_made_is_refused(string origin)
    {
        using var h = new Harness();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/transactions")
        {
            Content = JsonContent(Harness.Sale()),
        };
        request.Headers.Add("Origin", origin);

        var response = await h.Client.SendAsync(request);
        using var read = new HttpRequestMessage(HttpMethod.Get, "/api/log");
        read.Headers.Add("Origin", origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.SendAsync(read)).StatusCode);
        Assert.Empty(h.Terminal.Requests);
    }

    [Fact]
    public async Task Only_the_pages_own_origin_is_accepted_and_every_other_fetch_site_is_refused()
    {
        using var h = new Harness();
        using var same = new HttpRequestMessage(HttpMethod.Get, "/api/meta");
        same.Headers.Add("Origin", "http://localhost");
        same.Headers.Add("Sec-Fetch-Site", "same-origin");
        Assert.Equal(HttpStatusCode.OK, (await h.Client.SendAsync(same)).StatusCode);

        // "same-site" is refused too, on purpose: a sibling site on the same registrable domain
        // is not this page, and this API takes payments. Only same-origin and none (a typed URL
        // or bookmark) pass.
        foreach (var site in new[] { "same-site", "cross-site" })
        {
            using var other = new HttpRequestMessage(HttpMethod.Get, "/api/meta");
            other.Headers.Add("Sec-Fetch-Site", site);
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.SendAsync(other)).StatusCode);
        }
    }

    [Fact]
    public async Task An_unexpected_host_name_is_refused_which_defeats_dns_rebinding()
    {
        using var h = new Harness(register: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/meta");
        request.Headers.Host = "evil.example";

        var response = await h.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_page_has_no_inline_script_no_inline_style_and_no_cdn()
    {
        using var h = new Harness(register: false);
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AmwalECR.WebExample", "wwwroot");

        var html = await h.Client.GetStringAsync("/");
        Assert.Matches(new Regex("<script[^>]+src=\"app\\.js\"", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex("<script(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex("\\sstyle\\s*=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex("\\son[a-z]+\\s*=\\s*\"", RegexOptions.IgnoreCase), html); // no inline handlers

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".html") || f.EndsWith(".js") || f.EndsWith(".css")))
        {
            var text = await File.ReadAllTextAsync(file);
            var external = Regex.Matches(text, "https?://[^\\s'\")]+").Select(m => m.Value)
                .Where(u => !u.StartsWith("http://www.w3.org/") && !u.StartsWith("https://example.invalid")).ToList();
            Assert.True(external.Count == 0, $"{Path.GetFileName(file)} refers to {string.Join(", ", external)}");
            Assert.DoesNotContain("innerHTML", text); // everything server-supplied goes through textContent
            Assert.DoesNotContain("eval(", text);
        }
    }

    [Fact]
    public async Task Server_errors_do_not_leak_details()
    {
        using var h = new Harness(register: false);
        var bad = new HttpRequestMessage(HttpMethod.Post, "/api/terminals") { Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json") };

        var response = await h.Client.SendAsync(bad);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("at AmwalECR", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_api_path_is_a_json_404()
    {
        using var h = new Harness(register: false);

        var (status, body) = await h.Client.GetJson("/api/nothing/here");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("No such endpoint", body!["error"]!.GetValue<string>());
    }

    private static StringContent JsonContent(object value) =>
        new(System.Text.Json.JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
}
