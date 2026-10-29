using System;
using QRCoder;

namespace XrayMetro.Services;

public static class QrCodeService
{
    public static byte[] CreatePng(string text)
    {
        if (string.IsNullOrEmpty(text) || System.Text.Encoding.UTF8.GetByteCount(text) > 2200)
            throw new InvalidOperationException(LocalizationService.Pick("Ссылка слишком длинная для QR-кода.", "This link is too long for a QR code."));
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        using PngByteQRCode png = new(data);
        return png.GetGraphic(8);
    }
}
