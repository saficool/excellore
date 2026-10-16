using SkiaSharp;

namespace Excellore.Profile.Infrastructure;

public static class AvatarProcessor
{
    public const int MaxInputBytes = 700 * 1024;

    // Decoding and encoding creates a new image without metadata or original bytes.
    public static byte[]? Reencode(ReadOnlySpan<byte> input, string contentType)
    {
        if (input.Length is < 16 or > MaxInputBytes ||
            contentType is not ("image/png" or "image/jpeg" or "image/webp"))
            return null;
        using var data = SKData.CreateCopy(input);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width is < 1 or > 4096 ||
            codec.Info.Height is < 1 or > 4096 ||
            (long)codec.Info.Width * codec.Info.Height > 16_000_000)
            return null;
        var expected = contentType switch
        {
            "image/png" => SKEncodedImageFormat.Png,
            "image/jpeg" => SKEncodedImageFormat.Jpeg,
            _ => SKEncodedImageFormat.Webp
        };
        if (codec.EncodedFormat != expected) return null;
        using var bitmap = SKBitmap.Decode(data);
        if (bitmap is null) return null;
        var scale = Math.Min(1d, 512d / Math.Max(bitmap.Width, bitmap.Height));
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888,
            SKAlphaType.Premul));
        if (surface is null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true };
        surface.Canvas.DrawBitmap(bitmap, new SKRect(0, 0, width, height), paint);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 85);
        return encoded?.ToArray();
    }
}
