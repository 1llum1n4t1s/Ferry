using System.IO;
using Avalonia.Media.Imaging;
using QRCoder;

namespace Ferry.Infrastructure;

/// <summary>
/// QRCoder を使用した QR コード画像生成。
/// </summary>
public sealed class QrCodeGenerator : Services.IQrCodeService
{
    /// <summary>
    /// 指定した URL の QR コードビットマップを生成する。
    /// </summary>
    public Bitmap GenerateQrBitmap(string url)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);
        var pngBytes = qrCode.GetGraphic(10);

        using var memStream = new MemoryStream(pngBytes);
        return new Bitmap(memStream);
    }
}
