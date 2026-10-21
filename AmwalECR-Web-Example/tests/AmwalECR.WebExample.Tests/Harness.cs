using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AmwalECR.WebExample.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AmwalECR.WebExample.Tests;

/// <summary>The app hosted in-process on a temp data folder.</summary>
internal sealed class AppFactory : WebApplicationFactory<Program>
{
    private readonly TimeProvider? _time;

    public AppFactory(string dataDir, TimeProvider? time = null)
    {
        DataDir = dataDir;
        _time = time;
    }

    public string DataDir { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = DataDir }));

        if (_time != null)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(_time);
            });
        }
    }
}

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal static class Http
{
    public static async Task<JsonNode?> Json(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync());

    public static async Task<(HttpStatusCode Status, JsonNode? Body)> Send(
        this HttpClient client, HttpMethod method, string url, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, url);
        if (method != HttpMethod.Get)
        {
            request.Content = JsonContent.Create(body ?? new { });
        }

        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    public static Task<(HttpStatusCode Status, JsonNode? Body)> GetJson(this HttpClient c, string url) => c.Send(HttpMethod.Get, url);

    public static Task<(HttpStatusCode Status, JsonNode? Body)> PostJson(this HttpClient c, string url, object? body = null) =>
        c.Send(HttpMethod.Post, url, body);

    public static Task<(HttpStatusCode Status, JsonNode? Body)> PutJson(this HttpClient c, string url, object? body) =>
        c.Send(HttpMethod.Put, url, body);

    public static Task<(HttpStatusCode Status, JsonNode? Body)> DeleteJson(this HttpClient c, string url) =>
        c.Send(HttpMethod.Delete, url);
}

/// <summary>
/// A host, a data folder, a loopback terminal and (by default) one registered Wi‑Fi
/// terminal with the Wi-Fi key saved — through the app's own API, as a user would.
/// </summary>
internal sealed class Harness : IDisposable
{
    public const string Serial = "TW1234567890";

    private readonly string _dataDir;
    private readonly bool _ownsDataDir;

    public Harness(Func<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonObject?>? handler = null, TimeProvider? time = null, bool register = true, string? dataDir = null)
    {
        // A folder passed in belongs to the test, which may open a second host on it.
        _ownsDataDir = dataDir == null;
        _dataDir = dataDir ?? Path.Combine(Path.GetTempPath(), "ecr-web-" + Guid.NewGuid().ToString("N"));
        Terminal = new FakeTerminal(handler ?? (_ => FakeTerminal.Approved()));
        Factory = new AppFactory(_dataDir, time);
        Client = Factory.CreateClient();

        if (register)
        {
            RegisterAsync(Serial, Terminal.Port).GetAwaiter().GetResult();
            SaveKeyAsync().GetAwaiter().GetResult();
        }
    }

    public FakeTerminal Terminal { get; }

    public AppFactory Factory { get; }

    public HttpClient Client { get; }

    public string DataDir => _dataDir;

    public EcrApp App => Factory.Services.GetRequiredService<EcrApp>();

    public async Task RegisterAsync(string serial, int port, string name = "Demo terminal")
    {
        var (status, body) = await Client.PostJson("/api/terminals", new
        {
            name,
            serialNumber = serial,
            ecrMode = 2,
            ipAddress = "127.0.0.1",
            port = port.ToString(),
        });
        Assert.True(status == HttpStatusCode.OK, body?.ToJsonString());
    }

    public async Task SaveKeyAsync(string key = FakeTerminal.Key)
    {
        var (status, body) = await Client.PutJson("/api/settings/keys/wifi", new { value = key });
        Assert.True(status == HttpStatusCode.OK, body?.ToJsonString());
    }

    public Task<(HttpStatusCode Status, JsonNode? Body)> StartAsync(object? form = null) =>
        Client.PostJson("/api/transactions", form ?? Sale());

    public static object Sale(string amountDigits = "1234", string? merchantReference = null, string serial = Serial) => new
    {
        terminalSerial = serial,
        type = "Sale",
        amountDigits,
        merchantReference,
    };

    /// <summary>Starts a transaction and returns its id.</summary>
    public async Task<string> StartOkAsync(object? form = null)
    {
        var (status, body) = await StartAsync(form);
        Assert.True(status == HttpStatusCode.Accepted, body?.ToJsonString());
        return body!["id"]!.GetValue<string>();
    }

    /// <summary>Polls the way the page does until <paramref name="until"/> holds.</summary>
    public async Task<JsonNode> WaitAsync(string id, Func<JsonNode, bool> until, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        JsonNode? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var (status, body) = await Client.GetJson($"/api/transactions/{id}");
            Assert.Equal(HttpStatusCode.OK, status);
            last = body!;
            if (until(last)) return last;
            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for the transaction; last state: " + last?.ToJsonString());
    }

    public Task<JsonNode> WaitFinishedAsync(string id) => WaitAsync(id, v => v["busy"]!.GetValue<bool>() == false);

    public async Task<JsonNode> RunToEndAsync(object? form = null) => await WaitFinishedAsync(await StartOkAsync(form));

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        Terminal.Dispose();
        if (_ownsDataDir)
        {
            try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
        }
    }
}
