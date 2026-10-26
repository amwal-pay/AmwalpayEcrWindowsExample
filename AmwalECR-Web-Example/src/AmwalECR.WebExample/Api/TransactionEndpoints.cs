using System.Globalization;
using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Transaction;
using AmwalECR.WebExample.Hosting;
using AmwalECR.WebExample.Services;
using AmwalPay.Ecr;

namespace AmwalECR.WebExample.Api;

/// <summary>
/// Start, watch, cancel and follow up a transaction. The flow itself is Core's
/// <c>TransactionEngine</c>, run by <see cref="TransactionRegistry"/>; these handlers
/// only translate.
/// </summary>
public static class TransactionEndpoints
{
    public static void MapTransactions(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/transactions");

        group.MapGet("", (TransactionRegistry registry) =>
            Results.Ok(new { transactions = registry.Pending().Select(TransactionView.From) }));

        group.MapPost("", (TransactionFormBody body, EcrApp ecr, TransactionRegistry registry) => Start(body, ecr, registry));

        group.MapPost("/settlement", (SettlementBody body, EcrApp ecr, TransactionRegistry registry) =>
            Settle(body, ecr, registry));

        group.MapPost("/reachability", async (SettlementBody body, EcrApp ecr) =>
        {
            if (body.TerminalSerial is not { Length: > 0 } serial)
            {
                return Fields(("terminal", "Choose a POS terminal"));
            }

            var check = await ecr.Engine.CheckWebServiceReachabilityAsync(serial);
            return Results.Ok(new { check.Reachable, check.Message });
        });

        group.MapGet("/{id}", (string id, TransactionRegistry registry) =>
            registry.Find(id) is { } run ? Results.Ok(TransactionView.From(run)) : NotFound());

        group.MapPost("/{id}/cancel", (string id, TransactionRegistry registry) =>
            registry.Cancel(id) is { } run ? Results.Ok(TransactionView.From(run)) : NotFound());

        group.MapPost("/{id}/inquire", (string id, TransactionRegistry registry) =>
            registry.Inquire(id) switch
            {
                FollowUp.Started started => Accepted(started.Run),
                FollowUp.NotFound => NotFound(),
                FollowUp.Busy busy => Results.Conflict(new ErrorBody("Another transaction is running on this terminal", busy.ExistingId)),
                _ => Results.Conflict(new ErrorBody("This transaction has no unknown outcome to look up")),
            });

        group.MapPost("/{id}/receipt", (string id, TransactionRegistry registry) =>
            registry.RequestReceipt(id) switch
            {
                FollowUp.Started started => Accepted(started.Run),
                FollowUp.NotFound => NotFound(),
                _ => Results.Conflict(new ErrorBody("No receipt can be requested for this transaction")),
            });

        group.MapPost("/{id}/close-receipt", async (string id, TransactionRegistry registry) =>
            await registry.CloseReceiptAsync(id) switch
            {
                CloseReceiptOutcome.Done done => Results.Ok(new
                {
                    message = done.Message,
                    transaction = TransactionView.From(done.Run),
                }),
                CloseReceiptOutcome.NotFound => NotFound(),
                CloseReceiptOutcome.NotApplicable na => Results.Conflict(new ErrorBody(
                    na.Message ?? "Close receipt is not available for this transaction")),
                _ => Results.Conflict(new ErrorBody("Close receipt is not available for this transaction")),
            });

        // "The operator has read the result": frees the page to start the next one.
        group.MapDelete("/{id}", (string id, TransactionRegistry registry) =>
            registry.Acknowledge(id)
                ? Results.NoContent()
                : Results.Conflict(new ErrorBody("The transaction is still running")));
    }

    private static IResult Start(TransactionFormBody body, EcrApp ecr, TransactionRegistry registry)
    {
        var terminal = body.TerminalSerial is { Length: > 0 } serial ? ecr.Terminals.FindBySerial(serial) : null;
        if (terminal == null)
        {
            return Fields(("terminal", body.TerminalSerial is { Length: > 0 } gone
                ? $"Terminal {gone} is no longer registered"
                : "Choose a POS terminal"));
        }

        // The WPF Start button is disabled until the plan is ready, so the operator
        // sees what is missing before paying. The server holds the same line.
        var config = ecr.Engine.ResolveConfig(terminal);
        if (!config.IsReady) return Results.UnprocessableEntity(new FieldErrors(new Dictionary<string, string>(), string.Join("\n", config.Issues)));

        var (form, typeError) = TransactionFormMapper.ToState(body);
        if (form == null) return Fields(("type", typeError!));

        var (validated, request) = form.Validated(terminal.SerialNumber);
        if (request == null) return Results.UnprocessableEntity(new FieldErrors(ToDictionary(validated.Errors)));

        return registry.Start(
            request,
            config.UsesWebService,
            Math.Max(1, (int)config.EcrConfig.ResponseTimeout.TotalSeconds)) switch
        {
            StartResult.Started started => Accepted(started.Run),
            StartResult.Busy busy => Results.Conflict(new ErrorBody("A transaction is already running on this terminal", busy.ExistingId)),
            _ => throw new InvalidOperationException(),
        };
    }

