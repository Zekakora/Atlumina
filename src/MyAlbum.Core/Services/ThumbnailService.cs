using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MyAlbum.Core.Models;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MyAlbum.Core.Services;

/// <summary>
/// Generates and caches JPEG renders (the L2 disk-cache layer) at multiple sizes:
/// small tiles for the grid and a larger preview for the detail panel.
/// For RAW files the WIC raw decoder's embedded preview is used to avoid a
/// full-resolution decode.
/// EXIF orientation is applied manually from <see cref="PhotoRecord.Orientation"/>
/// (read via MetadataExtractor) because WIC's built-in orientation handling is
/// unreliable here — the camera's embedded preview JPEG carries no orientation tag.
/// </summary>
public sealed class ThumbnailService
{
    public const int GridSize = 256;
    public const int PreviewSize = 1600;

    private readonly string _cacheDirectory;

    /// <summary>
    /// In-flight render tasks keyed by cache path, so different files can be decoded
    /// in parallel while concurrent requests for the same file share one task.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new();

    public ThumbnailService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>The directory where generated renders are stored.</summary>
    public string CacheDirectory => _cacheDirectory;

    /// <summary>Returns (or creates) the grid thumbnail for a photo.</summary>
    public Task<string?> GetOrCreateThumbnailAsync(PhotoRecord photo) =>
        GetOrCreateAsync(photo, GridSize, "t");

    /// <summary>Returns (or creates) the large preview render for a photo.</summary>
    public Task<string?> GetOrCreatePreviewAsync(PhotoRecord photo) =>
        GetOrCreateAsync(photo, PreviewSize, "p");

    public async Task<string?> GetOrCreateAsync(PhotoRecord photo, int maxDimension, string suffix)
    {
        Directory.CreateDirectory(_cacheDirectory);

        var cachePath = Path.Combine(_cacheDirectory, $"{HashPath(photo.FilePath)}_{suffix}_{maxDimension}.jpg");
        if (File.Exists(cachePath))
        {
            return cachePath;
        }

        var lazy = _inflight.GetOrAdd(cachePath,
            _ => new Lazy<Task<string?>>(
                () => CreateAsync(photo, cachePath, maxDimension),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        finally
        {
            _inflight.TryRemove(cachePath, out _);
        }
    }

    private async Task<string?> CreateAsync(PhotoRecord photo, string cachePath, int maxDimension)
    {
        try
        {
            await using var fileStream = File.OpenRead(photo.FilePath);
            using var ras = fileStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(ras);

            using SoftwareBitmap? bitmap = await DecodePreviewOrFrameAsync(decoder, maxDimension, photo.Orientation);
            if (bitmap is null)
            {
                return null;
            }

            using var outStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream);
            encoder.SetSoftwareBitmap(bitmap);
            encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(0.88f, PropertyType.Single) },
            }).AsTask().GetAwaiter().GetResult();
            await encoder.FlushAsync();

            await using var target = File.Create(cachePath);
            outStream.Seek(0);
            await outStream.AsStreamForRead().CopyToAsync(target);
            return cachePath;
        }
        catch (Exception)
        {
            TryDelete(cachePath);
            return null;
        }
    }

    private static async Task<SoftwareBitmap?> DecodePreviewOrFrameAsync(BitmapDecoder decoder, int maxDimension, int? orientation)
    {
        // 1) Embedded preview (RAW: the camera's processed JPEG preview).
        try
        {
            var previewStream = await decoder.GetPreviewAsync();
            if (previewStream is not null)
            {
                var previewDecoder = await BitmapDecoder.CreateAsync(previewStream);
                var frame = await previewDecoder.GetFrameAsync(0);
                var bmp = await DecodeScaledAsync(frame, maxDimension, orientation);
                if (bmp is not null)
                {
                    return bmp;
                }
            }
        }
        catch
        {
            // fall through to frame decode
        }

        // 2) Scaled decode of the first frame.
        var firstFrame = await decoder.GetFrameAsync(0);
        return await DecodeScaledAsync(firstFrame, maxDimension, orientation);
    }

    private static async Task<SoftwareBitmap?> DecodeScaledAsync(BitmapFrame frame, int maxDimension, int? orientation)
    {
        uint sourceW = frame.PixelWidth;
        uint sourceH = frame.PixelHeight;
        if (sourceW == 0 || sourceH == 0)
        {
            return null;
        }

        var rotation = OrientationToRotation(orientation);
        bool swaps = rotation is BitmapRotation.Clockwise90Degrees or BitmapRotation.Clockwise270Degrees;

        // Effective (post-rotation) dimensions.
        uint effW = swaps ? sourceH : sourceW;
        uint effH = swaps ? sourceW : sourceH;

        var (outW, outH) = ComputeScaled(effW, effH, maxDimension);

        // WIC BitmapTransform: ScaledWidth/ScaledHeight are pre-rotation values;
        // for 90/270 rotations the output width/height swap.
        var transform = new BitmapTransform
        {
            ScaledWidth = swaps ? outH : outW,
            ScaledHeight = swaps ? outW : outH,
            Rotation = rotation,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        return await frame.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
    }

    /// <summary>
    /// Maps EXIF orientation (1..8) to the rotation needed to display upright.
    /// Values 2/4/5/7 include mirroring which is rare on modern cameras; they are
    /// approximated by the rotation component only.
    /// </summary>
    private static BitmapRotation OrientationToRotation(int? orientation) => orientation switch
    {
        3 => BitmapRotation.Clockwise180Degrees,
        6 or 7 => BitmapRotation.Clockwise90Degrees,
        8 or 5 => BitmapRotation.Clockwise270Degrees,
        _ => BitmapRotation.None,
    };

    private static (uint Width, uint Height) ComputeScaled(uint width, uint height, int maxDimension)
    {
        double scale = Math.Min(1.0, maxDimension / (double)Math.Max(width, height));
        return (
            (uint)Math.Max(1, Math.Round(width * scale)),
            (uint)Math.Max(1, Math.Round(height * scale)));
    }

    private static string HashPath(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
