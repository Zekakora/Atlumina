using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>A group of photos detected as duplicates / near-duplicates.</summary>
public sealed class DuplicateGroup
{
    public DuplicateGroup() { }
    public DuplicateGroup(List<PhotoRecord> photos) => Photos = photos;

    public List<PhotoRecord> Photos { get; set; } = new();
    public bool IsExact { get; set; }
    /// <summary>Hamming distance of the perceptual hashes (0..64, 0 = identical).</summary>
    public int? PhashDistance { get; set; }
    /// <summary>
    /// Files of the logical photo suggested to keep. All format variants of the same shot
    /// (same folder + base name, e.g. 123.ARW + 123.HIF + 123.JPG) are kept together.
    /// </summary>
    public IReadOnlyList<string> KeepPaths { get; set; } = Array.Empty<string>();
    /// <summary>Primary representative of <see cref="KeepPaths"/> for short display.</summary>
    public string? SuggestedKeepPath { get; set; }
}

/// <summary>A stem-based duplicate group: the same file name found in ≥2 folders.</summary>
public sealed class DedupNameGroup
{
    /// <summary>File name without extension (case-insensitive grouping key).</summary>
    public string Stem { get; set; } = "";

    /// <summary>Every folder that contains a file with this name, plus its format variants.</summary>
    public List<DedupOccurrence> Occurrences { get; set; } = new();

    /// <summary>Paths of the suggested-keep occurrence (all format variants of that folder).</summary>
    public IReadOnlyList<string> KeepPaths { get; set; } = Array.Empty<string>();
}

/// <summary>One folder occurrence of a stem-based duplicate (all format variants in that folder).</summary>
public sealed class DedupOccurrence
{
    public string Directory { get; set; } = "";

    /// <summary>Files with the stem living in <see cref="Directory"/> (distinct extensions).</summary>
    public List<PhotoRecord> Photos { get; set; } = new();

    public bool IsSuggestedKeep { get; set; }

    public string FormatsText => string.Join(" + ", Photos.Select(p => p.Extension.TrimStart('.').ToUpperInvariant()));

    public long TotalBytes => Photos.Sum(p => p.FileSizeBytes);

    public DateTime NewestModifiedUtc => Photos.Max(p => p.FileModifiedUtc);
}

/// <summary>
/// Duplicate detection: exact byte-for-byte hashing (fast, deterministic) plus a
/// perceptual hash (pHash) so near-identical frames in a burst are grouped too.
/// CPU only — phase 4 replaces the visual stage with an NPU embedding.
/// </summary>
public sealed class DuplicateService
{
    /// <summary>pHash distance below which two photos are considered near-duplicates.</summary>
    public const int PhashThreshold = 8;

    private readonly ThumbnailService _thumbs;

    public DuplicateService(ThumbnailService thumbs)
    {
        _thumbs = thumbs;
    }

