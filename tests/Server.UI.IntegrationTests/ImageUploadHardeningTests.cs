#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using CleanArchitecture.Blazor.Server.UI.Services;
using NUnit.Framework;
using SkiaSharp;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// Pass 47b (carried over from ProjectTracking passes 29-30): image uploads on SkiaSharp. The profile photo is what
/// ImageSharp gave - a 128 × 128 JPEG at quality 80, 4:2:0, scaled to cover and centre-cropped - and the document image is
/// ImageSharp's size and PNG. Both now turn the image upright first, and the hardening holds before any pixel is decoded:
/// JPEG, PNG and WebP only, by content; at most <see cref="ImageProcessor.MaxPixels"/>; and no metadata in the output.
/// </summary>
[TestFixture]
public class ImageUploadHardeningTests
{
    /// <summary>300 × 200: the left half red, the right half blue.</summary>
    private static SKBitmap Halves()
    {
        var bitmap = new SKBitmap(new SKImageInfo(300, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);
        using var red = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(SKRect.Create(150, 200), red);
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
    {
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    private static Task<MemoryStream> ProcessAsync(byte[] upload) =>
        ImageProcessor.ResizeAndCompressToJpegAsync(new MemoryStream(upload), 128, 128, 80);

    /// <summary>The JPEG's segments before its image data: marker and payload.</summary>
    private static List<(byte Marker, byte[] Payload)> Segments(byte[] jpeg)
    {
        var segments = new List<(byte, byte[])>();
        for (var i = 2; i + 3 < jpeg.Length && jpeg[i] == 0xFF;)
        {
            var marker = jpeg[i + 1];
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            segments.Add((marker, jpeg[(i + 4)..(i + 2 + length)]));
            if (marker == 0xDA) break;
            i += 2 + length;
        }

        return segments;
    }

    /// <summary>A JPEG with an APP1 EXIF segment (an Orientation, 6 unless given, and GPS latitude 9° 4' N) and a comment after its SOI.</summary>
    private static byte[] WithGpsExifAndComment(byte[] jpeg, int orientation = 6)
    {
        var tiff = new List<byte>();
        void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16(v >> 16); }

        tiff.AddRange("II*\0"u8.ToArray()); U32(8);                         // little-endian TIFF header, IFD0 at 8
        U16(2);                                                              // IFD0: two entries
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);               //   Orientation (6: rotate 90°)
        U16(0x8825); U16(4); U32(1); U32(38);                                //   GPSInfo -> GPS IFD at 38
        U32(0);                                                              // no IFD1
        U16(2);                                                              // GPS IFD at 38: two entries
        U16(0x0001); U16(2); U32(2); tiff.AddRange("N\0\0\0"u8.ToArray());  //   GPSLatitudeRef = "N"
        U16(0x0002); U16(5); U32(3); U32(68);                                //   GPSLatitude -> rationals at 68
        U32(0);
        foreach (var (n, d) in new[] { (9, 1), (4, 1), (0, 1) }) { U32(n); U32(d); }

        var exif = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var comment = "Taken at the client's office"u8.ToArray();
        static byte[] Segment(byte marker, byte[] payload) =>
            new byte[] { 0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2) }.Concat(payload).ToArray();

        return jpeg[..2].Concat(Segment(0xE1, exif)).Concat(Segment(0xFE, comment)).Concat(jpeg[2..]).ToArray();
    }

