using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Logging;
using AmwalECR.Example.Core.Services;
using AmwalPay.Ecr;

namespace AmwalECR.WebExample.Hosting;

/// <summary>
/// The composition root of the shared logic, mirroring what the WPF app's
/// <c>App.OnStartup</c> builds by hand: stores, SDK loggers feeding one
/// <see cref="LogBuffer"/>, and the <see cref="TransactionEngine"/>.
/// </summary>
public sealed class EcrApp
{
    public EcrApp(DataPaths paths, ISecretProtector protector, ILoggerFactory loggers)
    {
        paths.Ensure();

        // The SDK logs payloads (masked card numbers) and only ever says a signing
        // key is "configured", never its value. Mirrored at Debug so the console
        // stays quiet by default.
        var mirror = loggers.CreateLogger("AmwalPay.Ecr");
        Log = new LogBuffer(mirror: line => mirror.LogDebug("{Line}", line));

        var lan = EcrLogger.From(message => Log.Append("Ecr", message));
        var webService = EcrLogger.From(message => Log.Append("EcrWebService", message));

        Terminals = new TerminalRepository(paths.Terminals);
        Settings = new EcrExampleSettings(paths.Settings, protector);
        Engine = new TransactionEngine(Terminals, Settings, lan, webService);
    }

    public LogBuffer Log { get; }

    public TerminalRepository Terminals { get; }

    public EcrExampleSettings Settings { get; }

    public TransactionEngine Engine { get; }
}
