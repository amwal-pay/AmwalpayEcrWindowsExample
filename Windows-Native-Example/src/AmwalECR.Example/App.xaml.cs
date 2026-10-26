using System.Diagnostics;
using System.IO;
using System.Windows;
using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Logging;
using AmwalECR.Example.Core.Services;
using AmwalECR.Example.Services;
using AmwalECR.Example.ViewModels;
using AmwalPay.Ecr;

namespace AmwalECR.Example;

/// <summary>
/// The composition root: builds the stores, the SDK loggers and the view-models by
/// hand — there are few enough that a container would only hide the wiring.
/// </summary>
public partial class App : Application
{
    /// <summary>Where terminals.json and settings.dat live: <c>%APPDATA%\AmwalECR-Example</c>.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmwalECR-Example");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Debug.WriteLine(args.Exception);
            MessageBox.Show(
                $"Something went wrong: {args.Exception.Message}\n\n" +
                "If a transaction was in progress, its outcome may be unknown — check the terminal before taking payment again.",
                "Amwal ECR Example",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        // The Windows equivalent of Logcat: every line the SDK logs lands in the
        // Debug output panel, the debugger, and debug.log next to the saved terminals.
        Directory.CreateDirectory(DataDirectory);
        var logFile = Path.Combine(DataDirectory, "debug.log");
        var log = new LogBuffer(mirror: line => WriteDebugLog(logFile, line));
        log.Append("App", $"debug log {logFile}");
        var lanLogger = EcrLogger.From(message => log.Append("Ecr", message));
        var webServiceLogger = EcrLogger.From(message => log.Append("EcrWebService", message));

        var repository = new TerminalRepository(Path.Combine(DataDirectory, "terminals.json"));
        var settings = new EcrExampleSettings(Path.Combine(DataDirectory, "settings.dat"), new DpapiSecretProtector());
        var engine = new TransactionEngine(repository, settings, lanLogger, webServiceLogger);

        var clipboard = new ClipboardService();
        var dialogs = new DialogService();

        var transaction = new TransactionViewModel(repository, settings, engine);
        var terminals = new TerminalsViewModel(repository, new SettingsViewModel(settings), dialogs);
        var main = new MainViewModel(transaction, terminals, new LogViewModel(log, clipboard));

        // Kept alive by its subscription to the transaction view-model.
        _ = new ResultDialogPresenter(transaction, dialogs, clipboard);

        var window = new MainWindow { DataContext = main };
        MainWindow = window;
        window.Show();

        if (transaction.SelectedConfig != null) engine.LogSelectedConfig(transaction.SelectedConfig);
    }

    private static readonly object LogFileGate = new();

    /// <summary>
    /// Keeps the same lines a debugger would show, in a file, so a double-clicked
    /// exe still has a trail. A full disk must not stop a sale.
    /// </summary>
    private static void WriteDebugLog(string path, string line)
    {
        Debug.WriteLine(line);
        try
        {
            lock (LogFileGate)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1_000_000) File.WriteAllText(path, "");
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // Logging never decides whether a payment went through.
        }
    }
}
