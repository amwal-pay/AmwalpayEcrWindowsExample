using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Terminals;
using AmwalECR.WebExample.Hosting;

namespace AmwalECR.WebExample.Api;

/// <summary>Terminals CRUD, keyed by serial number. Rules and messages are <see cref="TerminalValidator"/>'s.</summary>
public static class TerminalEndpoints
{
    public static void MapTerminals(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/terminals");

        group.MapGet("", (EcrApp ecr) => Results.Ok(new
        {
            terminals = ecr.Terminals.GetAll().Select(TerminalView.From),
            notice = ecr.Terminals.LoadWarning,
        }));

        group.MapPost("", (TerminalForm form, EcrApp ecr) => Save(ecr, form, originalSerial: null));

        group.MapPut("/{serial}", (string serial, TerminalForm form, EcrApp ecr) =>
            ecr.Terminals.FindBySerial(serial) == null
                ? Results.NotFound(new ErrorBody("No such terminal"))
                : Save(ecr, form, serial));

        group.MapDelete("/{serial}", (string serial, EcrApp ecr) =>
        {
            if (ecr.Terminals.FindBySerial(serial) == null) return Results.NotFound(new ErrorBody("No such terminal"));
            return ecr.Terminals.Delete(serial) is SaveResult.Failed failed
                ? Results.Json(new ErrorBody($"Could not delete the terminal: {failed.Message}"), statusCode: 500)
                : Results.NoContent();
        });

        // announce=true is the WPF "selection changed" log line; the page passes it
        // only when the operator picks a terminal, not on every refresh.
        group.MapGet("/{serial}/config", (string serial, bool? announce, EcrApp ecr) =>
        {
            var terminal = ecr.Terminals.FindBySerial(serial);
            if (terminal == null) return Results.NotFound(new ErrorBody("No such terminal"));

            var config = ecr.Engine.ResolveConfig(terminal);
            if (announce == true) ecr.Engine.LogSelectedConfig(config);
            return Results.Ok(ConfigView.From(config));
        });
    }

    private static IResult Save(EcrApp ecr, TerminalForm form, string? originalSerial)
    {
        if (form.EcrMode is not { } value || EcrModes.FromValue(value) is not { } mode)
        {
            return Results.UnprocessableEntity(new FieldErrors(new Dictionary<string, string> { ["mode"] = "Choose an ECR mode" }));
        }

        var name = form.Name ?? "";
        var serial = form.SerialNumber ?? "";
        var ip = form.IpAddress ?? "";
        var port = form.Port ?? "";
        var terminalId = form.TerminalId ?? "";
        var merchantId = form.MerchantId ?? "";

        var errors = TerminalValidator.Validate(name, serial, mode, ip, port, terminalId, merchantId);
        if (errors.Any) return Results.UnprocessableEntity(new FieldErrors(ToDictionary(errors)));

        var terminal = TerminalValidator.ToTerminal(name, serial, mode, ip, port, terminalId, merchantId);
        return ecr.Terminals.Save(terminal, originalSerial) switch
        {
            SaveResult.Failed failed => Results.UnprocessableEntity(
                // A duplicate serial is shown against the serial field, where it can be fixed.
                new FieldErrors(new Dictionary<string, string> { ["serial"] = failed.Message })),
            _ => Results.Ok(TerminalView.From(terminal)),
        };
    }

    private static Dictionary<string, string> ToDictionary(TerminalErrors e)
    {
        var errors = new Dictionary<string, string>();
        if (e.Name != null) errors["name"] = e.Name;
        if (e.Serial != null) errors["serial"] = e.Serial;
        if (e.Ip != null) errors["ip"] = e.Ip;
        if (e.Port != null) errors["port"] = e.Port;
        if (e.TerminalId != null) errors["terminalId"] = e.TerminalId;
        if (e.MerchantId != null) errors["merchantId"] = e.MerchantId;
        return errors;
    }
}
