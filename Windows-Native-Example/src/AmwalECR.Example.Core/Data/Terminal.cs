using System.Text;
using System.Text.Json.Serialization;
using AmwalPay.Ecr;

namespace AmwalECR.Example.Core.Data;

/// <summary>
/// A registered POS terminal. The serial number is the unique identifier: the
/// transaction screen stores only the serial and looks connection details up
/// from here, so a terminal that moves to a new IP needs no change on the
/// transaction side.
/// </summary>
public sealed record Terminal
{
    public string SerialNumber { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>TMS <c>ecrMode</c>: USB cable, Wi‑Fi, Bluetooth, or Web Service.</summary>
    [JsonPropertyName("ecrMode")]
    public int EcrModeValue { get; init; } = EcrModes.Default.Value();

    public string IpAddress { get; init; } = "";

    public int Port { get; init; }

    /// <summary>Backend terminal ID — required for Web Service.</summary>
    public string TerminalId { get; init; } = "";

    /// <summary>Backend merchant ID — required for Web Service.</summary>
    public string MerchantId { get; init; } = "";

    /// <summary>
    /// USB cable identity. A device serial when Windows reported one, otherwise
    /// the COM name last chosen. Empty for every other mode. The COM number
    /// itself is assigned by Plug and Play and is resolved again at sale time.
    /// </summary>
    public string ComPort { get; init; } = "";

    [JsonIgnore]
    public EcrMode Mode => EcrModes.FromValue(EcrModeValue) ?? EcrModes.Default;

    [JsonIgnore]
    public string ModeLabel => Mode.Label();

    /// <summary>Label used in the terminal dropdown on the transaction screen.</summary>
    [JsonIgnore]
    public string DisplayName => $"{Name} ({SerialNumber})";

    public override string ToString() => DisplayName;

    /// <summary>The same summary as a property, for data binding.</summary>
    [JsonIgnore]
    public string ConnectionText => ConnectionSummary();

    /// <summary>One-line connection summary for the terminals list.</summary>
    public string ConnectionSummary()
    {
        switch (Mode)
        {
            case EcrMode.WebService:
                var summary = new StringBuilder();
                if (MerchantId.Length != 0) summary.Append("Merchant ").Append(MerchantId);
                if (TerminalId.Length != 0)
                {
                    if (summary.Length != 0) summary.Append(" · ");
                    summary.Append("Terminal ").Append(TerminalId);
                }
                return summary.Length == 0 ? "Web Service" : summary.ToString();

            case EcrMode.Bluetooth:
                return "Bluetooth";

            // The COM number is assigned by Windows and can change. Show the
            // port it has now, and the device serial the till is following.
            case EcrMode.UsbCable:
                if (ComPort.Length == 0) return "USB cable";
                var live = SerialPorts.PortFor(ComPort).Port;
                if (live.Length == 0) return ComPort;
                if (string.Equals(live, ComPort, StringComparison.OrdinalIgnoreCase)) return live;
                return live + " · " + ComPort;

            default:
                if (IpAddress.Length != 0 && Port > 0) return $"{IpAddress}:{Port}";
                return IpAddress.Length != 0 ? IpAddress : Mode.Label();
        }
    }
}
