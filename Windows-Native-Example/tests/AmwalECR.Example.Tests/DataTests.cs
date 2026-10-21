using System.Security.Cryptography;
using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Logging;
using AmwalECR.Example.Core.Terminals;
using AmwalPay.Ecr;
using Xunit;

namespace AmwalECR.Example.Tests;

/// <summary>A reversible stand-in for DPAPI that still refuses garbage.</summary>
internal sealed class FakeProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).Prepend((byte)0xA5).ToArray();

    public byte[] Unprotect(byte[] protectedData)
    {
        if (protectedData.Length == 0 || protectedData[0] != 0xA5) throw new CryptographicException("not protected");
        return protectedData.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecr-example-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public class TerminalTests
{
    [Fact]
    public void The_dropdown_label_is_name_and_serial() =>
        Assert.Equal("Till 1 (TW1)", new Terminal { Name = "Till 1", SerialNumber = "TW1" }.ToString());

    [Fact]
    public void An_unknown_mode_value_reads_as_wifi() =>
        Assert.Equal(EcrMode.Wifi, new Terminal { EcrModeValue = 99 }.Mode);

    [Theory]
    [InlineData(2, "192.168.1.50", 9100, "192.168.1.50:9100")]
    [InlineData(2, "192.168.1.50", 0, "192.168.1.50")]
    [InlineData(2, "", 0, "Wi‑Fi")]
    [InlineData(1, "192.168.1.50", 9100, "USB cable")]
    [InlineData(3, "192.168.1.50", 9100, "Bluetooth")]
    public void The_connection_summary_follows_the_mode(int mode, string ip, int port, string expected) =>
        Assert.Equal(expected, new Terminal { EcrModeValue = mode, IpAddress = ip, Port = port }.ConnectionSummary());

    [Theory]
    [InlineData("1234", "5678", "Merchant 1234 · Terminal 5678")]
    [InlineData("1234", "", "Merchant 1234")]
    [InlineData("", "5678", "Terminal 5678")]
    [InlineData("", "", "Web Service")]
    public void The_web_service_summary_lists_the_backend_ids(string merchant, string terminal, string expected) =>
        Assert.Equal(expected, new Terminal { EcrModeValue = 4, MerchantId = merchant, TerminalId = terminal }.ConnectionSummary());

    [Fact]
    public void The_modes_keep_the_tms_values()
    {
        Assert.Equal(1, EcrMode.UsbCable.Value());
        Assert.Equal(2, EcrMode.Wifi.Value());
        Assert.Equal(3, EcrMode.Bluetooth.Value());
        Assert.Equal(4, EcrMode.WebService.Value());
        Assert.True(EcrMode.Wifi.IsSupportedInExample());
        Assert.True(EcrMode.WebService.IsSupportedInExample());
        Assert.True(EcrMode.UsbCable.IsSupportedInExample());
        Assert.False(EcrMode.Bluetooth.IsSupportedInExample());
    }
}

public class TerminalValidatorTests
{
    private static TerminalErrors Check(
        string name = "Till", string serial = "TW1", EcrMode mode = EcrMode.Wifi,
        string ip = "192.168.1.50", string port = "9100", string terminalId = "", string merchantId = "") =>
        TerminalValidator.Validate(name, serial, mode, ip, port, terminalId, merchantId);

    [Fact]
    public void A_complete_wifi_terminal_is_valid() => Assert.False(Check().Any);

    [Fact]
    public void Name_and_serial_are_required()
    {
        var errors = Check(name: " ", serial: "");

        Assert.Equal("Enter a terminal name", errors.Name);
        Assert.Equal("Enter the terminal serial number", errors.Serial);
    }

    [Theory]
    [InlineData("", "Enter the terminal IP address")]
    [InlineData("192.168.1", "Enter a valid IPv4 address, for example 192.168.1.50")]
    [InlineData("256.1.1.1", "Enter a valid IPv4 address, for example 192.168.1.50")]
    [InlineData("a.b.c.d", "Enter a valid IPv4 address, for example 192.168.1.50")]
    [InlineData("127.0.0.1", null)]
    [InlineData(" 10.0.0.255 ", null)]
    public void Wifi_needs_a_valid_ipv4_address(string ip, string? error) => Assert.Equal(error, Check(ip: ip).Ip);

    [Theory]
    [InlineData("", null)]
    [InlineData("9100", null)]
    [InlineData("1", null)]
    [InlineData("65535", null)]
    [InlineData("0", "Port must be between 1 and 65535")]
    [InlineData("65536", "Port must be between 1 and 65535")]
    public void The_port_is_optional_but_must_be_in_range(string port, string? error) => Assert.Equal(error, Check(port: port).Port);

    [Fact]
    public void Web_service_needs_numeric_backend_ids()
    {
        var missing = Check(mode: EcrMode.WebService, ip: "", port: "");
        Assert.Equal("Enter the backend terminal ID", missing.TerminalId);
        Assert.Equal("Enter the merchant ID", missing.MerchantId);
        Assert.Null(missing.Ip);
        Assert.Null(missing.Port);

        var letters = Check(mode: EcrMode.WebService, terminalId: "12a", merchantId: "9 9");
        Assert.NotNull(letters.TerminalId);
        Assert.NotNull(letters.MerchantId);

        Assert.False(Check(mode: EcrMode.WebService, terminalId: "5678", merchantId: "1234").Any);
    }

    [Fact]
    public void Bluetooth_needs_nothing_beyond_name_and_serial() =>
        Assert.False(Check(mode: EcrMode.Bluetooth, ip: "", port: "").Any);

    [Fact]
    public void Usb_cable_needs_nothing_beyond_name_and_serial() =>
        Assert.False(Check(mode: EcrMode.UsbCable, ip: "", port: "").Any);

    [Fact]
    public void A_blank_port_is_stored_as_9100_and_fields_are_trimmed()
    {
        var terminal = TerminalValidator.ToTerminal(" Till ", " TW1 ", EcrMode.Wifi, " 10.0.0.1 ", "", "", "");

        Assert.Equal("Till", terminal.Name);
        Assert.Equal("TW1", terminal.SerialNumber);
        Assert.Equal("10.0.0.1", terminal.IpAddress);
        Assert.Equal(9100, terminal.Port);
    }
}

public class TerminalRepositoryTests
{
    private static Terminal T(string serial, string name = "Till") => new() { SerialNumber = serial, Name = name };

    [Fact]
    public void Terminals_are_listed_by_name_ignoring_case()
    {
        using var dir = new TempDir();
        var repo = new TerminalRepository(dir.File("terminals.json"));
        repo.Save(T("2", "banana"), null);
        repo.Save(T("1", "Cherry"), null);
        repo.Save(T("3", "apple"), null);

        Assert.Equal(new[] { "apple", "banana", "Cherry" }, repo.GetAll().Select(t => t.Name));
    }

    [Fact]
    public void A_duplicate_serial_is_refused_and_leaves_the_list_alone()
    {
        using var dir = new TempDir();
        var repo = new TerminalRepository(dir.File("terminals.json"));
        repo.Save(T("1", "First"), null);

        var result = repo.Save(T("1", "Second"), null);

        var failed = Assert.IsType<SaveResult.Failed>(result);
        Assert.Equal("A terminal with this serial number already exists", failed.Message);
        Assert.Equal("First", Assert.Single(repo.GetAll()).Name);
    }

    [Fact]
    public void Editing_in_place_keeps_the_serial_and_updates_the_rest()
    {
        using var dir = new TempDir();
        var repo = new TerminalRepository(dir.File("terminals.json"));
        repo.Save(T("1", "Old"), null);

        Assert.IsType<SaveResult.Saved>(repo.Save(T("1", "New"), "1"));

        Assert.Equal("New", Assert.Single(repo.GetAll()).Name);
    }

    [Fact]
    public void Changing_the_serial_replaces_the_entry_and_may_not_collide()
    {
        using var dir = new TempDir();
        var repo = new TerminalRepository(dir.File("terminals.json"));
        repo.Save(T("1", "A"), null);
        repo.Save(T("2", "B"), null);

        Assert.IsType<SaveResult.Failed>(repo.Save(T("2", "A"), "1"));
        Assert.IsType<SaveResult.Saved>(repo.Save(T("3", "A"), "1"));

        Assert.Equal(new[] { "2", "3" }, repo.GetAll().Select(t => t.SerialNumber).OrderBy(s => s));
        Assert.Null(repo.FindBySerial("1"));
    }

    [Fact]
    public void Terminals_survive_a_restart_and_deletes_persist()
    {
        using var dir = new TempDir();
        var path = dir.File("terminals.json");
        var repo = new TerminalRepository(path);
        repo.Save(new Terminal { SerialNumber = "1", Name = "Wifi", EcrModeValue = 2, IpAddress = "10.0.0.1", Port = 9100 }, null);
        repo.Save(new Terminal { SerialNumber = "2", Name = "Web", EcrModeValue = 4, MerchantId = "12", TerminalId = "34" }, null);
        repo.Delete("2");

        var reloaded = new TerminalRepository(path);

        var only = Assert.Single(reloaded.GetAll());
        Assert.Equal("10.0.0.1:9100", only.ConnectionSummary());
        Assert.Null(reloaded.LoadWarning);
    }

    [Fact]
    public void The_unreadable_file_warning_goes_away_once_a_key_is_saved_over_it()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.dat");
        File.WriteAllText(path, "not encrypted at all");
        var settings = new EcrExampleSettings(path, new FakeProtector());
        Assert.NotNull(settings.LoadWarning);

        settings.WifiSecureHashKey = "0123456789ABCDEF";

        // The bad file has been replaced by a good one: telling the operator to enter
        // the keys again, after they just did, is wrong.
        Assert.Null(settings.LoadWarning);
        Assert.Equal("0123456789ABCDEF", new EcrExampleSettings(path, new FakeProtector()).WifiSecureHashKey);
    }

    [Fact]
    public void Changes_are_announced()
    {
        using var dir = new TempDir();
        var repo = new TerminalRepository(dir.File("terminals.json"));
        var count = 0;
        repo.Changed += () => count++;

        repo.Save(T("1"), null);
        repo.Delete("1");

        Assert.Equal(2, count);
    }

    [Fact]
    public void An_unreadable_file_is_kept_aside_not_overwritten_silently()
    {
        using var dir = new TempDir();
        var path = dir.File("terminals.json");
        File.WriteAllText(path, "{ this is not json");

        var repo = new TerminalRepository(path);

        Assert.Empty(repo.GetAll());
        Assert.NotNull(repo.LoadWarning);
        Assert.Equal("{ this is not json", File.ReadAllText(path + ".unreadable"));
    }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_are_sit_and_no_keys()
    {
        using var dir = new TempDir();
        var settings = new EcrExampleSettings(dir.File("settings.dat"), new FakeProtector());

        Assert.Equal(EcrEnvironment.SIT, settings.Environment);
        Assert.Equal("", settings.WifiSecureHashKey);
        Assert.Equal("", settings.WebServiceSecureHashKey);
    }

    [Fact]
    public void Each_mode_gets_its_own_key()
    {
        using var dir = new TempDir();
        var settings = new EcrExampleSettings(dir.File("settings.dat"), new FakeProtector())
        {
            WifiSecureHashKey = "AAAAAAAAAAAAAAAA",
            WebServiceSecureHashKey = "BBBBBBBBBBBBBBBB",
        };

        Assert.Equal("AAAAAAAAAAAAAAAA", settings.SecureHashKeyFor(EcrMode.Wifi));
        Assert.Equal("AAAAAAAAAAAAAAAA", settings.SecureHashKeyFor(EcrMode.UsbCable));
        Assert.Equal("AAAAAAAAAAAAAAAA", settings.SecureHashKeyFor(EcrMode.Bluetooth));
        Assert.Equal("BBBBBBBBBBBBBBBB", settings.SecureHashKeyFor(EcrMode.WebService));
    }

    [Fact]
    public void Keys_are_trimmed_and_persist_encrypted()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.dat");
        var settings = new EcrExampleSettings(path, new FakeProtector());

        settings.WifiSecureHashKey = "  0123456789ABCDEF \n";
        settings.Environment = EcrEnvironment.PROD;

        Assert.DoesNotContain("0123456789ABCDEF", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        var reloaded = new EcrExampleSettings(path, new FakeProtector());
        Assert.Equal("0123456789ABCDEF", reloaded.WifiSecureHashKey);
        Assert.Equal(EcrEnvironment.PROD, reloaded.Environment);
    }

    [Fact]
    public void A_file_that_cannot_be_decrypted_starts_empty_and_says_so()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.dat");
        File.WriteAllText(path, "not encrypted at all");

        var settings = new EcrExampleSettings(path, new FakeProtector());

        Assert.NotNull(settings.LoadWarning);
        Assert.Equal("", settings.WifiSecureHashKey);
    }

    [Fact]
    public void Changes_are_announced()
    {
        using var dir = new TempDir();
        var settings = new EcrExampleSettings(dir.File("settings.dat"), new FakeProtector());
        var count = 0;
        settings.Changed += () => count++;

        settings.WebServiceSecureHashKey = "BBBBBBBBBBBBBBBB";

        Assert.Equal(1, count);
    }
}

