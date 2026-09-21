using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace AmwalECR.Example.Services;

/// <summary>
/// The e-receipt as a QR code, for the customer to scan.
/// <para>
/// Drawn on the PC rather than fetched, so it appears the moment the URL arrives
/// and works with no further network — the till may well be showing this on a
/// counter where the Wi-Fi has just dropped.
/// </para>
/// </summary>
public static class QrCodeGenerator
{
    /// <summary>A frozen bitmap of the code, or null when there is nothing to encode.</summary>
    public static BitmapImage? Create(string content, int pixelsPerModule = 8)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        try
        {
            using var generator = new QRCodeGenerator();
            // A receipt URL is long, and the code is scanned off a screen at arm's
            // length; M keeps it readable without crowding the modules.
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);

            var image = new BitmapImage();
            using var stream = new MemoryStream(png);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // Content too long for a QR code, or a codec failure: the dialog shows
            // the URL as text instead, so there is nothing more to do here.
            return null;
        }
    }
}
