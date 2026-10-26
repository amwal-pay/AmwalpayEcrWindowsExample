using System.Text.RegularExpressions;
using AmwalECR.Example.Core.Data;

namespace AmwalECR.Example.Core.Terminals;

public sealed record TerminalErrors(
    string? Name = null,
    string? Serial = null,
    string? Ip = null,
    string? Port = null,
    string? TerminalId = null,
    string? MerchantId = null,
    string? ComPort = null)
{
    public bool Any =>
        Name != null || Serial != null || Ip != null || Port != null || TerminalId != null || MerchantId != null || ComPort != null;
}

/// <summary>The terminal edit form's rules, the same as the Android sample's.</summary>
public static class TerminalValidator
{
    public const string DefaultPort = "9100";

    /// <summary>Dotted-quad, each octet 0-255.</summary>
    private static readonly Regex Ipv4 = new(
        @"^((25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static TerminalErrors Validate(
        string name,
        string serial,
        EcrMode mode,
        string ip,
        string port,
        string terminalId,
        string merchantId,
        string comPort = "") =>
        new(
            Name: string.IsNullOrWhiteSpace(name) ? "Enter a terminal name" : null,
            // The serial is the terminal's identity, so it is required for every mode.
            Serial: string.IsNullOrWhiteSpace(serial) ? "Enter the terminal serial number" : null,
            Ip: !mode.IsIpBased() ? null
                : string.IsNullOrWhiteSpace(ip) ? "Enter the terminal IP address"
                : !Ipv4.IsMatch(ip.Trim()) ? "Enter a valid IPv4 address, for example 192.168.1.50"
                : null,
            // Optional: blank means the terminals' standard port, 9100. The Android
            // form insists on one; the SDK does not need it.
            Port: !mode.IsIpBased() ? null
                : string.IsNullOrWhiteSpace(port) ? null
                : !(int.TryParse(port, out var p) && p is >= 1 and <= 65535) ? "Port must be between 1 and 65535"
                : null,
            TerminalId: mode != EcrMode.WebService ? null
                : string.IsNullOrWhiteSpace(terminalId) ? "Enter the backend terminal ID"
                : !IsNumeric(terminalId) ? "The terminal ID must be numeric" : null,
            MerchantId: mode != EcrMode.WebService ? null
                : string.IsNullOrWhiteSpace(merchantId) ? "Enter the merchant ID"
                : !IsNumeric(merchantId) ? "The merchant ID must be numeric" : null,
            ComPort: mode != EcrMode.UsbCable ? null
                : string.IsNullOrWhiteSpace(comPort) ? "Choose the USB device this cable uses"
                : !IsPortName(comPort) ? "Choose a USB device. Windows assigns its COM port from the device serial"
                : null);

    // The SDK reads both as 64-bit integers, so that is the rule here.
    private static bool IsNumeric(string value) =>
        long.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);

    private static bool IsPortName(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Length > 260) return false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c == '"' || c == '\'') return false;
        }

        return true;
    }

    public static Terminal ToTerminal(
        string name, string serial, EcrMode mode, string ip, string port, string terminalId, string merchantId, string comPort = "") =>
        new()
        {
            SerialNumber = serial.Trim(),
            Name = name.Trim(),
            EcrModeValue = mode.Value(),
            IpAddress = ip.Trim(),
            Port = mode.IsIpBased() ? (int.TryParse(port, out var p) ? p : int.Parse(DefaultPort)) : 0,
            TerminalId = terminalId.Trim(),
            MerchantId = merchantId.Trim(),
            ComPort = mode == EcrMode.UsbCable ? comPort.Trim() : "",
        };
}
