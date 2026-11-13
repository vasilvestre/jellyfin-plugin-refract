using Net.Codecrete.QrCodeGenerator;

namespace Jellyfin.Plugin.Refract.Tests;

/// <summary>
/// The widget renders the QR the server encodes for a device-code link.
/// </summary>
public class QrCodeTests
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>The verification URI must encode to a PNG the widget can render.</summary>
    [Fact]
    public void VerificationUriEncodesToAPng()
    {
        var png = QrCode.EncodeText("https://getrefract.app/device", QrCode.Ecc.Medium).ToPngBitmap(4, 8);

        Assert.True(png.Length > PngSignature.Length);
        Assert.Equal(PngSignature, png[..PngSignature.Length]);
    }
}
