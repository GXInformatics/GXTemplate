using SkiaSharp;

namespace CleanArchitecture.Blazor.Server.UI.Services;

/// <summary>
/// Pass 47b: an upload refused by <see cref="ImageProcessor"/> - not a JPEG, PNG or WebP by its content, larger than
/// <see cref="ImageProcessor.MaxPixels"/>, or not decodable. Its message is meant for the user.
/// </summary>
public sealed class UnsupportedImageException(string message) : Exception(message);

/// <summary>
/// Pass 47b: every image the app takes from a user goes through here, on SkiaSharp (it replaced ImageSharp). Every check
/// is made BEFORE a pixel is decoded:
/// <list type="number">
/// <item>the format is read from the file's first bytes, never its name, and only <see cref="AllowedFormats"/> is handed
/// to a decoder;</item>
/// <item>the header's dimensions must be within <see cref="MaxPixels"/>;</item>
/// <item>no metadata reaches the output: it is re-encoded from pixels decoded without a colour space, so it carries no
/// EXIF (GPS included), XMP, comment, text chunk or ICC profile.</item>
/// </list>
/// The EXIF orientation is the one thing read from the metadata: the image is turned upright before it is cropped or scaled.
/// </summary>
public static class ImageProcessor
{
    /// <summary>
    /// The largest image accepted, in pixels - 50 megapixels, checked from the header before any pixel is decoded.
    /// </summary>
    /// <remarks>
    /// Large enough for any camera photo a user is likely to pick (48 MP phones, 45 MP cameras); small enough that one
    /// decode holds at most about 200 MB (4 bytes a pixel). Without it, a few-kilobyte PNG or JPEG whose header claims
    /// 60,000 × 60,000 would ask for 14 GB the moment it was decoded. The upload's own byte limit
    /// (<c>GlobalVariables.MaxAllowedSize</c>, about 51 MB) does not bound this: compressed size says nothing of pixels.
    /// </remarks>
    public const long MaxPixels = 50_000_000;