    /// <summary>
    /// Scans <paramref name="photos"/> for exact duplicates (by SHA-256 of file bytes)
    /// and near-duplicates (by 64-bit pHash). A photo appears in at most one group.
    /// </summary>
    public List<DuplicateGroup> FindDuplicates(IReadOnlyList<PhotoRecord> photos, CancellationToken ct = default)
    {
        // 1) Exact duplicates via content hash.
        var exact = new Dictionary<string, List<PhotoRecord>>(StringComparer.Ordinal);
        foreach (var photo in photos)
        {
            ct.ThrowIfCancellationRequested();
            string? hash = photo.ContentHash;
            if (hash is null)
            {
                hash = ComputeSha256(photo.FilePath);
                photo.ContentHash = hash;
            }
            if (!exact.TryGetValue(hash, out var list))
            {
                exact[hash] = list = new List<PhotoRecord>();
            }
            list.Add(photo);
        }

        var groups = new List<DuplicateGroup>();
        foreach (var (_, list) in exact)
        {
            if (list.Count > 1)
            {
                var (primary, keep) = PickKeep(list);
                groups.Add(new DuplicateGroup(list)
                {
                    IsExact = true,
                    SuggestedKeepPath = primary,
                    KeepPaths = keep,
                });
            }
        }

        // 2) Near-duplicates among the remaining photos (exact groups already claimed).
        var remaining = photos.Where(p => !exact.Values.Any(l => l.Contains(p) && l.Count > 1)).ToList();
        var phashes = new Dictionary<PhotoRecord, ulong>();
        foreach (var photo in remaining)
        {
            ct.ThrowIfCancellationRequested();
            if (photo.PHash is not null && ulong.TryParse(photo.PHash, out var parsed))
            {
                phashes[photo] = parsed;
            }
        }

        var claimed = new HashSet<PhotoRecord>();
        for (int i = 0; i < remaining.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var a = remaining[i];
            if (claimed.Contains(a))
            {
                continue;
            }
            ulong hashA = GetOrComputePhash(a, phashes);
            var group = new List<PhotoRecord> { a };
            for (int j = i + 1; j < remaining.Count; j++)
            {
                var b = remaining[j];
                if (claimed.Contains(b))
                {
                    continue;
                }
                ulong hashB = GetOrComputePhash(b, phashes);
                if (Hamming(hashA, hashB) <= PhashThreshold)
                {
                    group.Add(b);
                }
            }
            if (group.Count > 1)
            {
                foreach (var p in group)
                {
                    claimed.Add(p);
                }
                var (primary, keep) = PickKeep(group);
                groups.Add(new DuplicateGroup(group)
                {
                    IsExact = false,
                    PhashDistance = Hamming(hashA, GetOrComputePhash(group[^1], phashes)),
                    SuggestedKeepPath = primary,
                    KeepPaths = keep,
                });
            }
            else
            {
                claimed.Add(a);
            }
        }

        return groups;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    // ---- name-based grouping (去重工具主分组方式) ----

    /// <summary>
    /// Groups photos by their file name (without extension) across the whole library.
    /// A stem that appears in ≥2 different folders is a duplicate group; each folder is
    /// one "occurrence" (its ARW/HIF/JPG variants count as a single logical photo).
    /// The suggested-keep occurrence wins by: format richness → path organization →
    /// total bytes → newest.
    /// </summary>
    public List<DedupNameGroup> FindNameDuplicates(IReadOnlyList<PhotoRecord> photos)
    {
        var groups = photos
            .Where(p => !p.IsMissing && p.FileName.Length > 0)
            .GroupBy(p => Path.GetFileNameWithoutExtension(p.FileName), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<DedupNameGroup>();
        foreach (var g in groups)
        {
            var occurrences = g
                .GroupBy(p => p.DirectoryPath)
                .Select(dir => new DedupOccurrence { Directory = dir.Key, Photos = dir.ToList() })
                .OrderBy(o => o.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Only cross-folder duplicates are interesting (same-dir format variants are 格式清理's job).
            if (occurrences.Count < 2)
            {
                continue;
            }

            var best = occurrences
                .OrderByDescending(o => o.Photos.Count)        // 格式最丰富（ARW+HIF+JPG > JPG）
                .ThenByDescending(o => OrganizationScore(o.Directory))  // 路径最正常有序（日期归类夹 > sub/temp）
                .ThenByDescending(o => o.TotalBytes)           // 更接近原始文件
                .ThenByDescending(o => o.NewestModifiedUtc)
                .First();
            best.IsSuggestedKeep = true;

            result.Add(new DedupNameGroup
            {
                Stem = g.Key,
                Occurrences = occurrences,
                KeepPaths = best.Photos.Select(p => p.FilePath).ToList(),
            });
        }

        return result.OrderBy(g => g.Stem, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Matches date-like folder segments: 2026, 2026-01, 2026-01-01, 20260101, 2026年01月 …</summary>
    private static readonly Regex DateFolderRegex = new(
        @"^(\d{4}[-_年./]\d{1,2}[-_月./]\d{1,2}日?|\d{4}[-_年./]\d{1,2}日?|\d{8}|\d{6}|\d{4})$",
        RegexOptions.Compiled);

    /// <summary>Folder names that look like ad-hoc buckets rather than an organized library.</summary>
    private static readonly HashSet<string> ClutterFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "temp", "tmp", "sub", "backup", "backups", "old", "new", "copy", "copies",
        "export", "exports", "download", "downloads", "unsorted", "misc", "junk",
        "trash", "recycle", "import", "imported", "临时", "备份", "复制", "导出",
        "新建", "副本", "旧", "测试", "拷贝", "下载", "未整理", "散图", "杂图",
    };

    /// <summary>
    /// Scores how "organized" a directory looks: date-categorized folders score high,
    /// ad-hoc buckets (temp/sub/…) score negative; shared root prefixes cancel out.
    /// </summary>
    private static int OrganizationScore(string directory)
    {
        int score = 0;
        foreach (var segment in directory.Split('\\', '/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }
            if (DateFolderRegex.IsMatch(segment))
            {
                score += 3;
            }
            else if (ClutterFolders.Contains(segment))
            {
                score -= 3;
            }
            else if (segment.Length >= 2 && !segment.EndsWith(':'))
            {
                score += 1;
            }
        }
        return score;
    }

    private ulong GetOrComputePhash(PhotoRecord photo, Dictionary<PhotoRecord, ulong> cache)
    {
        if (cache.TryGetValue(photo, out var value))
        {
            return value;
        }
        ulong hash = ComputePhash(photo);
        cache[photo] = hash;
        photo.PHash = hash.ToString();
        return hash;
    }

    /// <summary>
    /// Classic 64-bit pHash: downscale to 32×32 grayscale, compute the 8×8 DCT (only the
    /// low frequencies), then compare the 64 DCT coefficients against their median.
    /// </summary>
    public static ulong ComputePhash(PhotoRecord photo)
    {
        const int size = 32;
        double[]? gray = GetGrayscalePixels(photo.FilePath, size, size);
        if (gray is null)
        {
            return 0;
        }

        var dct = Dct2D(gray, size, size);
        var low = new double[64];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                low[y * 8 + x] = dct[y * size + x];
            }
        }

        var sorted = (double[])low.Clone();
        Array.Sort(sorted);
        double median = sorted[32]; // 64 values → average of 32nd/33rd = index 32 approximates

        ulong hash = 0;
        for (int i = 0; i < 64; i++)
        {
            if (low[i] > median)
            {
                hash |= 1UL << i;
            }
        }
        return hash;
    }

    /// <summary>Reads grayscale (luma) 32×32 pixels via WIC. Returns null on decode failure.</summary>
    private static double[]? GetGrayscalePixels(string filePath, int width, int height)
    {
        var gray = WicGrayscale.GetGrayscaleFixed(filePath, width, height);
        return gray?.Pixels;
    }

    private static double[] Dct2D(double[] input, int rows, int cols)
    {
        double[] output = new double[input.Length];
        double[] rowsDct = new double[input.Length];

        // 1D DCT along rows.
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                double sum = 0;
                for (int n = 0; n < cols; n++)
                {
                    sum += input[r * cols + n] * Math.Cos(Math.PI * c * (2 * n + 1) / (2 * cols));
                }
                rowsDct[r * cols + c] = sum;
            }
        }
        // 1D DCT along columns.
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                double sum = 0;
                for (int n = 0; n < rows; n++)
                {
                    sum += rowsDct[n * cols + c] * Math.Cos(Math.PI * r * (2 * n + 1) / (2 * rows));
                }
                output[r * cols + c] = sum;
            }
        }
        return output;
    }

    private static int Hamming(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    /// <summary>
    /// Picks the logical photo to keep. Files sharing the same folder + base name (e.g. the
    /// ARW/HIF/JPG of one shot) form one logical photo and are always kept together; the best
    /// logical photo wins by resolution, then rating, then newest.
    /// </summary>
    private static (string Primary, List<string> Keep) PickKeep(IReadOnlyList<PhotoRecord> photos)
    {
        var unit = photos
            .GroupBy(p => (Dir: Path.GetDirectoryName(p.FilePath) ?? "", Stem: Path.GetFileNameWithoutExtension(p.FileName)))
            .OrderByDescending(u => u.Max(p => (p.Width ?? 0) * (long)(p.Height ?? 0)))
            .ThenByDescending(u => u.Max(p => p.Rating))
            .ThenByDescending(u => u.Max(p => p.TakenAtUtc ?? p.FileModifiedUtc))
            .First();

        var keep = unit
            .OrderByDescending(p => (p.Width ?? 0) * (long)(p.Height ?? 0))
            .ThenByDescending(p => p.Rating)
            .ThenByDescending(p => p.TakenAtUtc ?? p.FileModifiedUtc)
            .Select(p => p.FilePath)
            .ToList();

        return (keep[0], keep);
    }
}
