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

        // The Windows equivalent of Logcat: every line the SDK logs lands here, and
        // in the debugger's output window.
        var log = new LogBuffer(mirror: line => Debug.WriteLine(line));
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
}
