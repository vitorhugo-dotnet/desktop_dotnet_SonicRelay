using QRCoder;

namespace SonicRelay.Windows.Presentation.Pairing;

public interface IPairingQrCodeService
{
    byte[] RenderPng(string payload);
}

public sealed class PairingQrCodeService : IPairingQrCodeService
{
    public byte[] RenderPng(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        return code.GetGraphic(12);
    }
}
