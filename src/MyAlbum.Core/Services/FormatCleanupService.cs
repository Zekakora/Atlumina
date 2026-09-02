using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>Outcome of deleting the non-kept formats of a logical photo group.</summary>
public sealed class FormatCleanupResult
{
    /// <summary>Files successfully sent to the recycle bin.</summary>
    public int DeletedCount { get; set; }
    /// <summary>Files that could not be deleted (locked, missing, …).</summary>
    public int FailedCount { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Format cleanup: files that share the same folder + base name (e.g. 123.ARW + 123.HIF +
/// 123.JPG of one shot) are treated as a single logical photo. The user picks which formats
/// to keep; the rest are sent to the recycle bin and removed from the library index.
/// </summary>
public sealed class FormatCleanupService
{
    private readonly PhotoDatabase _db;

    public FormatCleanupService(PhotoDatabase db)
    {
        _db = db;
    }

    /// <summary>
    /// Groups photos into logical photos by (directory, base name). Only groups with at
    /// least two members are returned, ordered newest first by shooting date.
    /// </summary>
    public List<List<PhotoRecord>> GroupByPhoto(IReadOnlyList<PhotoRecord> photos)
    {
        return photos
            .Where(p => !p.IsMissing && p.FileName.Length > 0)
            .GroupBy(p => (Dir: p.DirectoryPath, Stem: Path.GetFileNameWithoutExtension(p.FileName)))
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Max(p => p.TakenAtUtc ?? p.FileModifiedUtc))
            .Select(g => g.ToList())
            .ToList();
    }

    /// <summary>
    /// Deletes every member of <paramref name="group"/> whose extension is not in
    /// <paramref name="keepExtensions"/> (case-insensitive, without the leading dot, e.g.
    /// "ARW"). Deleted files go to the recycle bin; their DB rows are removed too.
    /// </summary>
    public Task<FormatCleanupResult> DeleteNonKeptAsync(
        IReadOnlyList<PhotoRecord> group,
        ISet<string> keepExtensions,
        CancellationToken ct = default)
    {
        var toDelete = group
            .Where(p => !keepExtensions.Contains(p.Extension.TrimStart('.').ToUpperInvariant()))
            .ToList();
        return DeletePhotosAsync(toDelete, ct);
    }

    /// <summary>
    /// Sends the given photos' source files to the recycle bin, removes their thumbnail
    /// cache files and deletes their DB rows (PhotoTags cascade). Used by 去重检测 and
    /// 格式清理 to discard the non-kept files.
    /// </summary>
    public async Task<FormatCleanupResult> DeletePhotosAsync(
        IReadOnlyList<PhotoRecord> photos,
        CancellationToken ct = default)
    {
        var result = new FormatCleanupResult();
        if (OriginalDataProtection.IsEnabled)
        {
            foreach (var photo in photos)
            {
                result.FailedCount++;
                result.Errors.Add($"{photo.FileName}: {OriginalDataProtection.BlockedMessage}");
            }
            return result;
        }

        foreach (var photo in photos)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                DeleteToRecycleBin(photo.FilePath);
                if (photo.ThumbnailCachePath is not null && File.Exists(photo.ThumbnailCachePath))
                {
                    try
                    {
                        File.Delete(photo.ThumbnailCachePath);
                    }
                    catch (Exception)
                    {
                        // Orphan cache files are harmless; never fail the cleanup for them.
                    }
                }
                await _db.DeletePhotoAsync(photo.FilePath);
                result.DeletedCount++;
            }
            catch (Exception e)
            {
                result.FailedCount++;
                result.Errors.Add($"{photo.FileName}: {e.Message}");
            }
        }
        return result;
    }

    private static void DeleteToRecycleBin(string path)
    {
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }
}
