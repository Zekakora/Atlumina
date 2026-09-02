using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>
/// Compares each photo's EXIF shooting date against its Windows file creation time,
/// then (optionally) rewrites the file-system timestamps to match the shooting date.
/// Only file timestamps are modified — EXIF / file content is never touched.
/// </summary>
public sealed class PhotoDateFixService
{
    /// <summary>Treat creation time as a match when it is within this many seconds of the shooting date.</summary>
    private const double ToleranceSeconds = 2;

    public static bool IsImageFile(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".webp" or ".bmp" or ".gif"
                or ".heic" or ".heif" or ".hif" or ".avif" or ".avifs"
                or ".arw" or ".dng" or ".nef" or ".cr2" or ".cr3" or ".raf" or ".srw"
                or ".orf" or ".rw2" or ".pef" or ".raw" => true,
            _ => false,
        };
    }

    /// <summary>
    /// Compares creation time vs shooting date for each photo. The shooting date comes
    /// from the library index (<see cref="PhotoRecord.TakenAtUtc"/>, already parsed from
    /// EXIF at import time) so no file re-parsing is needed.
    /// </summary>
    public PhotoDateScanResult Scan(IReadOnlyList<PhotoRecord> photos)
    {
        var result = new PhotoDateScanResult();
        foreach (var photo in photos)
        {
            if (photo.TakenAtUtc is not DateTime taken)
            {
                result.Items.Add(new PhotoDateCheckItem
                {
                    FilePath = photo.FilePath,
                    DirectoryPath = photo.DirectoryPath,
                    Status = PhotoDateStatus.NoExif,
                });
                result.NoExifCount++;
                continue;
            }

            DateTime created;
            try
            {
                created = File.GetCreationTime(photo.FilePath);
            }
            catch (Exception)
            {
                result.Items.Add(new PhotoDateCheckItem
                {
                    FilePath = photo.FilePath,
                    DirectoryPath = photo.DirectoryPath,
                    Status = PhotoDateStatus.NoExif,
                });
                result.NoExifCount++;
                continue;
            }

            if (Math.Abs((taken - created).TotalSeconds) < ToleranceSeconds)
            {
                result.Items.Add(new PhotoDateCheckItem
                {
                    FilePath = photo.FilePath,
                    DirectoryPath = photo.DirectoryPath,
                    Status = PhotoDateStatus.Match,
                    TakenAt = taken,
                    CreatedAt = created,
                });
                result.MatchedCount++;
            }
            else
            {
                var mismatch = new PhotoDateMismatch
                {
                    FilePath = photo.FilePath,
                    DirectoryPath = photo.DirectoryPath,
                    TakenAt = taken,
                    CreatedAt = created,
                };
                result.Items.Add(new PhotoDateCheckItem
                {
                    FilePath = photo.FilePath,
                    DirectoryPath = photo.DirectoryPath,
                    Status = PhotoDateStatus.Mismatch,
                    TakenAt = taken,
                    CreatedAt = created,
                    Mismatch = mismatch,
                });
                result.MismatchCount++;
            }
        }
        return result;
    }

    /// <summary>
    /// Sets the creation time of each file to its EXIF shooting date via
    /// <c>File.SetCreationTimeUtc</c> (which calls Win32 SetFileTime internally).
    /// Optionally also sets the last-write time. File content stays untouched.
    /// </summary>
    public PhotoDateFixResult Fix(IReadOnlyList<PhotoDateMismatch> mismatches, bool alsoModified)
    {
        var result = new PhotoDateFixResult();
        if (OriginalDataProtection.IsEnabled)
        {
            foreach (var m in mismatches)
            {
                result.Failed++;
                result.Errors.Add($"{m.FilePath}: {OriginalDataProtection.BlockedMessage}");
            }
            return result;
        }

        foreach (var m in mismatches)
        {
            try
            {
                // The EXIF shooting date is a wall-clock local time; convert it to the
                // UTC instant that File.SetCreationTimeUtc expects.
                var utc = DateTime.SpecifyKind(m.TakenAt, DateTimeKind.Local).ToUniversalTime();
                File.SetCreationTimeUtc(m.FilePath, utc);
                if (alsoModified)
                {
                    File.SetLastWriteTimeUtc(m.FilePath, utc);
                }
                result.Ok++;
            }
            catch (Exception e)
            {
                result.Failed++;
                result.Errors.Add($"{m.FilePath}: {e.Message}");
            }
        }
        return result;
    }
}
