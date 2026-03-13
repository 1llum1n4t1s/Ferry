using Avalonia.Media.Imaging;

namespace Ferry.Services;

/// <summary>
/// QR コード生成サービス。
/// </summary>
public interface IQrCodeService
{
    /// <summary>
    /// 指定した URL の QR コードビットマップを生成する。
    /// </summary>
    /// <param name="url">QR コードに埋め込む URL。</param>
    /// <returns>QR コードの Avalonia ビットマップ。</returns>
    Bitmap GenerateQrBitmap(string url);
}