    [TestCase(SKEncodedImageFormat.Png)]
    [TestCase(SKEncodedImageFormat.Jpeg)]
    [TestCase(SKEncodedImageFormat.Webp)]
    public async Task TheThreeOfferedFormats_StillBecomeA128SquareJpeg_AtQuality80_420(SKEncodedImageFormat format)
    {
        using var source = Halves();

        using var result = await ProcessAsync(Encode(source, format));

        var bytes = result.ToArray();
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        codec.EncodedFormat.Should().Be(SKEncodedImageFormat.Jpeg);
        (codec.Info.Width, codec.Info.Height).Should().Be((128, 128));

        var segments = Segments(bytes);
        // Quality 80 scales the standard luminance table's first entry, 16, to 6 (libjpeg: 16 × 40% rounded), as before.
        segments.First(s => s.Marker == 0xDB).Payload[1].Should().Be(6, "quality 80");
        // SOF0: the first component (Y) sampled 2 × 2 - 4:2:0, as ImageSharp's YCbCrRatio420.
        segments.First(s => s.Marker == 0xC0).Payload[7].Should().Be(0x22, "4:2:0");
    }

    [Test]
    public async Task ASmallImage_IsScaledUpTo128Square_AsBefore()
    {
        using var small = new SKBitmap(40, 30);
        small.Erase(SKColors.Green);

        using var result = await ProcessAsync(Encode(small, SKEncodedImageFormat.Png));

        using var codec = SKCodec.Create(new MemoryStream(result.ToArray()));
        (codec.Info.Width, codec.Info.Height).Should().Be((128, 128));
    }

    // X1
    [TestCase("gif")]
    [TestCase("bmp")]
    [TestCase("tiff")]
    public async Task AnyOtherFormat_IsRefusedFromItsContent_BeforeAnyDecoding(string format)
    {
        // Each is a real, decodable file: let through, SkiaSharp would decode the GIF and the BMP.
        var upload = format switch
        {
            "gif" => new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
                                  0x00, 0x00, 0x00, 0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00,
                                  0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B },
            "bmp" => Bmp2x2(),
            _ => "II*\0"u8.ToArray().Concat(new byte[64]).ToArray()
        };

        var act = () => ProcessAsync(upload);

