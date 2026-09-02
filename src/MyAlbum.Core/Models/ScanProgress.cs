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
}
