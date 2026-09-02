using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace MyAlbum.Core.Services;

/// <summary>A decoded luma image: row-major pixels in [0,1] plus its dimensions.</summary>
public sealed record WicGrayPixels(int Width, int Height, double[] Pixels);

/// <summary>
/// A decoded RGB image for neural-network preprocessing: flat interleaved RGB bytes in
/// [0,255] (row-major) plus its dimensions.
/// </summary>
public sealed record WicRgbPixels(int Width, int Height, byte[] Rgb);

/// <summary>
/// Shared WIC helper that decodes a photo's pixels as a flat grayscale (luma) array.
/// Used by both the perceptual hash and the Laplacian blur detector so the expensive
/// decode path lives in exactly one place. The green channel is used as luma because
/// some RAW/HIF decoders reject Gray8 ("bitmap pixel format is unsupported").
/// </summary>
public static class WicGrayscale
{
    /// <summary>
    /// Decodes <paramref name="filePath"/> scaled so its largest side is at most
    /// <paramref name="maxDimension"/> while preserving aspect ratio (used by the blur
    /// detector). Returns null if the file can't be decoded.
    /// </summary>
    public static WicGrayPixels? GetGrayscale(string filePath, int maxDimension)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            using var ras = fs.AsRandomAccessStream();
            var decoder = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
            var frame = decoder.GetFrameAsync(0).AsTask().GetAwaiter().GetResult();

            uint sourceW = frame.PixelWidth;
            uint sourceH = frame.PixelHeight;
            if (sourceW == 0 || sourceH == 0)
            {
                return null;
            }

            double scale = Math.Min(1.0, maxDimension / (double)Math.Max(sourceW, sourceH));
            uint outW = (uint)Math.Max(1, Math.Round(sourceW * scale));
            uint outH = (uint)Math.Max(1, Math.Round(sourceH * scale));
            return Decode(filePath, outW, outH);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes <paramref name="filePath"/> scaled to exactly <paramref name="width"/>×
    /// <paramref name="height"/> (stretched, used by the perceptual hash which needs a
    /// fixed 32×32 grid). Returns null if the file can't be decoded.
    /// </summary>
    public static WicGrayPixels? GetGrayscaleFixed(string filePath, int width, int height)
    {
        try
        {
            return Decode(filePath, (uint)width, (uint)height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes <paramref name="filePath"/> scaled to exactly <paramref name="width"/>×
    /// <paramref name="height"/> and returns interleaved RGB bytes (not BGRA) for neural
    /// network preprocessing. Stretches to the target size (classifiers such as MobileNet
    /// expect a fixed 224×224 square). Returns null if the file can't be decoded.
    /// </summary>
    public static WicRgbPixels? GetRgbFixed(string filePath, int width, int height)
    {
        try
        {
            return DecodeRgb(filePath, (uint)width, (uint)height);
        }
        catch
        {
            return null;
        }
    }

    private static WicGrayPixels? Decode(string filePath, uint outW, uint outH)
    {
        using var fs = File.OpenRead(filePath);
        using var ras = fs.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
        var frame = decoder.GetFrameAsync(0).AsTask().GetAwaiter().GetResult();

        var transform = new BitmapTransform
        {
            ScaledWidth = outW,
            ScaledHeight = outH,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bmp = frame.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
        if (bmp is null)
        {
            return null;
        }

        int count = (int)(outW * outH);
        var buffer = new byte[count * 4];
        bmp.CopyToBuffer(buffer.AsBuffer());
        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = buffer[i * 4 + 1] / 255.0;
        }
        return new WicGrayPixels((int)outW, (int)outH, result);
    }

    /// <summary>Same decode path as <see cref="Decode"/> but returns interleaved RGB.</summary>
    private static WicRgbPixels? DecodeRgb(string filePath, uint outW, uint outH)
    {
        using var fs = File.OpenRead(filePath);
        using var ras = fs.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
        var frame = decoder.GetFrameAsync(0).AsTask().GetAwaiter().GetResult();

        var transform = new BitmapTransform
        {
            ScaledWidth = outW,
            ScaledHeight = outH,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bmp = frame.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
        if (bmp is null)
        {
            return null;
        }

        int count = (int)(outW * outH);
        var buffer = new byte[count * 4];
        bmp.CopyToBuffer(buffer.AsBuffer());
        var rgb = new byte[count * 3];
        for (int i = 0, o = 0; i < count; i++, o += 3)
        {
            int b = i * 4;
            rgb[o] = buffer[b + 2];     // R
            rgb[o + 1] = buffer[b + 1]; // G
            rgb[o + 2] = buffer[b];     // B
        }
        return new WicRgbPixels((int)outW, (int)outH, rgb);
    }
}
