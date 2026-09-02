using System.IO.Compression;
using System.Net.Http;

namespace MyAlbum.Core.Services;

/// <summary>
/// Downloads and installs the official Windows ExifTool executable into
/// %LOCALAPPDATA%\Atlumina\tools (the same directory ExifWriterService probes).
/// The Windows ZIP contains "exiftool(-k).exe" and an "exiftool_files" helper
/// folder; the exe is renamed to exiftool.exe and both land in the tools dir.
/// </summary>
public sealed class ExifToolInstallerService
{
    private const string VersionUrl = "https://exiftool.org/ver.txt";
    private const string DownloadTemplate = "https://sourceforge.net/projects/exiftool/files/exiftool-{0}_64.zip/download";
    private const string FallbackVersion = "13.59";

    private readonly ExifWriterService _exifWriter;

    public ExifToolInstallerService(ExifWriterService exifWriter) => _exifWriter = exifWriter;

    public static string ToolsDirectory => ExifWriterService.SuggestedInstallDir;

    /// <summary>
    /// Downloads the latest Windows 64-bit ExifTool package and installs it into
    /// the tools directory. Reports (fraction 0..1, status text) progress.
    /// </summary>
    public async Task DownloadAndInstallAsync(
        IProgress<(double Fraction, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        var version = await GetLatestVersionAsync(ct) ?? FallbackVersion;
        var url = string.Format(DownloadTemplate, version);
        var zipPath = Path.Combine(Path.GetTempPath(), $"exiftool-{version}_64.zip");

        try
        {
            progress?.Report((0, $"正在下载 ExifTool {version}…"));

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Atlumina/1.0");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                long downloaded = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;
                    double fraction = total is > 0 ? (double)downloaded / total.Value : 0;
                    progress?.Report((fraction, $"正在下载 ExifTool {version}… {downloaded / 1024 / 1024} MB"));
                }
            }

            progress?.Report((0.95, $"正在解压安装 {version}…"));
            await Task.Run(() => ExtractAndInstall(zipPath, version), ct);

            _exifWriter.InvalidateCache();
            progress?.Report((1, "安装完成。"));
        }
        finally
        {
            try
            {
                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private void ExtractAndInstall(string zipPath, string version)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var tempDir = Path.Combine(ToolsDirectory, $".install-{version}");
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, true);
        }
        Directory.CreateDirectory(tempDir);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDir);

            var exe = Path.Combine(tempDir, "exiftool(-k).exe");
            if (!File.Exists(exe))
            {
                // Some packages ship it pre-renamed.
                exe = Path.Combine(tempDir, "exiftool.exe");
            }
            if (!File.Exists(exe))
            {
                throw new InvalidOperationException("下载包中未找到 exiftool 可执行文件。");
            }

            var target = Path.Combine(ToolsDirectory, "exiftool.exe");
            File.Copy(exe, target, overwrite: true);

            // exiftool(-k).exe requires the "exiftool_files" helper folder in the same directory.
            var filesSrc = Path.Combine(tempDir, "exiftool_files");
            var filesDst = Path.Combine(ToolsDirectory, "exiftool_files");
            if (Directory.Exists(filesSrc))
            {
                if (Directory.Exists(filesDst))
                {
                    Directory.Delete(filesDst, true);
                }
                Directory.Move(filesSrc, filesDst);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private static async Task<string?> GetLatestVersionAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Atlumina/1.0");
            var text = (await http.GetStringAsync(VersionUrl, ct)).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null; // fall back to the pinned version
        }
    }
}
