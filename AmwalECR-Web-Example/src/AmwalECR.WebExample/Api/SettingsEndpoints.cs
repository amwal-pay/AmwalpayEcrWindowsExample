using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Services;
using AmwalECR.WebExample.Hosting;
using AmwalPay.Ecr;

namespace AmwalECR.WebExample.Api;

/// <summary>
/// Environment and signing keys.
/// <para>
/// <b>Keys are write-only.</b> The WPF app can show a stored key; a browser must not
/// be able to, because this app has no authentication and anything the API returns
/// can be read by whoever can reach it. So every response says only whether a key is
/// configured, and the page offers Replace / Clear instead of Show / Hide.
/// </para>
/// </summary>
public static class SettingsEndpoints
{
    // The WPF settings screen's wording (SettingsViewModel).
    private const string InvalidKey = "Must be an even-length hex string of at least 16 characters";

    private static readonly string WifiLabel = LabelFor(EcrMode.Wifi);
    private static readonly string WebServiceLabel = LabelFor(EcrMode.WebService);

    public static void MapSettings(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings");

        group.MapGet("", (EcrApp ecr) => Results.Ok(View(ecr.Settings)));

        group.MapPut("/environment", (EnvironmentBody body, EcrApp ecr) =>
        {
            var name = body.Environment?.Trim() ?? "";
            var known = Enum.GetNames<EcrEnvironment>().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (known == null)
            {
                return Results.UnprocessableEntity(new FieldErrors(
                    new Dictionary<string, string> { ["environment"] = "Choose SIT, UAT or PROD" }));
            }

            ecr.Settings.Environment = Enum.Parse<EcrEnvironment>(known);
            return Saved(ecr.Settings);
        });

        group.MapPut("/keys/{kind}", (string kind, KeyBody body, EcrApp ecr) =>
        {
            if (!TryKind(kind, out var mode)) return Results.NotFound(new ErrorBody("No such key"));

            var key = body.Value?.Trim() ?? "";
            // Blank is what Clear is for: replacing a key with nothing by accident
            // would quietly disable the terminals that use it.
            if (key.Length == 0)
            {
                return Results.UnprocessableEntity(new FieldErrors(
                    new Dictionary<string, string> { ["value"] = "Enter the key, or use Clear to remove it" }));
            }
            // Unlike the WPF app, which saves what is typed key by key and flags it,
            // a key here is saved on request, so an invalid one is refused outright.
            if (!EcrConfig.IsValidSecureHashKey(key))
            {
                return Results.UnprocessableEntity(new FieldErrors(new Dictionary<string, string> { ["value"] = InvalidKey }));
            }

            Set(ecr.Settings, mode, key);
            return Saved(ecr.Settings);
        });

        group.MapDelete("/keys/{kind}", (string kind, EcrApp ecr) =>
        {
            if (!TryKind(kind, out var mode)) return Results.NotFound(new ErrorBody("No such key"));

            Set(ecr.Settings, mode, "");
            return Saved(ecr.Settings);
        });
    }

    private static SettingsView View(EcrExampleSettings settings) => new(
        settings.Environment.ToString(),
        new KeyView(WifiLabel, settings.WifiSecureHashKey.Length != 0),
        new KeyView(WebServiceLabel, settings.WebServiceSecureHashKey.Length != 0),
        settings.SaveError ?? settings.LoadWarning);

    /// <summary>The value stays in memory when the file cannot be written, so say so instead of a false "saved".</summary>
    private static IResult Saved(EcrExampleSettings settings) =>
        settings.SaveError is { } error
            ? Results.Json(new ErrorBody(error), statusCode: 500)
            : Results.Ok(View(settings));

    private static void Set(EcrExampleSettings settings, EcrMode mode, string key)
    {
        if (mode == EcrMode.WebService) settings.WebServiceSecureHashKey = key;
        else settings.WifiSecureHashKey = key;
    }

    private static bool TryKind(string kind, out EcrMode mode)
    {
        mode = kind switch { "wifi" => EcrMode.Wifi, "webService" => EcrMode.WebService, _ => 0 };
        return mode != 0;
    }

    /// <summary>The label the SDK plan uses for the mode's key, so it is not typed out a second time.</summary>
    private static string LabelFor(EcrMode mode) =>
        SelectedTerminalConfig.Resolve(
            new Terminal { EcrModeValue = mode.Value(), IpAddress = "127.0.0.1", Port = 9100, MerchantId = "1", TerminalId = "1" },
            EcrEnvironments.Default,
            secureHashKey: "").HashKeyLabel;
}
