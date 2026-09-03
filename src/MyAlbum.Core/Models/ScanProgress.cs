namespace MyAlbum.Core.Models;

public sealed class ScanProgress
{
    public required string Folder { get; init; }
    public required string CurrentFile { get; init; }
    public required int TotalFiles { get; init; }
    public required int Processed { get; init; }
    public required int Indexed { get; init; }
    public required int Skipped { get; init; }
    public required int Failed { get; init; }

    public double Fraction => TotalFiles == 0 ? 0 : (double)Processed / TotalFiles;
}

public sealed class ScanResult
{
    public required string Folder { get; init; }
    public int TotalFiles { get; set; }
    public int Indexed { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int MarkedMissing { get; set; }

    /// <summary>Case-insensitive duplicate rows (same physical file, different path casing)
    /// removed during the scan so the grid stops double-counting.</summary>
    public int RemovedDuplicates { get; set; }

    /// <summary>Per-file failure details ("文件名：原因") collected during the scan, so a
    /// refresh that reports failures can tell the user exactly which file failed and why.</summary>
    public List<string> FailedDetails { get; } = new();
}
