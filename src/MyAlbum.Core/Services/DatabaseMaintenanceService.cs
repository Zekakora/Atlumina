using MyAlbum.Core.Data;

namespace MyAlbum.Core.Services;

/// <summary>Result of <see cref="DatabaseMaintenanceService.VerifyAsync"/>.</summary>
public sealed class DatabaseHealthReport
{
    public bool IntegrityOk { get; set; }
    public string? IntegrityMessage { get; set; }
    public long PhotoCount { get; set; }
    public long ActivePhotoCount { get; set; }
    public long RedundantMissingPhotoCount { get; set; }
    public long TotalThumbnailCount { get; set; }
    public int OrphanThumbnailCount { get; set; }
    public long OrphanThumbnailBytes { get; set; }
    public long OrphanPhotoTagCount { get; set; }
    public long OrphanFaceCount { get; set; }
    public long DatabaseBytes { get; set; }
}

/// <summary>Outcome of <see cref="DatabaseMaintenanceService.CleanupAsync"/>.</summary>
public sealed class DatabaseCleanupResult
{
    public int RemovedMissingPhotos { get; set; }
    public int RemovedOrphanThumbnails { get; set; }
    public long FreedThumbnailBytes { get; set; }
    public long RemovedOrphanPhotoTags { get; set; }
    public long RemovedOrphanFaces { get; set; }
}

/// <summary>
/// Database maintenance: SQLite integrity check, then detection and removal of redundant
/// data — photo rows whose file no longer exists (IsMissing), orphaned thumbnail cache
/// files not referenced by any indexed photo, and orphaned tag/face links. Only the index
/// and the app's own cache are touched; original photo files are never modified or deleted.
/// </summary>
public sealed class DatabaseMaintenanceService
{
    private readonly PhotoDatabase _db;
    private readonly string _thumbnailCacheDirectory;

    public DatabaseMaintenanceService(PhotoDatabase db, ThumbnailService thumbs)
    {
        _db = db;
        _thumbnailCacheDirectory = thumbs.CacheDirectory;
    }

    public async Task<DatabaseHealthReport> VerifyAsync(IProgress<string>? progress = null)
    {
        var report = new DatabaseHealthReport();
        progress?.Report("正在检查数据库完整性…");
        try
        {
            var integrity = await _db.RunIntegrityCheckAsync();
            report.IntegrityOk = string.Equals(integrity.Trim(), "ok", StringComparison.OrdinalIgnoreCase);
            report.IntegrityMessage = report.IntegrityOk ? "正常" : integrity.Trim();
        }
        catch (Exception ex)
        {
            report.IntegrityOk = false;
            report.IntegrityMessage = ex.Message;
        }

        progress?.Report("正在统计照片记录…");
        var counts = await _db.GetTotalAndActivePhotoCountsAsync();
        report.PhotoCount = counts.Total;
        report.ActivePhotoCount = counts.Active;

        progress?.Report("正在检查缺失记录…");
        var missing = await _db.GetMissingPhotosAsync();
        report.RedundantMissingPhotoCount = missing.Count(m => !File.Exists(m.FilePath));

        progress?.Report("正在扫描孤立缩略图…");
        var (orphans, totalCount) = await ScanThumbnailCacheAsync(progress);
        report.TotalThumbnailCount = totalCount;
        report.OrphanThumbnailCount = orphans.Count;
        report.OrphanThumbnailBytes = orphans.Sum(o => o.Bytes);

        progress?.Report("正在统计孤立关联…");
        var links = await _db.CountOrphanLinksAsync();
        report.OrphanPhotoTagCount = links.Tags;
        report.OrphanFaceCount = links.Faces;

        report.DatabaseBytes = DbFileSize();
        progress?.Report("校验完成");
        return report;
    }

    public async Task<DatabaseCleanupResult> CleanupAsync(IProgress<string>? progress = null)
    {
        var result = new DatabaseCleanupResult();

        // 1) Drop redundant IsMissing rows (file really gone) + their grid thumbnails.
        progress?.Report("正在清理缺失照片记录…");
        var missing = await _db.GetMissingPhotosAsync();
        var toRemove = missing.Where(m => !File.Exists(m.FilePath)).ToList();
        foreach (var m in toRemove)
        {
            if (!string.IsNullOrEmpty(m.ThumbnailCachePath))
            {
                TryDeleteFile(m.ThumbnailCachePath);
            }
        }
        result.RemovedMissingPhotos = await _db.DeleteMissingPhotosAsync(toRemove.Select(m => m.FilePath).ToList());

        // 2) Orphan thumbnails (path hash not referenced by any indexed photo). Re-query
        //    the referenced set AFTER removing the missing rows so their preview renders
        //    become orphans too and are collected in the same pass.
        progress?.Report("正在扫描并清理孤立缩略图…");
        var (orphans, _) = await ScanThumbnailCacheAsync(progress);
        foreach (var orphan in orphans)
        {
            TryDeleteFile(orphan.Path);
        }
        result.RemovedOrphanThumbnails = orphans.Count;
        result.FreedThumbnailBytes = orphans.Sum(o => o.Bytes);

        // 3) Orphaned tag/face links.
        progress?.Report("正在清理孤立标签/人脸关联…");
        var links = await _db.DeleteOrphanLinksAsync();
        result.RemovedOrphanPhotoTags = links.Tags;
        result.RemovedOrphanFaces = links.Faces;

        progress?.Report("清理完成");
        return result;
    }

    /// <summary>
    /// Scans the thumbnail cache for files whose path-hash prefix matches no indexed photo.
    /// Returns (orphaned files, total jpg count in the cache).
    /// </summary>
    private async Task<(List<(string Path, long Bytes)> Orphans, long Total)> ScanThumbnailCacheAsync(IProgress<string>? progress = null)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in await _db.GetAllPhotoFilePathsAsync())
        {
            referenced.Add(HashPath(path));
        }

        var orphans = new List<(string, long)>();
        long total = 0;
        if (!Directory.Exists(_thumbnailCacheDirectory))
        {
            return (orphans, total);
        }
        long lastReport = 0;
        foreach (var file in Directory.EnumerateFiles(_thumbnailCacheDirectory, "*.jpg"))
        {
            total++;
            try
            {
                var name = Path.GetFileName(file);
                int underscore = name.IndexOf('_');
                var hash = underscore > 0 ? name[..underscore] : name;
                if (!referenced.Contains(hash))
                {
                    orphans.Add((file, new FileInfo(file).Length));
                }
            }
            catch
            {
                // unreadable cache file: skip
            }
            if (progress is not null && (total % 500 == 0 || Environment.TickCount64 - lastReport >= 200))
            {
                lastReport = Environment.TickCount64;
                progress.Report($"正在扫描孤立缩略图… 已检查 {total} 个");
            }
        }
        return (orphans, total);
    }

    /// <summary>Mirror of <c>ThumbnailService.HashPath</c>: first 24 hex chars of the SHA-256 of the path.</summary>
    private static string HashPath(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private long DbFileSize()
    {
        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                var file = _db.DatabasePath + suffix;
                if (File.Exists(file))
                {
                    total += new FileInfo(file).Length;
                }
            }
            catch
            {
                // best effort
            }
        }
        return total;
    }

    private static void TryDeleteFile(string path)
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
            // cache cleanup is best-effort
        }
    }
}
