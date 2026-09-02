using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

public enum DiffStatus
{
    /// <summary>File matches the DB index.</summary>
    Match,
    /// <summary>File exists but differs from the index (size / modified time / EXIF …).</summary>
    Differs,
    /// <summary>The file is gone or unreadable while a DB row still exists.</summary>
    FileMissing,
}

/// <summary>One photo compared against its source file.</summary>
public sealed class PhotoDiffItem
{
    /// <summary>The row currently stored in the SQLite index.</summary>
    public required PhotoRecord Db { get; init; }

    /// <summary>Fresh metadata read from the file (null when the file is missing/unreadable).</summary>
    public PhotoRecord? File { get; set; }

    public DiffStatus Status { get; set; }

    /// <summary>Human-readable labels of the fields that differ ("大小 · 修改时间 · 拍摄时间").</summary>
    public List<string> ChangedFields { get; } = new();

    /// <summary>Actual last-write time of the file (null when missing).</summary>
    public DateTime? ActualModifiedUtc { get; set; }

    /// <summary>Actual file size in bytes (null when missing).</summary>
    public long? ActualSizeBytes { get; set; }
}

public sealed class DbFileScanResult
{
    public int Total { get; set; }
    public int Matched { get; set; }
    public int Differs { get; set; }
    public int Missing { get; set; }
    public List<PhotoDiffItem> Items { get; } = new();
}

/// <summary>Outcome of writing the DB's data back into the photo files.</summary>
public sealed class DbFileWriteResult
{
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Compares the SQLite index against the original photo files and lets the caller sync
/// either direction: overwrite the database from the files (metadata only, DB-side user
/// data like rating/tags/thumbnails preserved), or write the DB's data (shooting time /
/// rating / GPS) into the files via ExifTool. The file→DB direction never touches the
/// source files; the DB→file direction is blocked while <see cref="OriginalDataProtection"/>
/// is enabled.
/// </summary>
public sealed class DbFileDiffService
{
    private const double MtimeToleranceSeconds = 2;

    private readonly PhotoDatabase _db;
    private readonly MetadataReaderService _reader;
    private readonly ExifWriterService _exif;
    private readonly LibraryService _library;

    public DbFileDiffService(
        PhotoDatabase db,
        MetadataReaderService reader,
        ExifWriterService exif,
        LibraryService library)
    {
        _db = db;
        _reader = reader;
        _exif = exif;
        _library = library;
    }

    /// <summary>
    /// Scans every DB row against its file. Files whose size and last-write time are
    /// unchanged are treated as a match without re-parsing EXIF (the content is identical,
    /// so the metadata cannot have changed). Only differing files get a full metadata read.
    /// </summary>
    public async Task<DbFileScanResult> ScanAsync(
        IReadOnlyList<PhotoRecord> dbPhotos,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new DbFileScanResult { Total = dbPhotos.Count };
        var items = new PhotoDiffItem?[dbPhotos.Count];
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
        };

