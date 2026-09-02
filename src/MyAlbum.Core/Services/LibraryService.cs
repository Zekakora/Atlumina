using System.Collections.Concurrent;
using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>
/// Scans folders into the library index. Files whose size and last-write time are
/// unchanged since the last scan are skipped (incremental indexing). Thumbnails are
/// generated for new/changed files and the missing flag is set for removed ones.
/// </summary>
public sealed class LibraryService
{
    /// <summary>Photos per SQLite batch transaction during a scan.</summary>
    private const int WriteBatchSize = 500;

    /// <summary>
    /// Upper bound for parallel EXIF reads / thumbnail decodes. Decoding is CPU and
    /// disk bound; more than this adds little and can thrash the cache.
    /// </summary>
    private const int MaxScanParallelism = 8;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arw", ".cr2", ".cr3", ".nef", ".raf", ".dng", ".orf", ".rw2", ".pef", ".srw", ".raw",
        ".jpg", ".jpeg", ".hif", ".heic", ".heif", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff",
    };

    private readonly PhotoDatabase _db;
    private readonly MetadataReaderService _reader;
    private readonly ThumbnailService _thumbs;

    public LibraryService(PhotoDatabase db, MetadataReaderService reader, ThumbnailService thumbs)
    {
        _db = db;
        _reader = reader;
        _thumbs = thumbs;
    }

    public static bool IsSupportedFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    public static IEnumerable<string> EnumerateImages(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(IsSupportedFile)
            .OrderBy(f => f);

    /// <summary>Re-indexes a single file (used by the folder watcher).</summary>
    public async Task<bool> IndexFileAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var photo = _reader.Read(filePath);
            photo.ThumbnailCachePath = await _thumbs.GetOrCreateThumbnailAsync(photo);
            await _db.UpsertPhotoAsync(photo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Re-reads metadata after an EXIF edit and updates the index row, keeping the
    /// existing thumbnail cache path (the pixels did not change, only the metadata).
    /// Returns the updated record, or null if the file is gone / unreadable.
    /// </summary>
    public async Task<PhotoRecord?> RefreshMetadataAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var existing = await _db.GetPhotoByPathAsync(filePath);
            var photo = _reader.Read(filePath);
            if (existing is not null)
            {
                photo.Id = existing.Id;
                photo.ThumbnailCachePath = existing.ThumbnailCachePath;
                photo.Rating = existing.Rating;
            }
            await _db.UpsertPhotoAsync(photo);
            return photo;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ScanResult> ScanFolderAsync(
        string folder,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default,
        SemaphoreSlim? sharedConcurrency = null)
    {
        var result = new ScanResult { Folder = folder };

        // Register the folder up front (not after the photo batches) so a mid-import
        // app close still leaves it listed in Settings and watched on the next start.
        // The end-of-scan upsert below just refreshes LastScannedUtc.
        await _db.UpsertFolderAsync(new FolderRecord
        {
            Path = folder,
            IsWatched = true,
            AddedUtc = DateTime.UtcNow,
        });

        // Load the previous index rows for this folder (incl. subfolders) as full records so a
        // re-scan can (a) skip unchanged files cheaply and (b) preserve user-owned / derived
        // fields (rating, GPS place, AI analysis) when a file DID change.
        var existing = (await _db.GetPhotosByDirectoryPrefixAsync(folder))
            .ToDictionary(p => p.FilePath, StringComparer.OrdinalIgnoreCase);
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var counters = new ScanCounters();
        var pending = new ConcurrentBag<PhotoRecord>();
        var throttled = progress is null ? null : new ThrottledProgress(progress, TimeSpan.FromMilliseconds(100));

        // Enumerate on a thread-pool thread: AllDirectories walk + sort of tens of
        // thousands of files must never block the UI thread.
        var files = await Task.Run(() => EnumerateImages(folder).ToList(), ct);
        result.TotalFiles = files.Count;

        // sharedConcurrency 由调用方传入时，跨多个文件夹共享同一把信号量，从而把
        // "同时解码的文件数"限制为用户设定的总预算（文件夹之间并行、但文件级总并发受控）；
        // 不传则每个文件夹用自身默认的 CPU 核数上限并发。
        SemaphoreSlim? ownedSem = null;
        SemaphoreSlim concurrency = sharedConcurrency
            ?? (ownedSem = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 2, MaxScanParallelism)));
        int maxDop = sharedConcurrency is null
            ? Math.Clamp(Environment.ProcessorCount, 2, MaxScanParallelism)
            : Math.Max(1, Math.Min(Environment.ProcessorCount * 2, 64));

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = maxDop,
        };

        int processed = 0;
        try
        {
            await Parallel.ForEachAsync(files, parallelOptions, async (file, token) =>
            {
                await concurrency.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    seen.TryAdd(file, 0);
                    int done = Interlocked.Increment(ref processed);

                    try
                    {
                        var fi = new FileInfo(file);
                        if (existing.TryGetValue(file, out var rec)
                            && rec.FileSizeBytes == fi.Length
                            && Math.Abs((rec.FileModifiedUtc - fi.LastWriteTimeUtc).TotalSeconds) < 2
                            && rec.ThumbnailCachePath is not null
                            && File.Exists(rec.ThumbnailCachePath))
                        {
                            Interlocked.Increment(ref counters.Skipped);
                        }
                        else
                        {
                            var photo = _reader.Read(file);
                            // Preserve user-owned / derived fields from the previous index row so a
                            // re-scan (e.g. EXIF or file-modified-time changed) never wipes the star
                            // rating, GPS place / normalized address, or AI analysis results. Only the
                            // file-derived metadata and the thumbnail are refreshed.
                            if (existing.TryGetValue(file, out var old))
                            {
                                photo.Id = old.Id;
                                photo.Rating = old.Rating;
                                photo.Tags = old.Tags;
                                photo.GpsPlace = old.GpsPlace;
                                photo.PlaceCountry = old.PlaceCountry;
                                photo.PlaceProvince = old.PlaceProvince;
                                photo.PlaceCity = old.PlaceCity;
                                photo.PlaceDistrict = old.PlaceDistrict;
                                photo.PlaceLandmark = old.PlaceLandmark;
                                photo.GpsPlaceSource = old.GpsPlaceSource;
                                photo.GpsPlaceFailed = old.GpsPlaceFailed;
                                photo.PHash = old.PHash;
                                photo.BlurScore = old.BlurScore;
                                photo.AiAnalyzedAtUtc = old.AiAnalyzedAtUtc;
                                photo.AestheticScore = old.AestheticScore;
                                photo.DominantColors = old.DominantColors;
                                photo.IsMono = old.IsMono;
                                photo.Embedding = old.Embedding;
                                photo.ClipEmbedding = old.ClipEmbedding;
                                photo.ObjectsJson = old.ObjectsJson;
                                photo.DeepAnalyzedAtUtc = old.DeepAnalyzedAtUtc;
                            }
                            photo.ThumbnailCachePath = await _thumbs.GetOrCreateThumbnailAsync(photo);
                            pending.Add(photo);
                            Interlocked.Increment(ref counters.Indexed);
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref counters.Failed);
                    }

                    throttled?.Report(new ScanProgress
                    {
                        Folder = folder,
                        CurrentFile = file,
                        TotalFiles = result.TotalFiles,
                        Processed = done,
                        Indexed = Volatile.Read(ref counters.Indexed),
                        Skipped = Volatile.Read(ref counters.Skipped),
                        Failed = Volatile.Read(ref counters.Failed),
                    });
                }
                finally
                {
                    concurrency.Release();
                }
            });
        }
        finally
        {
            ownedSem?.Dispose();
        }

        // Persist new/changed records in batched transactions.
        var toWrite = pending.ToArray();
        for (int i = 0; i < toWrite.Length; i += WriteBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            await _db.BulkUpsertPhotosAsync(toWrite.AsSpan(i, Math.Min(WriteBatchSize, toWrite.Length - i)).ToArray(), ct);
        }

        result.Indexed = counters.Indexed;
        result.Skipped = counters.Skipped;
        result.Failed = counters.Failed;

        // Files that disappeared since the last scan.
        foreach (var path in existing.Keys.Where(p => !seen.ContainsKey(p)))
        {
            await _db.MarkMissingAsync(path, true);
            result.MarkedMissing++;
        }

        await _db.UpsertFolderAsync(new FolderRecord
        {
            Path = folder,
            LastScannedUtc = DateTime.UtcNow,
            IsWatched = true,
            AddedUtc = DateTime.UtcNow,
        });

        // Always emit a final report so the progress bar reaches 100%.
        throttled?.Report(new ScanProgress
        {
            Folder = folder,
            CurrentFile = "",
            TotalFiles = result.TotalFiles,
            Processed = result.TotalFiles,
            Indexed = counters.Indexed,
            Skipped = counters.Skipped,
            Failed = counters.Failed,
        });

        return result;
    }

    /// <summary>
    /// Re-registers Folders rows for directories that contain indexed photos but have no
    /// row yet. Recovers the broken state left by an import that was interrupted before
    /// the folder upsert ran (the pre-fix code only upserted the folder after all photo
    /// batches). Only the shallowest photo-bearing directories are registered, so an
    /// interrupted scan of one root folder is restored as that root (not each sub-folder).
    /// </summary>
    public async Task RepairFolderRecordsAsync(CancellationToken ct = default)
    {
        var registered = (await _db.GetFoldersAsync())
            .Select(f => f.Path.TrimEnd('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool Covered(string dir)
        {
            var current = dir.TrimEnd('\\', '/');
            while (!string.IsNullOrEmpty(current))
            {
                if (registered.Contains(current))
                {
                    return true;
                }
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                {
                    break;
                }
                current = parent.TrimEnd('\\', '/');
            }
            return false;
        }

        bool AncestorOf(string ancestor, string dir) =>
            dir.TrimEnd('\\', '/').StartsWith(ancestor.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);

        var dirs = await _db.GetPhotoDirectoriesAsync();
        var candidates = dirs.Where(d => !Covered(d)).ToList();

        // Keep the topmost candidates only (no candidate that is an ancestor of another).
        var roots = candidates
            .Where(d => !candidates.Any(a => !string.Equals(a, d, StringComparison.OrdinalIgnoreCase) && AncestorOf(a, d)))
            .ToList();

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            await _db.UpsertFolderAsync(new FolderRecord
            {
                Path = root,
                IsWatched = Directory.Exists(root),
                AddedUtc = DateTime.UtcNow,
            });
        }
    }

    /// <summary>Thread-safe counters used by the parallel scan loop.</summary>
    private sealed class ScanCounters
    {
        public int Indexed;
        public int Skipped;
        public int Failed;
    }

    /// <summary>
    /// Reports at most one update per <paramref name="interval"/> (plus always the
    /// final one), so a huge import does not flood the UI thread with progress posts.
    /// </summary>
    private sealed class ThrottledProgress
    {
        private readonly IProgress<ScanProgress> _inner;
        private readonly TimeSpan _interval;
        private readonly object _lock = new();
        private DateTime _lastReport;

        public ThrottledProgress(IProgress<ScanProgress> inner, TimeSpan interval)
        {
            _inner = inner;
            _interval = interval;
            _lastReport = DateTime.UtcNow - interval;
        }

        public void Report(ScanProgress p)
        {
            bool report = false;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (p.Processed >= p.TotalFiles || now - _lastReport >= _interval)
                {
                    _lastReport = now;
                    report = true;
                }
            }
            if (report)
            {
                _inner.Report(p);
            }
        }
    }
}
