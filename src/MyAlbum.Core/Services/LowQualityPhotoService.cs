using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>
/// One logical photo group flagged for cleanup: every file sharing the same folder + base
/// name (e.g. DSC05361.ARW + DSC05361.JPG). Deleting a group removes all its members.
/// </summary>
public sealed class LowQualityPhotoGroup
{
    public required string Folder { get; init; }
    public required string Stem { get; init; }
    public List<PhotoRecord> Photos { get; } = new();

    /// <summary>Worst blur score among members (or null when none have one).</summary>
    public double? WorstBlur => Photos.Select(p => p.BlurScore).Where(s => s is not null).DefaultIfEmpty().Min();

    /// <summary>Worst aesthetic score among members (or null when none have one).</summary>
    public double? WorstAesthetic => Photos.Select(p => p.AestheticScore).Where(s => s is not null).DefaultIfEmpty().Min();

    public string Title => Stem;
    public string FolderText => Folder;

    /// <summary>Why this group is flagged: 模糊 / 低分 / 模糊+低分.</summary>
    public string ReasonText
    {
        get
        {
            bool blur = WorstBlur is { } b && b <= LowQualityPhotoService.BlurThreshold;
            bool low = WorstAesthetic is { } a && a <= LowQualityPhotoService.AestheticThreshold;
            return (blur, low) switch
            {
                (true, true) => "模糊 + 低分",
                (true, false) => "模糊",
                _ => "低分",
            };
        }
    }
}

/// <summary>
/// The "低质量照片清理" engine: makes sure every photo has a blur score (Laplacian, CPU)
/// and a NIMA aesthetic score (when the model is installed), then returns low-quality photo
/// groups (blurry and/or low-scoring) so the tool can delete whole groups into the recycle bin.
/// Combines the old 照片质量分析 (pHash+清晰度) and 照片美学分析 (NIMA) passes into one flow.
/// </summary>
public sealed class LowQualityPhotoService
{
    public const double BlurThreshold = VisionAnalysisService.BlurThreshold;         // 100
    public const double AestheticThreshold = DeepAnalysisService.LowAestheticThreshold; // 4.5

    private readonly PhotoDatabase _db;
    private readonly VisionAnalysisService _vision;
    private readonly AestheticScoreService _aesthetic;
    private readonly FormatCleanupService _cleanup;
    private volatile string? _lastError;

    public LowQualityPhotoService(
        PhotoDatabase db,
        VisionAnalysisService vision,
        AestheticScoreService aesthetic,
        FormatCleanupService cleanup)
    {
        _db = db;
        _vision = vision;
        _aesthetic = aesthetic;
        _cleanup = cleanup;
    }

    public string? LastError => _lastError;

    /// <summary>
    /// Runs the pending analysis passes so low-quality flags are complete:
    /// 1. vision (pHash + blur) for photos that never had it, then
    /// 2. NIMA aesthetic for photos that never had it (skipped when the model is missing).
    /// </summary>
    public async Task<QualityAnalyzeResult> AnalyzePendingAsync(
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        // 1) blur / pHash — CPU, always available.
        var visionResult = await _vision.AnalyzeLibraryAsync(progress, ct);

        // 2) NIMA aesthetic — only when the model is installed.
        int aestheticOk = 0, aestheticTotal = 0;
        if (AestheticScoreService.InstalledModelPath is not null)
        {
            var pending = await _db.GetPhotosPendingAestheticAsync(limit: int.MaxValue);
            aestheticTotal = pending.Count;
            var batch = new List<(long Id, double Score)>(512);
            int degree = Math.Clamp(Environment.ProcessorCount, 2, 8);
            var gate = new object();
            int failed = 0;
            var now = DateTime.UtcNow;
            await Parallel.ForEachAsync(pending, new ParallelOptions
            {
                MaxDegreeOfParallelism = degree,
                CancellationToken = ct,
            }, (photo, token) =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var score = _aesthetic.Score(photo.FilePath);
                    if (score is not null)
                    {
                        lock (gate) { batch.Add((photo.Id, score.Value)); }
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                    }
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    Interlocked.Increment(ref failed);
                }
                return ValueTask.CompletedTask;
            });
            await _db.BulkSetAestheticAsync(batch);
            aestheticOk = batch.Count;
        }

        return new QualityAnalyzeResult(
            visionResult.Total + aestheticTotal,
            visionResult.Analyzed + aestheticOk,
            visionResult.Failed + (aestheticTotal - aestheticOk));
    }

    /// <summary>
    /// Returns low-quality photos grouped into logical photo groups (same folder + base name),
    /// newest first. Only photos that have a blur or aesthetic score are considered.
    /// </summary>
    public async Task<List<LowQualityPhotoGroup>> GetLowQualityGroupsAsync(int limit = 10000)
    {
        var flagged = await _db.GetLowQualityPhotosAsync(BlurThreshold, AestheticThreshold, limit);
        if (flagged.Count == 0)
        {
            return [];
        }
        return flagged
            .GroupBy(p => (Dir: p.DirectoryPath, Stem: Path.GetFileNameWithoutExtension(p.FileName)))
            .Select(g => new LowQualityPhotoGroup
            {
                Folder = g.Key.Dir,
                Stem = g.Key.Stem,
            })
            .Select(g => { g.Photos.AddRange(flagged.Where(p => p.DirectoryPath == g.Folder && Path.GetFileNameWithoutExtension(p.FileName) == g.Stem)); return g; })
            .OrderByDescending(g => g.Photos.Max(p => p.TakenAtUtc ?? p.FileModifiedUtc))
            .ToList();
    }

    /// <summary>
    /// Deletes the given groups' source files into the recycle bin and removes their DB rows.
    /// Returns (deleted, failed) file counts.
    /// </summary>
    public async Task<(int Deleted, int Failed)> DeleteGroupsAsync(
        IEnumerable<LowQualityPhotoGroup> groups,
        CancellationToken ct = default)
    {
        var photos = groups.SelectMany(g => g.Photos).DistinctBy(p => p.FilePath).ToList();
        if (photos.Count == 0)
        {
            return (0, 0);
        }
        var result = await _cleanup.DeletePhotosAsync(photos, ct);
        return (result.DeletedCount, result.FailedCount);
    }
}

/// <summary>Result of one low-quality analysis pass.</summary>
public sealed record QualityAnalyzeResult(int Total, int Analyzed, int Failed);
