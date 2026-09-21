using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text;
using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Terminals;

namespace AmwalECR.WebExample.Tests;

public class TerminalTests
{
    private static object Wifi(string name = "Till 1", string serial = "S1", string ip = "127.0.0.1", string port = "9100") =>
        new { name, serialNumber = serial, ecrMode = 2, ipAddress = ip, port };

    [Fact]
    public async Task A_terminal_is_added_listed_summarised_and_persisted_by_serial()
    {
        using var h = new Harness(register: false);

        var (status, body) = await h.Client.PostJson("/api/terminals", Wifi(port: "19100"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("127.0.0.1:19100", body!["connectionSummary"]!.GetValue<string>());
        Assert.Equal("Till 1 (S1)", body["displayName"]!.GetValue<string>());
        Assert.Equal("Wi‑Fi", body["modeLabel"]!.GetValue<string>());

        var (_, list) = await h.Client.GetJson("/api/terminals");
        Assert.Equal("S1", Assert.Single(list!["terminals"]!.AsArray())!["serialNumber"]!.GetValue<string>());
        Assert.Contains("\"serialNumber\": \"S1\"", File.ReadAllText(Path.Combine(h.DataDir, "terminals.json")));
    }

    [Fact]
    public async Task A_blank_port_means_the_standard_9100()
    {
        using var h = new Harness(register: false);

        var (_, body) = await h.Client.PostJson("/api/terminals", Wifi(port: ""));

        Assert.Equal("127.0.0.1:9100", body!["connectionSummary"]!.GetValue<string>());
        Assert.Equal(9100, body["port"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(2, "", "", "", "", "", "name", "Enter a terminal name")]
    [InlineData(2, "T", "", "127.0.0.1", "", "", "serial", "Enter the terminal serial number")]
    [InlineData(2, "T", "S", "", "", "", "ip", "Enter the terminal IP address")]
    [InlineData(2, "T", "S", "999.1.1.1", "", "", "ip", "Enter a valid IPv4 address, for example 192.168.1.50")]
    [InlineData(2, "T", "S", "10.0.0.1", "70000", "", "port", "Port must be between 1 and 65535")]
    [InlineData(4, "T", "S", "", "", "", "terminalId", "Enter the backend terminal ID")]
    [InlineData(4, "T", "S", "", "", "abc", "terminalId", "The terminal ID must be numeric")]
    public async Task Validation_reports_the_same_messages_as_the_windows_sample(
        int mode, string name, string serial, string ip, string port, string terminalId, string field, string message)
    {
        using var h = new Harness(register: false);

        var (status, body) = await h.Client.PostJson("/api/terminals", new
        {
            name, serialNumber = serial, ecrMode = mode, ipAddress = ip, port, terminalId, merchantId = "1",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(message, body!["errors"]![field]!.GetValue<string>());
        // ...and it is Core's own answer, not a second copy of the rule.
        var core = TerminalValidator.Validate(name, serial, (EcrMode)mode, ip, port, terminalId, "1");
        Assert.Equal(body["errors"]!.AsObject().Count, new[] { core.Name, core.Serial, core.Ip, core.Port, core.TerminalId, core.MerchantId }.Count(e => e != null));
    }

    [Fact]
    public async Task A_web_service_terminal_needs_numeric_merchant_and_terminal_ids_and_summarises_them()
    {
        using var h = new Harness(register: false);

        var (bad, badBody) = await h.Client.PostJson("/api/terminals", new { name = "W", serialNumber = "W1", ecrMode = 4, merchantId = "", terminalId = "9" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad);
        Assert.Equal("Enter the merchant ID", badBody!["errors"]!["merchantId"]!.GetValue<string>());

        var (ok, body) = await h.Client.PostJson("/api/terminals", new { name = "W", serialNumber = "W1", ecrMode = 4, merchantId = "12", terminalId = "34" });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("Merchant 12 · Terminal 34", body!["connectionSummary"]!.GetValue<string>());
    }

    [Fact]
    public async Task Usb_and_bluetooth_can_be_registered_but_are_marked_unsupported()
    {
        using var h = new Harness(register: false);

        var (_, usb) = await h.Client.PostJson("/api/terminals", new { name = "U", serialNumber = "U1", ecrMode = 1 });
        var (_, bt) = await h.Client.PostJson("/api/terminals", new { name = "B", serialNumber = "B1", ecrMode = 3 });

        Assert.Equal("USB cable", usb!["connectionSummary"]!.GetValue<string>());
        Assert.False(usb["supported"]!.GetValue<bool>());
        Assert.Equal("Bluetooth", bt!["connectionSummary"]!.GetValue<string>());
        Assert.False(bt["supported"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_unknown_mode_is_refused()
    {
        using var h = new Harness(register: false);

        var (status, body) = await h.Client.PostJson("/api/terminals", new { name = "T", serialNumber = "S", ecrMode = 9 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("Choose an ECR mode", body!["errors"]!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_duplicate_serial_is_reported_against_the_serial_field()
    {
        using var h = new Harness(register: false);
        await h.Client.PostJson("/api/terminals", Wifi());

        var (status, body) = await h.Client.PostJson("/api/terminals", Wifi(name: "Other"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("A terminal with this serial number already exists", body!["errors"]!["serial"]!.GetValue<string>());
    }

    [Fact]
    public async Task Editing_keeps_the_entry_or_replaces_it_when_the_serial_changes()
    {
        using var h = new Harness(register: false);
        await h.Client.PostJson("/api/terminals", Wifi());
        await h.Client.PostJson("/api/terminals", Wifi(name: "Second", serial: "S2"));

        var (same, _) = await h.Client.PutJson("/api/terminals/S1", Wifi(name: "Renamed"));
        var (renumbered, _) = await h.Client.PutJson("/api/terminals/S1", Wifi(name: "Renamed", serial: "S3"));
        var (clash, clashBody) = await h.Client.PutJson("/api/terminals/S3", Wifi(serial: "S2"));
        var (missing, _) = await h.Client.PutJson("/api/terminals/NOPE", Wifi());

        Assert.Equal(HttpStatusCode.OK, same);
        Assert.Equal(HttpStatusCode.OK, renumbered);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash);
        Assert.Equal("A terminal with this serial number already exists", clashBody!["errors"]!["serial"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, missing);

        var (_, list) = await h.Client.GetJson("/api/terminals");
        var serials = list!["terminals"]!.AsArray().Select(t => t!["serialNumber"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "S2", "S3" }, serials.OrderBy(s => s).ToArray()); // S1 is gone; the key was the serial
    }

    [Fact]
    public async Task Deleting_removes_the_terminal()
    {
        using var h = new Harness(register: false);
        await h.Client.PostJson("/api/terminals", Wifi());

        var (deleted, _) = await h.Client.DeleteJson("/api/terminals/S1");
        var (again, _) = await h.Client.DeleteJson("/api/terminals/S1");
        var (_, list) = await h.Client.GetJson("/api/terminals");

        Assert.Equal(HttpStatusCode.NoContent, deleted);
        Assert.Equal(HttpStatusCode.NotFound, again);
        Assert.Empty(list!["terminals"]!.AsArray());
    }

    [Fact]
    public async Task Terminals_survive_a_restart_of_the_host()
    {
        using var dir = new TempFolder();
        using (var first = new Harness(register: false, dataDir: dir.Path))
        {
            await first.Client.PostJson("/api/terminals", Wifi());
        }

        using var second = new Harness(register: false, dataDir: dir.Path);
        var (_, list) = await second.Client.GetJson("/api/terminals");

        Assert.Equal("S1", Assert.Single(list!["terminals"]!.AsArray())!["serialNumber"]!.GetValue<string>());
    }
}

public class SettingsTests
{
    private const string Key = FakeTerminal.Key;

    [Fact]
    public async Task Settings_start_unconfigured_on_sit()
    {
        using var h = new Harness(register: false);

        var (_, body) = await h.Client.GetJson("/api/settings");

        Assert.Equal("SIT", body!["environment"]!.GetValue<string>());
        Assert.False(body["wifi"]!["configured"]!.GetValue<bool>());
        Assert.False(body["webService"]!["configured"]!.GetValue<bool>());
        Assert.Equal("Wi-Fi/LAN secure hash key", body["wifi"]!["label"]!.GetValue<string>());
        Assert.Equal("Web Service secure hash key", body["webService"]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_saved_key_is_reported_as_configured_and_never_echoed_back()
    {
        using var h = new Harness(register: false);

        var (putStatus, put) = await h.Client.PutJson("/api/settings/keys/wifi", new { value = Key });
        var (_, get) = await h.Client.GetJson("/api/settings");
        var (_, terminals) = await h.Client.GetJson("/api/terminals");

        Assert.Equal(HttpStatusCode.OK, putStatus);
        Assert.True(get!["wifi"]!["configured"]!.GetValue<bool>());
        Assert.False(get["webService"]!["configured"]!.GetValue<bool>());
        foreach (var text in new[] { put!.ToJsonString(), get.ToJsonString(), terminals!.ToJsonString() })
        {
            Assert.DoesNotContain(Key, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("ABC")]                                // too short
    [InlineData("0123456789ABCDEF0123456789ABCDE")]    // odd length
    [InlineData("ZZ23456789ABCDEF0123456789ABCDEF")]   // not hex
    public async Task An_invalid_key_is_refused_and_nothing_is_saved(string key)
    {
        using var h = new Harness(register: false);

        var (status, body) = await h.Client.PutJson("/api/settings/keys/wifi", new { value = key });
        var (_, get) = await h.Client.GetJson("/api/settings");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("Must be an even-length hex string of at least 16 characters", body!["errors"]!["value"]!.GetValue<string>());
        Assert.DoesNotContain(key, body.ToJsonString());
        Assert.False(get!["wifi"]!["configured"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_blank_key_is_refused_and_clear_removes_one()
    {
        using var h = new Harness(register: false);
        await h.SaveKeyAsync();

        var (blank, _) = await h.Client.PutJson("/api/settings/keys/wifi", new { value = "  " });
        var (_, stillThere) = await h.Client.GetJson("/api/settings");
        var (cleared, body) = await h.Client.DeleteJson("/api/settings/keys/wifi");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank);
        Assert.True(stillThere!["wifi"]!["configured"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, cleared);
        Assert.False(body!["wifi"]!["configured"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_two_keys_are_independent_and_an_unknown_kind_is_a_404()
    {
        using var h = new Harness(register: false);

        await h.Client.PutJson("/api/settings/keys/webService", new { value = Key });
        var (_, get) = await h.Client.GetJson("/api/settings");
        var (unknown, _) = await h.Client.PutJson("/api/settings/keys/bluetooth", new { value = Key });

        Assert.False(get!["wifi"]!["configured"]!.GetValue<bool>());
        Assert.True(get["webService"]!["configured"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, unknown);
    }

    [Theory]
    [InlineData("UAT", "UAT")]
    [InlineData("prod", "PROD")]
    public async Task The_environment_can_be_chosen(string sent, string expected)
    {
        using var h = new Harness(register: false);

        var (status, body) = await h.Client.PutJson("/api/settings/environment", new { environment = sent });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, body!["environment"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("STAGING")]
    [InlineData("1")]
    [InlineData("")]
    public async Task An_unknown_environment_is_refused(string sent)
    {
        using var h = new Harness(register: false);

        var (status, _) = await h.Client.PutJson("/api/settings/environment", new { environment = sent });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
    }

    [Fact]
    public async Task Secrets_are_encrypted_at_rest_and_survive_a_restart_through_the_key_ring()
    {
        using var folder = new TempFolder();
        var dir = folder.Path;
        using (var first = new Harness(register: false, dataDir: dir))
        {
            await first.SaveKeyAsync();
            await first.Client.PutJson("/api/settings/environment", new { environment = "UAT" });

            var file = File.ReadAllBytes(Path.Combine(dir, "settings.dat"));
            var text = Encoding.Latin1.GetString(file);
            Assert.DoesNotContain(Key, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wifiSecureHashKey", text); // not even the JSON shape is readable
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(dir, "keys"), "key-*.xml")); // the ring is persisted beside it
        }

        // A new host over the same folder: decrypted by the persisted ring, and the key
        // still signs correctly (a sale is accepted by the terminal that holds it).
        using var second = new Harness(register: false, dataDir: dir);
        var (_, settings) = await second.Client.GetJson("/api/settings");
        Assert.True(settings!["wifi"]!["configured"]!.GetValue<bool>());
        Assert.Equal("UAT", settings["environment"]!.GetValue<string>());

        await second.RegisterAsync(Harness.Serial, second.Terminal.Port);
        var end = await second.RunToEndAsync();
        Assert.Equal("completed", end["state"]!.GetValue<string>());
        Assert.Equal("Approved", end["dialog"]!["headline"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_lost_key_ring_is_reported_as_a_notice_not_a_crash()
    {
        using var folder = new TempFolder();
        var dir = folder.Path;
        using (var first = new Harness(register: false, dataDir: dir))
        {
            await first.SaveKeyAsync();
        }
        Directory.Delete(Path.Combine(dir, "keys"), recursive: true);

        using var second = new Harness(register: false, dataDir: dir);
        var (_, settings) = await second.Client.GetJson("/api/settings");

        Assert.False(settings!["wifi"]!["configured"]!.GetValue<bool>());
        Assert.Contains("could not be read", settings["notice"]!.GetValue<string>());
    }

    [Fact]
    public async Task Data_folder_is_created_and_private_on_unix()
    {
        using var h = new Harness(register: false);

        Assert.True(Directory.Exists(h.DataDir));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(h.DataDir));
        }
        await Task.CompletedTask;
    }

    [Fact]
    public void An_existing_data_folder_keeps_its_permissions_but_the_key_ring_is_still_made_private()
    {
        if (OperatingSystem.IsWindows()) return;

        // DataDir may point at a folder that is shared on purpose; its mode is not ours to change.
        var root = Path.Combine(Path.GetTempPath(), "ecr-web-existing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(root, shared);
        // An earlier, looser key ring folder is exactly what must be tightened.
        Directory.CreateDirectory(Path.Combine(root, "keys"));
        File.SetUnixFileMode(Path.Combine(root, "keys"), shared);
        try
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = root })
                .Build();

            new AmwalECR.WebExample.Hosting.DataPaths(config).Ensure();

            Assert.Equal(shared, File.GetUnixFileMode(root));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Combine(root, "keys")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecr-web-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
