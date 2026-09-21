using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AmwalECR.WebExample.Tests;

/// <summary>
/// A POS terminal on loopback that speaks the framed, signed ECR protocol — the C#
/// counterpart of tools/fake_pos_server_signed.py, so the whole flow (plan, probe,
/// sign, send, verify, classify) runs against the real SDK.
/// <para>
/// Copied from AmwalECR.Example.Tests/EngineTests.cs rather than referenced: that file
/// also holds the WPF sample's own test classes, and pulling it in would run them
/// twice.
/// </para>
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

    public IReadOnlyList<JsonObject> Sent(string messageType)
    {
        lock (Requests) return Requests.Where(r => r["messageType"]!.GetValue<string>() == messageType).ToList();
    }

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

    /// <summary>The terminal settled only part of the amount (what fake_pos_server --partial does).</summary>
    public static JsonObject Partial(string requested, string settled) => new()
    {
        ["success"] = true,
        ["responseCode"] = "00",
        ["message"] = "Approved",
        ["data"] = new JsonObject
        {
            ["amount"] = requested, ["authorizeAmount"] = settled, ["isPartialApprove"] = true,
            ["authCode"] = "123456", ["cardMask"] = "483312******1234", ["rrn"] = "1",
        },
    };

    public static JsonObject Declined(string code = "51") => new()
    {
        ["success"] = false,
        ["responseCode"] = code,
        ["message"] = "Declined",
        ["data"] = null,
        ["errorList"] = new JsonArray($"Declined by the issuer ({code})"),
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

    public static JsonObject NotFound() => new()
    {
        ["success"] = false,
        ["responseCode"] = "25",
        ["message"] = "No transactions found for the provided STAN and Terminal",
        ["data"] = null,
    };

    public static JsonObject Receipt(string url) => new()
    {
        ["success"] = true, ["responseCode"] = "00", ["message"] = "Receipt ready",
        ["data"] = new JsonObject { ["receiptUrl"] = url },
    };

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
