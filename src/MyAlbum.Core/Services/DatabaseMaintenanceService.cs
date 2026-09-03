using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>Result of <see cref="DatabaseMaintenanceService.VerifyAsync"/>.</summary>
public sealed class DatabaseHealthReport
{
    public bool IntegrityOk { get; set; }
    public string? IntegrityMessage { get; set; }
    public long PhotoCount { get; set; }
    public long ActivePhotoCount { get; set; }
    public long RedundantMissingPhotoCount { get; set; }
    public int CaseDuplicateCount { get; set; }
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
    public int RemovedCaseDuplicates { get; set; }
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

        progress?.Report("正在检查缺失文件…");
        // Check disk existence for EVERY row, not just the ones already flagged IsMissing:
        // a file deleted externally is only flagged when the folder watcher or a folder scan
        // saw it disappear — if both were missed (app closed, folder removed, watcher dropped
        // the event), the flag is still 0 but the file is just as redundant.
        var rows = await _db.GetPhotoExistenceRowsAsync();
        report.RedundantMissingPhotoCount = CountMissingOnDisk(rows);

        progress?.Report("正在检查重复路径…");
        var dupRows = await _db.GetCaseDuplicateRowsAsync();
        report.CaseDuplicateCount = dupRows
            .GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Count() > 1);

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

        // 1) Drop every row whose file is gone from disk — regardless of whether the IsMissing
        //    flag was ever set (externally deleted files may never have been flagged). Also
        //    clear the flag on rows whose file came back (restored from recycle bin), so they
        //    are not stuck invisible in the grid. All existence checks run in parallel.
        progress?.Report("正在检查缺失照片记录…");
        var rows = await _db.GetPhotoExistenceRowsAsync();
        var removedPaths = new List<string>(rows.Count);
        var removedThumbs = new List<string?>(rows.Count);
        var restored = new List<string>();
        var sync = new object();
        Parallel.ForEach(rows, row =>
        {
            bool exists = File.Exists(row.FilePath);
            if (!exists)
            {
                lock (sync)
                {
                    removedPaths.Add(row.FilePath);
                    removedThumbs.Add(row.ThumbnailCachePath);
                }
            }
            else if (row.IsMissing)
            {
                lock (sync)
                {
                    restored.Add(row.FilePath);
                }
            }
        });
        foreach (var thumb in removedThumbs)
        {
            if (!string.IsNullOrEmpty(thumb))
            {
                TryDeleteFile(thumb);
            }
        }
        result.RemovedMissingPhotos = await _db.DeleteMissingPhotosAsync(removedPaths);
        if (restored.Count > 0)
        {
            await _db.MarkMissingBatchAsync(restored, false);
        }

        // 2) Case-insensitive duplicate FilePath rows: a file rewritten/renamed with a different
        //    case can leave two rows for one physical file (the FilePath UNIQUE is BINARY). Keep
        //    the freshest row per group, drop the rest (plus their thumbnails).
        progress?.Report("正在清理重复路径记录…");
        var dupRows = await _db.GetCaseDuplicateRowsAsync();
        var dupDrop = new List<(string Path, string? Thumb)>();
        foreach (var group in dupRows.GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() <= 1)
            {
                continue;
            }
            PhotoRecord? best = null;
            foreach (var row in group)
            {
                if (best is null || IsFresher(row, best))
                {
                    if (best is not null)
                    {
                        dupDrop.Add((best.FilePath, best.ThumbnailCachePath));
                    }
                    best = row;
                    continue;
                }
                dupDrop.Add((row.FilePath, row.ThumbnailCachePath));
            }
        }
        foreach (var (_, thumb) in dupDrop)
        {
            if (!string.IsNullOrEmpty(thumb))
            {
                TryDeleteFile(thumb);
            }
        }
        if (dupDrop.Count > 0)
        {
            result.RemovedCaseDuplicates = await _db.DeleteMissingPhotosAsync(dupDrop.Select(d => d.Path).ToList());
        }

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

    /// <summary>Counts rows whose file is absent from disk. Existence probes are IO-bound and
    /// run in parallel — for tens of thousands of photos this stays well under a second.</summary>
    private static int CountMissingOnDisk(List<PhotoDatabase.PhotoExistenceRow> rows)
    {
        int missing = 0;
        Parallel.ForEach(rows, row =>
        {
            if (!File.Exists(row.FilePath))
            {
                Interlocked.Increment(ref missing);
            }
        });
        return missing;
    }

    /// <summary>True when <paramref name="a"/> is the better of two rows pointing at the same
    /// physical file (case-insensitive duplicate): one whose size/time matches disk wins,
    /// otherwise the most recently indexed row.</summary>
    private static bool IsFresher(PhotoRecord a, PhotoRecord b)
    {
        bool aMatches = FingerprintMatchesDisk(a);
        bool bMatches = FingerprintMatchesDisk(b);
        if (aMatches != bMatches)
        {
            return aMatches;
        }
        return (a.IndexedAtUtc) >= (b.IndexedAtUtc);
    }

    private static bool FingerprintMatchesDisk(PhotoRecord p)
    {
        try
        {
            var fi = new FileInfo(p.FilePath);
            return fi.Exists
                && p.FileSizeBytes == fi.Length
                && Math.Abs((p.FileModifiedUtc - fi.LastWriteTimeUtc).TotalSeconds) < 2;
        }
        catch
        {
            return false;
        }
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
