using System.Diagnostics;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>Metadata changes to apply to one file (fields to leave unchanged are null).</summary>
public sealed class ExifEditOptions
{
    public string FilePath { get; set; } = "";
    public DateTime? TakenAtUtc { get; set; }
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public int? Rating { get; set; }
    public double? GpsLatitude { get; set; }
    public double? GpsLongitude { get; set; }
    public double? GpsAltitude { get; set; }
    public bool ClearGps { get; set; }
}

/// <summary>Outcome of writing metadata to a single file.</summary>
public sealed class ExifEditResult
{
    public string FilePath { get; set; } = "";
    public bool Success { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Writes EXIF metadata via the ExifTool CLI. The process is spawned hidden and
/// argument-listed (never a shell string), so RAW and regular formats are edited
/// without a console window or injection risk. One short-lived process per file
/// keeps RAW memory usage bounded and lets batches scale across cores.
/// </summary>
public sealed class ExifWriterService
{
    public const int MaxParallelism = 4;

    private static readonly string[] CandidateNames =
        ["exiftool.exe", "exiftool", "exiftool(-k).exe"];

    private readonly string? _explicitPath;

    /// <summary>Set this to override auto-detection (settings UI). Null means "auto".</summary>
    public string? ExplicitPath
    {
        get => _explicitPath;
        init => _explicitPath = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private string? _cachedPath;
    private readonly object _cacheLock = new();

    public ExifWriterService(string? explicitPath = null)
    {
        _explicitPath = string.IsNullOrWhiteSpace(explicitPath) ? null : explicitPath;
    }

    /// <summary>
    /// Locates exiftool.exe: explicit path first, then %LOCALAPPDATA%\Atlumina\tools
    /// (a place the app can download/copy it into), then PATH. Returns null if absent.
    /// </summary>
    public string? FindExifTool()
    {
        lock (_cacheLock)
        {
            if (_cachedPath is not null)
            {
                return _cachedPath;
            }
            _cachedPath = Locate();
            return _cachedPath;
        }
    }

    private string? Locate()
    {
        if (_explicitPath is { Length: > 0 } && File.Exists(_explicitPath))
        {
            return _explicitPath;
        }

        var toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Atlumina", "tools");
        foreach (var name in CandidateNames)
        {
            var candidate = Path.Combine(toolsDir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var name in CandidateNames)
        {
            var fromPath = FindOnPath(name);
            if (fromPath is not null)
            {
                return fromPath;
            }
        }
        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // unreadable PATH entry
            }
        }
        return null;
    }

    /// <summary>True when an exiftool executable is available.</summary>
    public bool IsAvailable => FindExifTool() is not null;

    /// <summary>Forgets the cached location so the next call re-probes (call after installing exiftool).</summary>
    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedPath = null;
        }
    }

    /// <summary>Recommended download location for exiftool.exe (user-facing help text).</summary>
    public static string SuggestedInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Atlumina", "tools");

    /// <summary>
    /// Applies <paramref name="edits"/> to each file. Runs several exiftool processes
    /// in parallel and re-reads nothing here — the caller decides how to refresh its index.
    /// When <paramref name="keepOriginalBackup"/> is true, exiftool keeps a
    /// "&lt;name&gt;.original" backup of each rewritten file (via -overwrite_original)
    /// instead of overwriting in place with no backup.
    /// </summary>
    public async Task<IReadOnlyList<ExifEditResult>> WriteBatchAsync(
        IReadOnlyList<ExifEditOptions> edits,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default,
        bool keepOriginalBackup = false)
    {
        if (OriginalDataProtection.IsEnabled)
        {
            return edits.Select(e => new ExifEditResult
            {
                FilePath = e.FilePath,
                Success = false,
                Message = OriginalDataProtection.BlockedMessage,
            }).ToList();
        }

        var tool = FindExifTool();
        if (tool is null)
        {
            return edits.Select(e => new ExifEditResult
            {
                FilePath = e.FilePath,
                Success = false,
                Message = "未找到 ExifTool。请将其安装到 " + SuggestedInstallDir + "（文件名为 exiftool.exe）后重试。",
            }).ToList();
        }

        var results = new ExifEditResult[edits.Count];
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Min(MaxParallelism, Math.Max(1, Environment.ProcessorCount)),
        };

        await Parallel.ForAsync(0, edits.Count, options, async (i, token) =>
        {
            token.ThrowIfCancellationRequested();
            results[i] = await WriteOneAsync(tool, edits[i], token, keepOriginalBackup);
            progress?.Report((i + 1, edits.Count, edits[i].FilePath));
        });

        return results;
    }

    private static async Task<ExifEditResult> WriteOneAsync(string tool, ExifEditOptions edit, CancellationToken ct, bool keepOriginalBackup)
    {
        var args = BuildArgs(edit, keepOriginalBackup);
        var psi = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new ExifEditResult { FilePath = edit.FilePath, Success = false, Message = "进程启动失败" };
            }
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            string output = (await stdout).Trim() + (await stderr).Trim();

            bool success = process.ExitCode == 0 && !output.Contains("Error", StringComparison.OrdinalIgnoreCase);
            return new ExifEditResult
            {
                FilePath = edit.FilePath,
                Success = success,
                Message = success ? "OK" : (string.IsNullOrWhiteSpace(output) ? $"退出码 {process.ExitCode}" : output[..Math.Min(400, output.Length)]),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExifEditResult { FilePath = edit.FilePath, Success = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// Builds the exiftool argument list. Values that look like "tag=value" are passed
    /// as single argument-list entries so no quoting/escaping risk exists.
    /// </summary>
    public static List<string> BuildArgs(ExifEditOptions edit, bool keepOriginalBackup = false)
    {
        var args = new List<string>
        {
            "-m",              // ignore minor errors
        };
        // 保留 .original 备份（默认 exiftool 行为），比 _in_place 安全：写坏可回退。
        args.Add(keepOriginalBackup ? "-overwrite_original" : "-overwrite_original_in_place");

        if (edit.TakenAtUtc is { } takenAt)
        {
            string stamp = takenAt.ToLocalTime().ToString("yyyy:MM:dd HH:mm:ss");
            args.Add($"-DateTimeOriginal={stamp}");
            args.Add($"-CreateDate={stamp}");
            args.Add($"-ModifyDate={stamp}");
        }
        if (edit.CameraMake is { } make)
        {
            args.Add($"-Make={make}");
        }
        if (edit.CameraModel is { } model)
        {
            args.Add($"-Model={model}");
        }
        if (edit.Rating is { } rating)
        {
            args.Add($"-Rating={rating}");
            args.Add($"-XMP:Rating={rating}");
        }
        if (edit.ClearGps)
        {
            args.Add("-GPSLatitude=");
            args.Add("-GPSLongitude=");
            args.Add("-GPSAltitude=");
        }
        else
        {
            if (edit.GpsLatitude is { } lat)
            {
                args.Add($"-GPSLatitude={lat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (edit.GpsLongitude is { } lon)
            {
                args.Add($"-GPSLongitude={lon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}");
            }
            if (edit.GpsAltitude is { } alt)
            {
                args.Add($"-GPSAltitude={alt.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
            }
        }

        args.Add(edit.FilePath);
        return args;
    }
}
