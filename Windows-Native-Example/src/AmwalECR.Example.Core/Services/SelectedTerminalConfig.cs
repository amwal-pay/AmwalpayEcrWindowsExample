using AmwalECR.Example.Core.Data;
using AmwalPay.Ecr;

namespace AmwalECR.Example.Core.Services;

/// <summary>
/// App wrapper around <see cref="EcrSessionPlan"/> for the selected registered
/// terminal.
/// <para>
/// Transport validation and SDK config assembly live in
/// <see cref="EcrSessions.Plan"/>; the app only maps persisted terminal records to
/// <see cref="EcrLink"/> and supplies credentials from settings.
/// </para>
/// </summary>
public sealed record SelectedTerminalConfig(Terminal Terminal, EcrSessionPlan Plan)
{
    public EcrMode Mode => Terminal.Mode;

    public string ModeLabel => Mode.Label();

    public EcrConfig EcrConfig => Plan.Config;

    public IReadOnlyList<string> Issues => Plan.Issues;

    public bool HasIssues => Plan.Issues.Count != 0;

    public bool UsesWebService => Plan.UsesWebService;

    public bool UsesLan => Plan.UsesLan;

    public bool IsSupported => Mode.IsSupportedInExample();

    public bool IsReady => IsSupported && Plan.IsReady;

    public string HashKeyLabel => Plan.SigningKeyLabel;

    public bool HashKeyConfigured => Plan.SigningKeyConfigured;

    public string ConnectionSummary => Plan.ConnectionSummary;

    /// <summary>"TCP Wi-Fi" / "HTTPS Web Service…", as the config card shows it.</summary>
    public string TransportLabel =>
        UsesWebService ? "HTTPS Web Service (JSON HMAC)"
        : UsesLan ? "TCP Wi-Fi (sorted key=value HMAC)"
        : Mode.Label();

    public string HashKeyStatus =>
        HashKeyConfigured
            ? $"configured ({EcrConfig.SecureHashKey.Length} hex chars)"
            : "not configured — set in Terminals settings";

    public static SelectedTerminalConfig Resolve(Terminal terminal, EcrEnvironment environment, string secureHashKey)
    {
        var issues = new List<string>();
        if (!terminal.Mode.IsSupportedInExample())
        {
            issues.Add($"{terminal.Mode.Label()} is not supported in this sample");
        }

        var link = ToLink(terminal);
        var trimmedKey = secureHashKey.Trim();
        var requested = new EcrConfig
        {
            SecureHashKey = EcrConfig.IsValidSecureHashKey(trimmedKey) ? trimmedKey : "",
            MerchantId = terminal.MerchantId,
            TerminalId = terminal.TerminalId,
            Environment = environment,
            Port = PortOrDefault(terminal.Port),
        };

        var plan = EcrSessions.Plan(link, requested, trimmedKey);
        if (issues.Count != 0)
        {
            plan = plan with { Issues = plan.Issues.Concat(issues).ToList() };
        }

        return new SelectedTerminalConfig(terminal, plan);
    }

    private static int PortOrDefault(int port) => port is >= 1 and <= 65535 ? port : EcrConfig.DefaultPort;

    private static EcrLink ToLink(Terminal terminal) => terminal.Mode switch
    {
        EcrMode.WebService => new EcrLink.WebService(terminal.MerchantId, terminal.TerminalId),

        // No address to carry: the cable is found by looking at what is plugged
        // in, so there is nothing here for an operator to type or to get wrong.
        EcrMode.UsbCable => new EcrLink.UsbCable(),

        EcrMode.Wifi => new EcrLink.Lan(terminal.IpAddress, PortOrDefault(terminal.Port)),

        _ => new EcrLink.Lan(terminal.IpAddress, EcrConfig.DefaultPort),
    };
}