    /// <summary>The formats decoded - the three the uploads offer (<c>Accept=".jpg, .jpeg, .png, .webp"</c>).</summary>
    public static readonly IReadOnlySet<SKEncodedImageFormat> AllowedFormats =
        new HashSet<SKEncodedImageFormat> { SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Png, SKEncodedImageFormat.Webp };

    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>
    /// The profile photo: resized to exactly <paramref name="width"/> × <paramref name="height"/> - scaled to cover, then
    /// centre-cropped, smaller images scaled up - and encoded as JPEG at <paramref name="quality"/>, 4:2:0. The size,
    /// format, quality and subsampling are what ImageSharp produced before pass 47b.
    /// </summary>
    /// <exception cref="UnsupportedImageException">The upload is refused.</exception>
    public static Task<MemoryStream> ResizeAndCompressToJpegAsync(Stream sourceStream, int width = 1200, int height = 1200, int quality = 80)
    {
        using var decoded = Decode(sourceStream);
        var upright = decoded.Upright;

        // Scale to cover, centre-crop (no distortion): as ImageSharp's ResizeMode.Crop with AnchorPositionMode.Center.
        var scale = Math.Max((float)width / upright.Width, (float)height / upright.Height);
        var cropWidth = width / scale;
        var cropHeight = height / scale;
        var crop = SKRect.Create((upright.Width - cropWidth) / 2, (upright.Height - cropHeight) / 2, cropWidth, cropHeight);

        using var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(upright))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawImage(image, crop, SKRect.Create(width, height), Smooth);
        }

        using var pixmap = output.PeekPixels();
        using var jpeg = pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
                         ?? throw new UnsupportedImageException("The image could not be encoded.");
        return Task.FromResult(new MemoryStream(jpeg.ToArray()));
    }

    /// <summary>
    /// The document image (Documents → upload): turned upright, scaled down keeping its aspect ratio, and encoded as PNG.
    /// The size is what ImageSharp produced before pass 47b: the factor is chosen from the stored width (over 1600 px:
    /// 0.3; over 800: 0.5; else 0.9) and applied to the upright width, rounded half to even; the height follows the
    /// aspect ratio, rounded half to even, at least 1. An opaque image is written as RGB, one with alpha as RGBA.
    /// </summary>
    /// <exception cref="UnsupportedImageException">The upload is refused.</exception>
    public static Task<MemoryStream> ScaleToPngAsync(Stream sourceStream)
    {
        using var decoded = Decode(sourceStream);
        var upright = decoded.Upright;

        var factor = decoded.StoredWidth > 1600 ? 0.3m : decoded.StoredWidth > 800 ? 0.5m : 0.9m;
        var width = Math.Max(1, (int)Math.Round(upright.Width * factor, MidpointRounding.ToEven));
        var height = Math.Max(1, (int)MathF.Round(upright.Height * width / (float)upright.Width, MidpointRounding.ToEven));

        var alpha = decoded.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        using var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, alpha));
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(upright))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(image, SKRect.Create(upright.Width, upright.Height), SKRect.Create(width, height), Smooth);
        }

        using var pixmap = output.PeekPixels();
        using var png = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6))
                        ?? throw new UnsupportedImageException("The image could not be encoded.");
        return Task.FromResult(new MemoryStream(png.ToArray()));
    }

    /// <summary>An upload decoded and turned upright, with what the callers need of its stored form.</summary>
    private sealed record Decoded(SKBitmap Upright, int StoredWidth, bool Opaque) : IDisposable
    {
        public void Dispose() => Upright.Dispose();
    }

    /// <summary>The three checks, in order, then the decode and the turn upright. Nothing is decoded before the checks pass.</summary>
    private static Decoded Decode(Stream sourceStream)
    {
        sourceStream.Position = 0;
        using var source = new MemoryStream();
        sourceStream.CopyTo(source);
        var bytes = source.ToArray();

        // 1. The format, from the content.
        var format = FormatOf(bytes);
        if (format is not { } detected || !AllowedFormats.Contains(detected))
        {
            throw new UnsupportedImageException("Only JPEG, PNG and WebP images can be uploaded.");
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
                          ?? throw new UnsupportedImageException("The image could not be read.");
        if (codec.EncodedFormat != detected)
        {
            throw new UnsupportedImageException("The image's content does not match its format.");
        }

        // 2. The size, from the header.
        var (sourceWidth, sourceHeight) = (codec.Info.Width, codec.Info.Height);
        if ((long)sourceWidth * sourceHeight > MaxPixels)
        {
            throw new UnsupportedImageException(
                $"The image is {sourceWidth} × {sourceHeight} pixels; at most {MaxPixels / 1_000_000} million pixels can be uploaded.");
        }

        // 3. Decode, with no colour space: an embedded ICC profile is not applied and cannot reach the output.
        var decodeInfo = new SKImageInfo(sourceWidth, sourceHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new UnsupportedImageException("The image could not be read.");
        }

        // Turned upright as the EXIF orientation says, before any crop or scale.
        return new Decoded(Upright(decoded, codec.EncodedOrigin), sourceWidth, codec.Info.AlphaType == SKAlphaType.Opaque);
    }

    /// <summary>
    /// <paramref name="stored"/> as it should be seen, given its EXIF <paramref name="origin"/> - the stored pixels'
    /// top-left corner. A copy, so the caller disposes it either way; 5 to 8 swap width and height.
    /// </summary>
    private static SKBitmap Upright(SKBitmap stored, SKEncodedOrigin origin)
    {
        var (w, h) = (stored.Width, stored.Height);
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        // Each maps a stored pixel (x, y) to where it is seen: x' = ScaleX·x + SkewX·y + TransX, y' = SkewY·x + ScaleY·y + TransY.
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // 2: mirrored
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // 3: turned 180°
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // 4: flipped
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // 5: transposed
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // 6: turned 90° clockwise
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // 7: transversed
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // 8: turned 90° anticlockwise
            _ => SKMatrix.Identity                                                     // 1, or none
        };

        var upright = new SKBitmap(new SKImageInfo(swaps ? h : w, swaps ? w : h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(upright);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(stored, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null); // whole-pixel turns: copied exactly
        return upright;
    }

    /// <summary>The image format named by the file's first bytes (its signature), or null when it names none known.</summary>
    public static SKEncodedImageFormat? FormatOf(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xFF, 0xD8, 0xFF, ..] => SKEncodedImageFormat.Jpeg,
        [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, ..] => SKEncodedImageFormat.Png,
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => SKEncodedImageFormat.Webp,
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => SKEncodedImageFormat.Gif,
        [(byte)'B', (byte)'M', ..] => SKEncodedImageFormat.Bmp,
        _ => null
    };
}
