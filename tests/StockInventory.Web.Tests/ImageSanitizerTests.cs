using SkiaSharp;
using StockInventory.Web.Vision;

namespace StockInventory.Web.Tests;

public class ImageSanitizerTests
{
    static ImageRejection Rejected(byte[] b) => Assert.Throws<ImageRejectedException>(() => ImageSanitizer.Sanitize(b)).Reason;

    [Fact]
    public void Jpeg_WithExif_IsReencodedWithoutMetadata()
    {
        var src = Img.JpegWithExifGps();
        Assert.True(Img.ContainsAscii(src, Img.ExifMarker));
        var o = ImageSanitizer.Sanitize(src);
        Assert.Equal("image/jpeg", o.MimeType);
        Assert.False(Img.ContainsAscii(o.Bytes, Img.ExifMarker));
        Assert.False(Img.ContainsAscii(o.Bytes, "Exif"));
        using var codec = SKCodec.Create(SKData.CreateCopy(o.Bytes));
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
    }

    [Fact]
    public void Png_And_WebP_AreAccepted_AndConvertedToJpeg()
    {
        Assert.Equal("image/jpeg", ImageSanitizer.Sanitize(Img.Png()).MimeType);
        Assert.Equal("image/jpeg", ImageSanitizer.Sanitize(Img.WebP()).MimeType);
    }

    [Fact]
    public void TransparentPng_IsFlattenedOnWhite()
    {
        var o = ImageSanitizer.Sanitize(Img.Png(16, 16, SKColors.Transparent));
        using var bmp = SKBitmap.Decode(o.Bytes);
        var p = bmp.GetPixel(8, 8);
        Assert.True(p.Red > 240 && p.Green > 240 && p.Blue > 240);
    }

    [Fact]
    public void LargeImage_IsDownscaledToSendLongEdge()
    {
        var o = ImageSanitizer.Sanitize(Img.Png(4000, 1000));
        using var bmp = SKBitmap.Decode(o.Bytes);
        Assert.Equal(ImageSanitizer.SendLongEdge, bmp.Width);
        Assert.Equal(512, bmp.Height);
    }

    [Fact]
    public void Boundary_4096LongEdge_IsAccepted() => ImageSanitizer.Sanitize(Img.Png(4096, 100));

    [Theory]
    [InlineData(4097, 10)]
    [InlineData(10, 4097)]
    [InlineData(4000, 4001)]
    public void TooManyPixels_IsTooLarge(int w, int h) => Assert.Equal(ImageRejection.TooLarge, Rejected(Img.Png(w, h)));

    [Fact]
    public void NotAnImage_Heic_Gif_Empty_AreUnsupported()
    {
        Assert.Equal(ImageRejection.UnsupportedMedia, Rejected("hello"u8.ToArray()));
        Assert.Equal(ImageRejection.UnsupportedMedia, Rejected(Img.Heic()));
        Assert.Equal(ImageRejection.UnsupportedMedia, Rejected(Img.Gif()));
        Assert.Equal(ImageRejection.UnsupportedMedia, Rejected([]));
        Assert.Equal(ImageRejection.UnsupportedMedia, Rejected("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()));
        Assert.Equal(ImageRejection.UnsupportedMedia, Rejected("%PDF-1.4"u8.ToArray()));
    }

    [Fact]
    public void Truncated_Or_Corrupt_IsUnsupported()
    {
        var jpeg = Img.Jpeg(300, 300);
        Assert.Equal(ImageRejection.UnsupportedMedia, Rejected(jpeg[..(jpeg.Length / 3)]));
        var png = Img.Png(300, 300);
        Assert.Equal(ImageRejection.UnsupportedMedia, Rejected(png[..(png.Length / 2)]));
        Assert.Equal(ImageRejection.UnsupportedMedia, Rejected([0xFF, 0xD8, 0xFF, 0x00, 0x01]));
        Assert.Equal(ImageRejection.UnsupportedMedia, Rejected([.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. "WEBP"u8.ToArray()]));
    }
}