    private static IResult Settle(SettlementBody body, EcrApp ecr, TransactionRegistry registry)
    {
        var terminal = body.TerminalSerial is { Length: > 0 } serial ? ecr.Terminals.FindBySerial(serial) : null;
        if (terminal == null)
        {
            return Fields(("terminal", body.TerminalSerial is { Length: > 0 } gone
                ? $"Terminal {gone} is no longer registered"
                : "Choose a POS terminal"));
        }

        var config = ecr.Engine.ResolveConfig(terminal);
        if (!config.Mode.IsSupportedInExample())
        {
            return Results.UnprocessableEntity(new FieldErrors(
                new Dictionary<string, string>(),
                "Settlement is not available for this terminal mode"));
        }

        if (!config.IsReady)
        {
            return Results.UnprocessableEntity(new FieldErrors(new Dictionary<string, string>(), string.Join("\n", config.Issues)));
        }

        return registry.Start(
            new TransactionRequest
            {
                Type = EcrTransactionType.Settlement,
                TerminalSerial = terminal.SerialNumber,
            },
            config.UsesWebService,
            Math.Max(1, (int)config.EcrConfig.ResponseTimeout.TotalSeconds)) switch
        {
            StartResult.Started started => Accepted(started.Run),
            StartResult.Busy busy => Results.Conflict(new ErrorBody("A transaction is already running on this terminal", busy.ExistingId)),
            _ => throw new InvalidOperationException(),
        };
    }

    private static IResult Accepted(TransactionSnapshot run) =>
        Results.Json(TransactionView.From(run), statusCode: StatusCodes.Status202Accepted);

    private static IResult NotFound() => Results.NotFound(new ErrorBody("No such transaction"));

    private static IResult Fields(params (string Field, string Message)[] errors) =>
        Results.UnprocessableEntity(new FieldErrors(errors.ToDictionary(e => e.Field, e => e.Message)));

    private static Dictionary<string, string> ToDictionary(FormErrors e)
    {
        var errors = new Dictionary<string, string>();
        if (e.Amount != null) errors["amount"] = e.Amount;
        if (e.ReceiptNumber != null) errors["receiptNumber"] = e.ReceiptNumber;
        if (e.OriginalTerminalId != null) errors["originalTerminalId"] = e.OriginalTerminalId;
        if (e.OriginalReference != null) errors["originalReference"] = e.OriginalReference;
        if (e.MerchantReference != null) errors["merchantReference"] = e.MerchantReference;
        return errors;
    }
}

/// <summary>
/// The raw form fields of the browser, turned into Core's <see cref="TransactionFormState"/>
/// with the same input rules the WPF form view-model applies as the operator types
/// (digits only, length caps) — so a request that skips the page is held to them too.
/// </summary>
public static class TransactionFormMapper
{
    public static (TransactionFormState? State, string? TypeError) ToState(TransactionFormBody body)
    {
        var type = EcrTransactionTypes.MenuOptions.Cast<EcrTransactionType?>()
            .FirstOrDefault(t => string.Equals(t.ToString(), body.Type?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (type == null) return (null, "Choose a transaction type");

        return (new TransactionFormState
        {
            Type = type.Value,
            // Same as the digits the WPF AmountInput keeps: ASCII digits, no leading zeros, capped.
            AmountDigits = Example.Core.Ui.AmountEntry.KeyedDigits(body.AmountDigits ?? ""),
            ReceiptNumber = Digits(body.ReceiptNumber).Take(TransactionFormState.ReceiptNumberMaxLength).Aggregate("", (s, c) => s + c),
            OriginalTerminalId = new string(Digits(body.OriginalTerminalId).ToArray()),
            OriginalDate = ParseDate(body.OriginalDate),
            UseOtherTerminal = body.UseOtherTerminal,
            LookUpByReference = body.LookUpByReference,
            // The WPF field caps this at 32 as it is typed. The sale's own reference is
            // not cut here: Validated reports an over-long one, with its message.
            OriginalReference = Truncate(body.OriginalReference ?? ""),
            MerchantReference = body.MerchantReference ?? "",
        }, null);
    }

    private static IEnumerable<char> Digits(string? value) => (value ?? "").Where(c => c >= '0' && c <= '9');

    private static string Truncate(string value) =>
        value.Length <= TransactionFormState.MerchantReferenceMaxLength ? value : value[..TransactionFormState.MerchantReferenceMaxLength];

    /// <summary>yyyy-MM-dd (a date input) or yyyyMMdd; blank is today, and the future is today, as in the WPF picker.</summary>
    private static DateTime ParseDate(string? value)
    {
        var today = DateTime.Today;
        if (!DateTime.TryParseExact(value?.Trim(), new[] { "yyyy-MM-dd", "yyyyMMdd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return today;
        }

        return date.Date > today ? today : date.Date;
    }
}
