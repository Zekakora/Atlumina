using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>
/// Phase-4 vision analysis pass (CPU first): computes the perceptual hash and the
/// Laplacian blur score for every photo, then bulk-writes the results back to the
/// index so the "similar photos" and "blurry photos" features can reuse them without
/// re-decoding. Runs fully in the background (call it from a worker task).
/// </summary>
public sealed class VisionAnalysisService
{
    /// <summary>
    /// Threshold below which the Laplacian variance (in 0-255 byte domain) is considered
    /// "blurry". Empirically tuned on downscaled-to-256px luma; real-world libraries may
    /// want to tune it.
    /// </summary>
    public const double BlurThreshold = 100.0;

    /// <summary>Largest side of the luma sample used for the blur score.</summary>
    private const int BlurSampleSize = 256;

    private readonly PhotoDatabase _db;
    private volatile string? _lastError;

    public VisionAnalysisService(PhotoDatabase db)
    {
        _db = db;
    }

    /// <summary>
    /// Analyzes all photos that have not been analyzed yet. Returns the number of photos
    /// processed. <paramref name="progress"/> (created on the UI thread if UI is involved)
    /// receives (done, total, current file name).
    /// </summary>
    public async Task<VisionAnalysisResult> AnalyzeLibraryAsync(
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        var pending = await _db.GetPhotosPendingVisionAsync(limit: int.MaxValue);
        if (pending.Count == 0)
        {
            return new VisionAnalysisResult(0, 0, 0);
        }

        var now = DateTime.UtcNow;
        int failed = 0;
        var batch = new List<(long, string?, double?, DateTime)>(512);
        int degree = Math.Clamp(Environment.ProcessorCount, 2, 8);

        await Parallel.ForEachAsync(pending, new ParallelOptions
        {
            MaxDegreeOfParallelism = degree,
            CancellationToken = ct,
        }, (photo, token) =>
        {
            token.ThrowIfCancellationRequested();
            string? phash = null;
            double? blur = null;
            try
            {
                ulong hash = DuplicateService.ComputePhash(photo);
                phash = hash == 0 ? null : hash.ToString();
                var gray = WicGrayscale.GetGrayscale(photo.FilePath, BlurSampleSize);
                blur = gray is null ? null : ComputeLaplacianVariance(gray.Pixels, gray.Width, gray.Height);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Interlocked.Increment(ref failed);
            }
            lock (batch)
            {
                batch.Add((photo.Id, phash, blur, now));
            }
            int done = batch.Count;
            if (progress is not null && done % 10 == 0)
            {
                progress.Report((done, pending.Count, Path.GetFileName(photo.FilePath)));
            }
            return ValueTask.CompletedTask;
        });

        await _db.BulkSetVisionAsync(batch);

        progress?.Report((batch.Count, pending.Count, ""));

        int analyzed = batch.Count - failed;
        return new VisionAnalysisResult(pending.Count, analyzed, failed);
    }

    /// <summary>Last failure message (diagnostics; null when everything succeeded).</summary>
    public string? LastError => _lastError;    /// <summary>
    /// Laplacian variance of a luma image (higher = sharper edges). The classic focus
    /// metric: a blurry image has little high-frequency content, so the variance of the
    /// second derivative stays low. Returns the variance scaled back into the 0-255 byte
    /// domain (pixels are kept in [0,1] internally) so the threshold is a familiar number.
    /// </summary>
    public static double ComputeLaplacianVariance(double[] gray, int width, int height)
    {
        double sum = 0, sumSq = 0;
        int n = 0;
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                double laplacian =
                    4 * gray[i]
                    - gray[i - 1]
                    - gray[i + 1]
                    - gray[i - width]
                    - gray[i + width];
                sum += laplacian;
                sumSq += laplacian * laplacian;
                n++;
            }
        }
        if (n == 0)
        {
            return 0;
        }
        double mean = sum / n;
        return (sumSq / n - mean * mean) * 255.0 * 255.0;
    }
}

/// <summary>Result of one analysis pass.</summary>
public sealed record VisionAnalysisResult(int Total, int Analyzed, int Failed);