public class LogBufferTests
{
    [Fact]
    public void Only_the_last_lines_are_kept()
    {
        var log = new LogBuffer(capacity: 3);
        for (var i = 1; i <= 5; i++) log.Append("Ecr", $"line {i}");

        Assert.Equal(3, log.Count);
        var text = log.Snapshot();
        Assert.DoesNotContain("line 2", text);
        Assert.Contains("line 3", text);
        Assert.Contains("line 5", text);
    }

    [Fact]
    public void The_default_capacity_is_500() => Assert.Equal(500, new LogBuffer().Capacity);

    [Fact]
    public void Lines_are_mirrored_and_a_broken_mirror_does_not_break_logging()
    {
        var mirrored = new List<string>();
        var log = new LogBuffer(mirror: mirrored.Add);
        log.Append("Ecr", "hello");
        Assert.Contains("hello", Assert.Single(mirrored));

        var fragile = new LogBuffer(mirror: _ => throw new InvalidOperationException());
        fragile.Append("Ecr", "still logged");
        Assert.Equal(1, fragile.Count);
    }

    [Fact]
    public void Clearing_empties_the_buffer_and_announces_it()
    {
        var log = new LogBuffer();
        var changes = 0;
        log.Changed += () => changes++;
        log.Append("Ecr", "x");

        log.Clear();

        Assert.Equal(0, log.Count);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task It_is_safe_to_write_from_many_threads()
    {
        var log = new LogBuffer(capacity: 100);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++) log.Append("Ecr", $"{t}-{i}");
        })));

        Assert.Equal(100, log.Count);
    }
}
