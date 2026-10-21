using System.Globalization;
using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Transaction;
using AmwalECR.Example.Core.Ui;
using AmwalPay.Ecr;

namespace AmwalECR.WebExample.Api;

/// <summary>
/// <c>GET /api/meta</c>: what the page needs to build its form, taken from the SDK's
/// transaction-type flags and Core's form state — the page carries no copy of those
/// rules.
/// </summary>
public static class MetaEndpoint
{
    private static readonly IReadOnlyList<OperationMeta> Operations = EcrTransactionTypes.MenuOptions.Select(BuildOperation).ToList();

    public static void MapMeta(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/meta", () => Results.Ok(new Meta(
            Operations,
            EcrModes.All.Select(m => new ModeMeta(m.Value(), m.Label(), m.IsIpBased(), m.IsSupportedInExample(), ModeHint(m), ModeNote(m))).ToList(),
            Enum.GetNames<EcrEnvironment>(),
            new AmountMeta(
                AmountLimits.Formatted(AmountLimits.Min),
                AmountLimits.Formatted(AmountLimits.Max),
                AmountLimits.FractionDigits,
                AmountLimits.MaxDigits),
            TransactionFormState.MerchantReferenceMaxLength,
            TransactionFormState.ReceiptNumberMaxLength,
            EcrConfig.DefaultPort,
            DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));

    private static OperationMeta BuildOperation(EcrTransactionType type)
    {
        // Every combination of the two switches, asked of the same TransactionFormState
        // the server validates with, so "shown" and "required" cannot drift apart.
        var layouts = new Dictionary<string, FieldLayout>();
        foreach (var other in new[] { false, true })
        {
            foreach (var byReference in new[] { false, true })
            {
                var form = new TransactionFormState { Type = type, UseOtherTerminal = other, LookUpByReference = byReference };
                layouts[$"{(other ? 1 : 0)}{(byReference ? 1 : 0)}"] = new FieldLayout(
                    Amount: type.RequiresAmount(),
                    MerchantReference: form.ShowMerchantReference,
                    ReferenceSwitch: form.ShowReferenceSwitch,
                    OriginalReference: form.LooksUpByReference,
                    ReceiptNumber: form.ShowReceiptNumber,
                    OriginalDate: form.ShowOriginalDate,
                    OtherTerminalSwitch: form.ShowOtherTerminalSwitch,
                    OriginalTerminal: form.ShowOriginalTerminal);
            }
        }

        return new OperationMeta(
            type.ToString(),
            type.DisplayName(),
            type.RequiresAmount(),
            type.RequiresOriginalStan(),
            type.RequiresOriginalDate(),
            type.AllowsOtherTerminal(),
            type.MovesMoney(),
            // Copy from the WPF form view-model (TransactionFormViewModel.AmountHelp).
            type switch
            {
                _ when type.RequiresAmount() => "",
                EcrTransactionType.Inquiry => "An inquiry only reads; nothing is charged",
                _ => "Uses the original transaction's amount",
            },
            type == EcrTransactionType.Inquiry ? "Look up transaction" : "Start transaction",
            layouts);
    }

    // Copy from the WPF terminal editor (ModeOptionViewModel.Hint and TerminalEditWindow.xaml).
    private static string ModeHint(EcrMode mode) => mode.IsSupportedInExample()
        ? ""
        : "Not available in this sample — Bluetooth has no channel.";

    private static string ModeNote(EcrMode mode) => mode switch
    {
        EcrMode.UsbCable =>
            "There is no address to enter. Plug the terminal into this computer with a USB cable. " +
            "Windows attaches its built-in WinUSB driver on the first sale and asks for approval once. The signing key is the Wi-Fi key.",
        EcrMode.Bluetooth => "Bluetooth ECR cannot be exercised from this sample.",
        _ => "",
    };
}
