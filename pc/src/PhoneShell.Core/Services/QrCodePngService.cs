using QRCoder;

namespace PhoneShell.Core.Services;

public sealed class QrCodePngService
{
    public byte[] Generate(string payload, int pixelsPerModule = 8)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("QR payload cannot be empty.", nameof(payload));

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(data);
        return qrCode.GetGraphic(pixelsPerModule);
    }
}