        await act.Should().ThrowAsync<UnsupportedImageException>().WithMessage("Only JPEG, PNG and WebP images can be uploaded.");
    }

    private static byte[] Bmp2x2()
    {
        var pixels = new byte[] { 0, 0, 255, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0 }; // 2 rows of 2 BGR pixels, padded to 4
        var header = new List<byte>();
        void U16(int v) { header.Add((byte)v); header.Add((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16(v >> 16); }
        header.AddRange("BM"u8.ToArray()); U32(54 + pixels.Length); U32(0); U32(54);
        U32(40); U32(2); U32(2); U16(1); U16(24); U32(0); U32(pixels.Length); U32(2835); U32(2835); U32(0); U32(0);
        return header.Concat(pixels).ToArray();
    }

    // X2
    [Test]
    public async Task AnImageOverFiftyMegapixels_IsRefusedFromItsHeader()
    {
        // A real 8 × 8 JPEG whose frame header (SOF0) is changed to claim 8000 × 7000 = 56 megapixels.
        using var tiny = new SKBitmap(8, 8);
        tiny.Erase(SKColors.Gray);
        var jpeg = Encode(tiny, SKEncodedImageFormat.Jpeg);
        var sof = Enumerable.Range(2, jpeg.Length - 3).First(i => jpeg[i] == 0xFF && jpeg[i + 1] == 0xC0);
        (jpeg[sof + 5], jpeg[sof + 6], jpeg[sof + 7], jpeg[sof + 8]) = (7000 >> 8, 7000 & 0xFF, 8000 >> 8, 8000 & 0xFF);

        var act = () => ProcessAsync(jpeg);

        await act.Should().ThrowAsync<UnsupportedImageException>()
            .WithMessage("The image is 8000 × 7000 pixels; at most 50 million pixels can be uploaded.");
    }

    // X3
    [Test]
    public async Task AJpegCarryingGpsExifAndAComment_LeavesNoMetadataInTheOutput()
    {
        using var source = Halves();
        var upload = WithGpsExifAndComment(Encode(source, SKEncodedImageFormat.Jpeg));
        Segments(upload).Select(s => s.Marker).Should().Contain([0xE1, 0xFE], "the upload carries EXIF (with GPS) and a comment");

        using var result = await ProcessAsync(upload);

        var bytes = result.ToArray();
        Segments(bytes).Select(s => s.Marker).Where(m => m is >= 0xE1 and <= 0xEF or 0xFE).Should().BeEmpty(
            "no APP1-APP15 (EXIF, XMP, ICC, Photoshop) and no comment: only the JFIF header (APP0) survives");
        Encoding.ASCII.GetString(bytes).Should().NotContain("Exif").And.NotContain("client's office");
    }

    // X4. The EXIF orientation is applied before the crop; the output still carries no metadata.
    [TestCase(6, "top", "bottom")]  // turned 90° clockwise: the stored left (red) is seen at the top
    [TestCase(8, "bottom", "top")]  // turned 90° anticlockwise: the stored left is seen at the bottom
    [TestCase(3, "right", "left")]  // turned 180°: the stored left is seen on the right
    public async Task ExifOrientation_IsAppliedBeforeTheCrop(int orientation, string red, string blue)
    {
        using var source = Halves(); // stored: red left, blue right

        using var result = await ProcessAsync(WithGpsExifAndComment(Encode(source, SKEncodedImageFormat.Jpeg), orientation));

        var bytes = result.ToArray();
        using var output = SKBitmap.Decode(bytes);
        SKColor At(string side) => side switch
        {
            "top" => output.GetPixel(64, 10), "bottom" => output.GetPixel(64, 118),
            "left" => output.GetPixel(10, 64), _ => output.GetPixel(118, 64)
        };
        At(red).Red.Should().BeGreaterThan(200, $"Orientation {orientation} puts red on the {red}");
        At(blue).Blue.Should().BeGreaterThan(200, $"and blue on the {blue}");
        Segments(bytes).Select(sg => sg.Marker).Should().NotContain(0xE1, "the orientation is applied, not carried");
    }

    // ---- The document image (Documents -> upload): ImageSharp's size and PNG, measured on the code before pass 47b ----

    private static Task<MemoryStream> ScaleAsync(byte[] upload) => ImageProcessor.ScaleToPngAsync(new MemoryStream(upload));

    /// <summary>A <paramref name="width"/> × <paramref name="height"/> JPEG, the left half red and the right half blue.</summary>
    private static byte[] HalvesJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var red = new SKPaint { Color = SKColors.Red })
        {
            canvas.Clear(SKColors.Blue);
            canvas.DrawRect(SKRect.Create(width / 2f, height), red);
        }

        return Encode(bitmap, SKEncodedImageFormat.Jpeg);
    }

    /// <summary>The PNG's chunk types, in order.</summary>
    private static List<string> Chunks(byte[] png)
    {
        var chunks = new List<string>();
        for (var i = 8; i + 8 <= png.Length;)
        {
            var length = (png[i] << 24) | (png[i + 1] << 16) | (png[i + 2] << 8) | png[i + 3];
            var type = Encoding.ASCII.GetString(png, i + 4, 4);
            chunks.Add(type);
            if (type == "IEND") break;
            i += 12 + length;
        }

        return chunks;
    }

    // Each expected size is what ImageSharp 3.1.12 produced from the same upload with the dialog code before pass 47b: the factor
    // from the stored width (> 1600: 0.3, > 800: 0.5, else 0.9), applied to the upright width, rounded half to even.
    [TestCase(1000, 700, 1, 500, 350)]
    [TestCase(801, 333, 1, 400, 166)]
    [TestCase(900, 335, 1, 450, 168)]
    [TestCase(1000, 701, 1, 500, 350)]
    [TestCase(1000, 699, 1, 500, 350)]
    [TestCase(1601, 1001, 1, 480, 300)]
    [TestCase(300, 200, 6, 180, 270)]
    [TestCase(2000, 1500, 6, 450, 600)]
    [TestCase(1700, 900, 6, 270, 510)]
    public async Task ADocumentImage_IsScaledAsBefore_ToAnRgbPng(int width, int height, int orientation, int expectedWidth, int expectedHeight)
    {
        var upload = HalvesJpeg(width, height);
        if (orientation != 1) upload = WithGpsExifAndComment(upload, orientation);

        using var result = await ScaleAsync(upload);

        var bytes = result.ToArray();
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        codec.EncodedFormat.Should().Be(SKEncodedImageFormat.Png);
        (codec.Info.Width, codec.Info.Height).Should().Be((expectedWidth, expectedHeight));
        bytes[25].Should().Be(2, "an opaque upload is written as 8-bit RGB (colour type 2), as ImageSharp did");
    }

    [Test]
    public async Task ADocumentPngWithTransparency_StaysRgba()
    {
        using var clear = new SKBitmap(new SKImageInfo(100, 50, SKColorType.Rgba8888, SKAlphaType.Premul));
        clear.Erase(SKColors.Transparent);

        using var result = await ScaleAsync(Encode(clear, SKEncodedImageFormat.Png));

        result.ToArray()[25].Should().Be(6, "RGBA (colour type 6), as ImageSharp's RgbWithAlpha");
    }

    // X3, the document image: ImageSharp copied the EXIF (GPS included) into an eXIf chunk.
    [Test]
    public async Task ADocumentImageCarryingGpsExif_LeavesNoMetadataInThePng()
    {
        using var result = await ScaleAsync(WithGpsExifAndComment(HalvesJpeg(300, 200)));

        var bytes = result.ToArray();
        bytes[..8].Should().Equal(0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A);
        // Only the image and how it is encoded (sBIT: significant bits, written by Skia's encoder) - no metadata chunk.
        Chunks(bytes).Should().NotContain(["eXIf", "tEXt", "zTXt", "iTXt", "iCCP", "tIME", "pHYs"],
            "no EXIF (GPS included), text, colour profile, time or resolution reaches the stored PNG");
        Encoding.ASCII.GetString(bytes).Should().NotContain("Exif").And.NotContain("client's office");
    }

    // X4, the document image: ImageSharp applied orientation here (AutoOrient); it still is.
    [Test]
    public async Task ADocumentImage_IsTurnedUpright()
    {
        using var result = await ScaleAsync(WithGpsExifAndComment(HalvesJpeg(300, 200), 6));

        using var output = SKBitmap.Decode(result.ToArray());
        output.GetPixel(output.Width / 2, 5).Red.Should().BeGreaterThan(200, "Orientation 6 puts the stored left (red) on top");
        output.GetPixel(output.Width / 2, output.Height - 5).Blue.Should().BeGreaterThan(200, "and the right (blue) at the bottom");
    }

    // X1 and X2, the document image: the same refusals, before any decoding.
    [Test]
    public async Task ADocumentImage_IsRefusedTheSameWay()
    {
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
                               0x00, 0x00, 0x00, 0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00,
                               0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B };
        await ((Func<Task>)(() => ScaleAsync(gif))).Should().ThrowAsync<UnsupportedImageException>()
            .WithMessage("Only JPEG, PNG and WebP images can be uploaded.");
        await ((Func<Task>)(() => ScaleAsync(Bmp2x2()))).Should().ThrowAsync<UnsupportedImageException>()
            .WithMessage("Only JPEG, PNG and WebP images can be uploaded.");

        var jpeg = HalvesJpeg(8, 8);
        var sof = Enumerable.Range(2, jpeg.Length - 3).First(i => jpeg[i] == 0xFF && jpeg[i + 1] == 0xC0);
        (jpeg[sof + 5], jpeg[sof + 6], jpeg[sof + 7], jpeg[sof + 8]) = (7000 >> 8, 7000 & 0xFF, 8000 >> 8, 8000 & 0xFF);
        await ((Func<Task>)(() => ScaleAsync(jpeg))).Should().ThrowAsync<UnsupportedImageException>()
            .WithMessage("The image is 8000 × 7000 pixels; at most 50 million pixels can be uploaded.");
    }
}
#nullable restore
