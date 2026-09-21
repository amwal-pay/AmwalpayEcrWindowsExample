using AmwalECR.WebExample.Hosting;
using QRCoder;

namespace AmwalECR.WebExample.Api;

/// <summary>The debug output panel's feed, and the e-receipt QR.</summary>
public static class LogAndQrEndpoints
{
    /// <summary>A receipt URL is short; this is far above one and well below what a QR can carry.</summary>
    public const int MaxQrTextLength = 1024;

    public static void MapLogAndQr(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/log", (EcrApp ecr) => Results.Ok(new LogView(ecr.Log.Snapshot(), ecr.Log.Count, ecr.Log.Capacity)));

        app.MapDelete("/api/log", (EcrApp ecr) =>
        {
            ecr.Log.Clear();
            return Results.NoContent();
        });

        // Drawn here rather than by a script on the page: nothing to fetch, works with
        // no network, and no client library. SvgQRCode needs no System.Drawing.
        app.MapGet("/api/qr", (string? text) =>
        {
            if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new ErrorBody("Nothing to encode"));
            if (text.Length > MaxQrTextLength) return Results.BadRequest(new ErrorBody($"The text is longer than {MaxQrTextLength} characters"));

            try
            {
                using var generator = new QRCodeGenerator();
                // A receipt URL is scanned off a screen at arm's length; M keeps it
                // readable without crowding the modules.
                using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
                return Results.Content(new SvgQRCode(data).GetGraphic(pixelsPerModule: 8), "image/svg+xml");
            }
            catch (QRCoder.Exceptions.DataTooLongException)
            {
                return Results.BadRequest(new ErrorBody("The text is too long for a QR code"));
            }
        });
    }
}