        await Parallel.ForAsync(0, dbPhotos.Count, options, (i, token) =>
        {
            token.ThrowIfCancellationRequested();
            items[i] = ScanOne(dbPhotos[i]);
            if (progress is not null && (i % 25 == 0 || i == dbPhotos.Count - 1))
            {
                progress.Report((i + 1, dbPhotos.Count));
            }
            return ValueTask.CompletedTask;
        });

        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }
            result.Items.Add(item);
            switch (item.Status)
            {
                case DiffStatus.Match: result.Matched++; break;
                case DiffStatus.Differs: result.Differs++; break;
                default: result.Missing++; break;
            }
        }
        return result;
    }

    private PhotoDiffItem ScanOne(PhotoRecord db)
    {
        var item = new PhotoDiffItem { Db = db };
        try
        {
            var fi = new FileInfo(db.FilePath);
            if (!fi.Exists)
            {
                item.Status = DiffStatus.FileMissing;
                item.ChangedFields.Add("文件缺失");
                return item;
            }

            item.ActualModifiedUtc = fi.LastWriteTimeUtc;
            item.ActualSizeBytes = fi.Length;

            bool fastMatch = db.FileSizeBytes == fi.Length
                && Math.Abs((db.FileModifiedUtc - fi.LastWriteTimeUtc).TotalSeconds) < MtimeToleranceSeconds;
            if (fastMatch)
            {
                item.Status = DiffStatus.Match;
                return item;
            }

            var fresh = _reader.Read(db.FilePath);
            item.File = fresh;
            CompareFields(item, db, fresh);
            item.Status = item.ChangedFields.Count > 0 ? DiffStatus.Differs : DiffStatus.Match;
        }
        catch
        {
            // locked / unreadable file: cannot compare or write
            item.Status = DiffStatus.FileMissing;
            item.ChangedFields.Add("无法读取");
        }
        return item;
    }

    private static void CompareFields(PhotoDiffItem item, PhotoRecord db, PhotoRecord fresh)
    {
        if (db.FileSizeBytes != fresh.FileSizeBytes)
        {
            item.ChangedFields.Add("大小");
        }
        if (Math.Abs((db.FileModifiedUtc - fresh.FileModifiedUtc).TotalSeconds) >= MtimeToleranceSeconds)
        {
            item.ChangedFields.Add("修改时间");
        }
        if (!TimesEqual(db.TakenAtUtc, fresh.TakenAtUtc))
        {
            item.ChangedFields.Add("拍摄时间");
        }
        if (!StringsEqual(db.CameraMake, fresh.CameraMake) || !StringsEqual(db.CameraModel, fresh.CameraModel))
        {
            item.ChangedFields.Add("相机");
        }
        if (db.Width != fresh.Width || db.Height != fresh.Height)
        {
            item.ChangedFields.Add("尺寸");
        }
    }

    /// <summary>
    /// Overwrites the DB rows from the freshly read file metadata. The DB-side user data
    /// (rating, tags, thumbnail cache path) is preserved — only file-derived fields change.
    /// </summary>
    public async Task<int> OverwriteDatabaseFromFilesAsync(
        IReadOnlyList<PhotoDiffItem> items,
        CancellationToken ct = default)
    {
        int ok = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.File is not { } fresh)
            {
                continue;
            }
            fresh.Id = item.Db.Id;
            fresh.Rating = item.Db.Rating;
            fresh.ThumbnailCachePath = item.Db.ThumbnailCachePath;
            fresh.IsMissing = false;
            await _db.UpsertPhotoAsync(fresh);
            ok++;
        }
        return ok;
    }

    /// <summary>
    /// Writes the DB's data (shooting time / rating / GPS) into the photo files via
    /// ExifTool, keeping a ".original" backup. Afterwards the DB rows are refreshed from
    /// the rewritten files so the index matches again. Blocked when the protection switch
    /// is on or ExifTool is missing.
    /// </summary>
    public async Task<DbFileWriteResult> WriteDatabaseToFilesAsync(
        IReadOnlyList<PhotoDiffItem> items,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        var result = new DbFileWriteResult();
        if (OriginalDataProtection.IsEnabled)
        {
            result.Failed = items.Count;
            result.Errors.Add(OriginalDataProtection.BlockedMessage);
            return result;
        }
        if (!_exif.IsAvailable)
        {
            result.Failed = items.Count;
            result.Errors.Add("未找到 ExifTool。请将其安装到 " + ExifWriterService.SuggestedInstallDir + " 后重试。");
            return result;
        }

        var edits = new List<ExifEditOptions>();
        foreach (var item in items)
        {
            if (item.File is null)
            {
                continue;
            }
            var db = item.Db;
            var edit = new ExifEditOptions { FilePath = db.FilePath };
            if (db.TakenAtUtc is { } taken)
            {
                edit.TakenAtUtc = taken;
            }
            if (db.Rating > 0)
            {
                edit.Rating = db.Rating;
            }
            if (db.GpsLatitude is { } lat && db.GpsLongitude is { } lon)
            {
                edit.GpsLatitude = lat;
                edit.GpsLongitude = lon;
                edit.GpsAltitude = db.GpsAltitude;
            }
            edits.Add(edit);
        }
        if (edits.Count == 0)
        {
            return result;
        }

        var results = await _exif.WriteBatchAsync(edits, progress, ct, keepOriginalBackup: true);
        foreach (var r in results)
        {
            if (r.Success)
            {
                result.Succeeded++;
                await _library.RefreshMetadataAsync(r.FilePath, ct);
            }
            else
            {
                result.Failed++;
                result.Errors.Add($"{Path.GetFileName(r.FilePath)}: {r.Message}");
            }
        }
        return result;
    }

    private static bool TimesEqual(DateTime? a, DateTime? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return Math.Abs((a.Value - b.Value).TotalSeconds) < MtimeToleranceSeconds;
    }

    private static bool StringsEqual(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a)
            ? string.IsNullOrWhiteSpace(b)
            : string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
