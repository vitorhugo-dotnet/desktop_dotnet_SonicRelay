using SonicRelay.Windows.Presentation.Pairing;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class PairingQrCodeServiceTests
{
    [Fact]
    public void Render_returns_PNG_bytes_in_memory()
    {
        var service = new PairingQrCodeService();

        var bytes = service.RenderPng("opaque-backend-payload");

        Assert.True(bytes.Length > 8);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], bytes[..8]);
    }
}
