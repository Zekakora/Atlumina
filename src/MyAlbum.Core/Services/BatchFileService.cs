using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>Result of renaming a single file.</summary>
public sealed record RenameResult(string OldPath, string NewPath, bool Success, string? Message);

/// <summary>Result of exporting a single photo.</summary>
public sealed record ExportResult(string SourcePath, string? DestinationPath, bool Success, string? Message);

/// <summary>
/// Batch file operations on the library: template-based renaming (with collision
/// avoidance and the index kept in sync) and exporting to a target folder.
/// </summary>
public sealed class BatchFileService
{
    private readonly PhotoDatabase _db;

    public BatchFileService(PhotoDatabase db)
    {
        _db = db;
    }

    /// <summary>
    /// Builds a target file name from a template. Supported tokens:
    /// {date}, {time}, {year}, {month}, {day}, {camera}, {ext}, {index:N}, {name}.
    /// </summary>
    public static string BuildName(string template, PhotoRecord photo, int index)
    {
        var date = photo.TakenAtUtc ?? photo.FileModifiedUtc;
        var ext = Path.GetExtension(photo.FilePath);
        var camera = string.IsNullOrWhiteSpace(photo.CameraModel) ? "Unknown" : photo.CameraModel;

        string result = template
            .Replace("{date}", date.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", date.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{year}", date.ToString("yyyy"), StringComparison.OrdinalIgnoreCase)
            .Replace("{month}", date.ToString("MM"), StringComparison.OrdinalIgnoreCase)
            .Replace("{day}", date.ToString("dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{camera}", SanitizeFileName(camera), StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", SanitizeFileName(Path.GetFileNameWithoutExtension(photo.FilePath)), StringComparison.OrdinalIgnoreCase)
            .Replace("{index}", index.ToString("D2"), StringComparison.OrdinalIgnoreCase)
            .Replace("{ext}", ext.TrimStart('.'), StringComparison.OrdinalIgnoreCase);

        if (!Path.HasExtension(result))
        {
            result += ext;
        }
        return SanitizeFileName(result);
    }

    /// <summary>
    /// Renames <paramref name="photos"/> to <paramref name="template"/> inside their own
    /// directories, updating both the file system and the index. Conflicts are resolved
    /// by appending a numeric suffix.
    /// </summary>
    public async Task<IReadOnlyList<RenameResult>> RenameBatchAsync(
        IReadOnlyList<PhotoRecord> photos,
        string template,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<RenameResult>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (OriginalDataProtection.IsEnabled)
        {
            foreach (var photo in photos)
            {
                results.Add(new RenameResult(photo.FilePath, photo.FilePath, false, OriginalDataProtection.BlockedMessage));
            }
            return results;
        }

        for (int i = 0; i < photos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var photo = photos[i];
            var dir = Path.GetDirectoryName(photo.FilePath) ?? "";
            string candidate = Path.Combine(dir, BuildName(template, photo, i));
            candidate = MakeUnique(candidate, usedNames);

            if (string.Equals(candidate, photo.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                usedNames.Add(photo.FilePath);
                results.Add(new RenameResult(photo.FilePath, photo.FilePath, true, "未变"));
                continue;
            }

            try
            {
                File.Move(photo.FilePath, candidate);
                usedNames.Add(candidate);

                await _db.RenamePhotoPathAsync(photo.FilePath, candidate);
                photo.FilePath = candidate;
                photo.FileName = Path.GetFileName(candidate);
                photo.DirectoryPath = Path.GetDirectoryName(candidate) ?? "";

                results.Add(new RenameResult(photo.FilePath, candidate, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new RenameResult(photo.FilePath, photo.FilePath, false, ex.Message));
            }
            progress?.Report((i + 1, photos.Count, photo.FilePath));
        }
        return results;
    }

    /// <summary>
    /// Copies <paramref name="photos"/> into <paramref name="targetFolder"/>, flattening
    /// the source layout with unique names. The index is left untouched (the copies are
    /// new files; a later scan indexes them if the folder is imported).
    /// </summary>
    public async Task<IReadOnlyList<ExportResult>> ExportBatchAsync(
        IReadOnlyList<PhotoRecord> photos,
        string targetFolder,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetFolder);
        var results = new List<ExportResult>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < photos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var photo = photos[i];
            var target = MakeUnique(Path.Combine(targetFolder, photo.FileName), usedNames);
            try
            {
                await CopyAsync(photo.FilePath, target, ct);
                usedNames.Add(target);
                results.Add(new ExportResult(photo.FilePath, target, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new ExportResult(photo.FilePath, null, false, ex.Message));
            }
            progress?.Report((i + 1, photos.Count, photo.FilePath));
        }
        return results;
    }

    private static async Task CopyAsync(string source, string target, CancellationToken ct)
    {
        await using var src = File.OpenRead(source);
        await using var dst = File.Create(target);
        await src.CopyToAsync(dst, ct);
    }

    private static string MakeUnique(string candidate, HashSet<string> used)
    {
        if (!File.Exists(candidate) && used.Add(candidate))
        {
            return candidate;
        }
        string dir = Path.GetDirectoryName(candidate) ?? "";
        string name = Path.GetFileNameWithoutExtension(candidate);
        string ext = Path.GetExtension(candidate);
        for (int i = 1; ; i++)
        {
            var next = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(next) && used.Add(next))
            {
                return next;
            }
        }
    }

    /// <summary>Strips characters that are invalid in Windows file names.</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString();
    }
}
