using System.Text.Json.Serialization;
using AmwalECR.Example.Core.Data;
using AmwalECR.WebExample.Api;
using AmwalECR.WebExample.Hosting;
using AmwalECR.WebExample.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// No request here carries more than a form.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Secrets at rest: Data Protection, with its key ring under the data folder — the
// cross-platform counterpart of the WPF app's DPAPI. Resolved from DI so the data
// folder can be set per host (tests point it at a temp folder).
builder.Services.AddSingleton<DataPaths>();
builder.Services.AddDataProtection().SetApplicationName("AmwalECR-WebExample");
builder.Services.AddOptions<KeyManagementOptions>().Configure<DataPaths, ILoggerFactory>((options, paths, loggers) =>
{
    paths.Ensure();
    options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(paths.Keys), loggers);
});
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

builder.Services.AddSingleton<EcrApp>();
builder.Services.AddSingleton(new TransactionRegistryOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new TransactionRegistry(
    sp.GetRequiredService<EcrApp>().Engine,
    sp.GetRequiredService<TransactionRegistryOptions>(),
    sp.GetRequiredService<TimeProvider>()));

var app = builder.Build();

app.UseExceptionHandler(errors => errors.Run(context =>
{
    // Never the exception text: it can name paths, and it is not for a browser. A body
    // that is not valid JSON is the caller's mistake, not the server's.
    var bad = context.Features.Get<IExceptionHandlerFeature>()?.Error is BadHttpRequestException;
    context.Response.StatusCode = bad ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
    return context.Response.WriteAsJsonAsync(new ErrorBody(bad ? "The request could not be read" : "Unexpected error"));
}));
app.UseSecurityHeaders();
app.UseApiGuard();

// Open the stores now so a bad data folder or an unreadable file is logged at
// start-up, not on the first request.
var ecr = app.Services.GetRequiredService<EcrApp>();
var logger = app.Logger;
logger.LogInformation("Data folder: {DataDir}", app.Services.GetRequiredService<DataPaths>().Root);
if (ecr.Terminals.LoadWarning is { } terminalsWarning) logger.LogWarning("{Warning}", terminalsWarning);
if (ecr.Settings.LoadWarning is { } settingsWarning) logger.LogWarning("{Warning}", settingsWarning);

app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? Array.Empty<string>();
    foreach (var address in addresses.Where(a => !IsLoopback(a)))
    {
        logger.LogWarning(
            "Listening on {Address}, which is not loopback. This sample has NO authentication and can take payments and edit signing keys; " +
            "see 'Before you expose this' in the README.", address);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapMeta();
app.MapTerminals();
app.MapSettings();
app.MapTransactions();
app.MapLogAndQr();

// An /api path nothing handles is a JSON 404, not the SPA's nothing.
app.MapFallback("/api/{**rest}", () => Results.NotFound(new ErrorBody("No such endpoint")));

app.Run();

static bool IsLoopback(string address) =>
    Uri.TryCreate(address.Replace("*", "0.0.0.0").Replace("+", "0.0.0.0"), UriKind.Absolute, out var uri)
    && (uri.IsLoopback || uri.Host == "[::1]");

// Lets tests host the app in-process.
public partial class Program;
