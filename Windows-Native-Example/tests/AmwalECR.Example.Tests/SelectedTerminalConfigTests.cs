using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Services;
using AmwalPay.Ecr;
using Xunit;

namespace AmwalECR.Example.Tests;

public class SelectedTerminalConfigTests
{
    private const string Key = "0123456789ABCDEF0123456789ABCDEF";

    private static Terminal Wifi(string ip = "192.168.1.50", int port = 9100) => new()
    {
        SerialNumber = "TW1",
        Name = "Till 1",
        EcrModeValue = EcrMode.Wifi.Value(),
        IpAddress = ip,
        Port = port,
    };

    private static Terminal Web(string merchant = "1234", string terminal = "5678") => new()
    {
        SerialNumber = "TW2",
        Name = "Till 2",
        EcrModeValue = EcrMode.WebService.Value(),
        MerchantId = merchant,
        TerminalId = terminal,
    };

    [Fact]
    public void A_wifi_terminal_with_address_and_key_is_ready()
    {
        var config = SelectedTerminalConfig.Resolve(Wifi(), EcrEnvironment.SIT, Key);

        Assert.True(config.IsReady);
        Assert.Empty(config.Issues);
        Assert.True(config.UsesLan);
        Assert.False(config.UsesWebService);
        Assert.Equal("192.168.1.50:9100", config.ConnectionSummary);
        Assert.Equal("Wi-Fi/CABLE secure hash key", config.HashKeyLabel);
        Assert.True(config.HashKeyConfigured);
        Assert.Equal("configured (32 hex chars)", config.HashKeyStatus);
        Assert.Equal("TCP Wi-Fi (sorted key=value HMAC)", config.TransportLabel);
    }

    [Fact]
    public void A_missing_key_is_reported_before_paying_and_blocks_the_terminal()
    {
        var config = SelectedTerminalConfig.Resolve(Wifi(), EcrEnvironment.SIT, "");

        Assert.False(config.IsReady);
        Assert.Contains("Wi-Fi/CABLE secure hash key is not configured", config.Issues);
        Assert.False(config.HashKeyConfigured);
        Assert.Contains("not configured", config.HashKeyStatus);
    }

    [Theory]
    [InlineData("zzzz")]
    [InlineData("0123")]
    [InlineData("0123456789ABCDEF0")]
    public void A_malformed_key_is_reported_and_not_passed_on(string key)
    {
        var config = SelectedTerminalConfig.Resolve(Wifi(), EcrEnvironment.SIT, key);

        Assert.False(config.IsReady);
        Assert.False(config.HashKeyConfigured);
        Assert.Contains(config.Issues, i => i.Contains("even-length hex string"));
    }

    [Fact]
    public void The_key_is_trimmed()
    {
        var config = SelectedTerminalConfig.Resolve(Wifi(), EcrEnvironment.SIT, "  " + Key + "\n");

        Assert.True(config.IsReady);
        Assert.Equal(Key, config.EcrConfig.SecureHashKey);
    }

    [Fact]
    public void A_wifi_terminal_needs_an_address()
    {
        var config = SelectedTerminalConfig.Resolve(Wifi(ip: ""), EcrEnvironment.SIT, Key);

        Assert.False(config.IsReady);
        Assert.Contains("IP address is required for Wi-Fi ECR", config.Issues);
    }

    [Fact]
    public void An_unset_port_falls_back_to_9100()
    {
        var config = SelectedTerminalConfig.Resolve(Wifi(port: 0), EcrEnvironment.SIT, Key);

        Assert.True(config.IsReady);
        Assert.Equal(9100, config.EcrConfig.Port);
        Assert.Equal("192.168.1.50:9100", config.ConnectionSummary);
    }

    [Fact]
    public void A_web_service_terminal_uses_its_own_key_label_and_transport()
    {
        var config = SelectedTerminalConfig.Resolve(Web(), EcrEnvironment.UAT, Key);

        Assert.True(config.IsReady);
        Assert.True(config.UsesWebService);
        Assert.Equal("Web Service secure hash key", config.HashKeyLabel);
        Assert.Equal("HTTPS Web Service (JSON HMAC)", config.TransportLabel);
        Assert.Equal("UAT · merchant 1234 · terminal 5678", config.ConnectionSummary);
        Assert.Equal(EcrEnvironment.UAT, config.EcrConfig.Environment);
    }

    [Fact]
    public void A_web_service_terminal_needs_numeric_backend_ids()
    {
        var missing = SelectedTerminalConfig.Resolve(Web(merchant: "", terminal: ""), EcrEnvironment.SIT, Key);
        Assert.False(missing.IsReady);
        Assert.Contains("Merchant ID is required for Web Service", missing.Issues);
        Assert.Contains("Terminal ID is required for Web Service", missing.Issues);

        var letters = SelectedTerminalConfig.Resolve(Web(merchant: "abc"), EcrEnvironment.SIT, Key);
        Assert.Contains("Merchant ID must be numeric", letters.Issues);
    }

    [Fact]
    public void Usb_cable_is_ready_when_the_wifi_key_is_set()
    {
        var usb = new Terminal { SerialNumber = "TW3", Name = "Cable", EcrModeValue = EcrMode.UsbCable.Value(), ComPort = "COM8" };

        var config = SelectedTerminalConfig.Resolve(usb, EcrEnvironment.SIT, Key);

        Assert.True(config.IsSupported);
        Assert.True(config.IsReady);
        Assert.True(config.Plan.UsesUsbCable);
        Assert.Equal("COM8", config.ConnectionSummary);

        var unplugged = SelectedTerminalConfig.Resolve(
            new Terminal { SerialNumber = "TW3", Name = "Cable", EcrModeValue = EcrMode.UsbCable.Value() },
            EcrEnvironment.SIT, Key);
        Assert.False(unplugged.IsReady);
        Assert.Contains("Choose the USB device this cable uses", unplugged.Issues);
    }

    [Fact]
    public void Bluetooth_is_registered_but_not_supported_in_this_sample()
    {
        var bluetooth = new Terminal { SerialNumber = "TW4", Name = "BT", EcrModeValue = EcrMode.Bluetooth.Value() };

        var config = SelectedTerminalConfig.Resolve(bluetooth, EcrEnvironment.SIT, Key);

        Assert.False(config.IsReady);
        Assert.Contains("Bluetooth is not supported in this sample", config.Issues);
    }

    [Fact]
    public void The_lan_key_serves_wifi_and_the_other_key_serves_web_service()
    {
        var wifiKey = "AAAAAAAAAAAAAAAA";
        var webKey = "BBBBBBBBBBBBBBBB";
        var wifi = SelectedTerminalConfig.Resolve(Wifi(), EcrEnvironment.SIT, wifiKey);
        var web = SelectedTerminalConfig.Resolve(Web(), EcrEnvironment.SIT, webKey);

        Assert.Equal(wifiKey, wifi.EcrConfig.SecureHashKey);
        Assert.Equal(webKey, web.EcrConfig.SecureHashKey);
    }
}
