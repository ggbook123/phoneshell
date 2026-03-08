using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace PhoneShell.Services;

public sealed class QrCodeService
{
    public BitmapImage Generate(string payload, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(data);
        var pngBytes = qrCode.GetGraphic(pixelsPerModule);
        return LoadBitmap(pngBytes);
    }

    private static BitmapImage LoadBitmap(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
