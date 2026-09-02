namespace MyAlbum.Core.Models;

public enum PhotoDateStatus
{
    Mismatch = 0,
    Match = 1,
    NoExif = 2,
}

/// <summary>
/// A photo whose Windows file creation time differs from its EXIF shooting date.
/// Only file-system timestamps are fixed later — EXIF bytes are never touched.
/// </summary>
public sealed class PhotoDateMismatch
{
    public string FilePath { get; set; } = "";
    public string DirectoryPath { get; set; } = "";

    /// <summary>EXIF shooting date (wall-clock local time, no timezone info).</summary>
    public DateTime TakenAt { get; set; }

    /// <summary>Current Windows file creation time (local kind).</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>One photo audited during a date-consistency scan (matched, mismatched or without a shooting date).</summary>
public sealed class PhotoDateCheckItem
{
    public string FilePath { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public PhotoDateStatus Status { get; set; }

    /// <summary>EXIF shooting date (wall-clock local). Null when <see cref="Status"/> is NoExif.</summary>
    public DateTime? TakenAt { get; set; }

    /// <summary>Windows file creation time (local kind). Null when <see cref="Status"/> is NoExif.</summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>Non-null only when <see cref="Status"/> is Mismatch; carries the data used for the actual fix.</summary>
    public PhotoDateMismatch? Mismatch { get; set; }
}

/// <summary>Result of a date-consistency scan over a set of photos.</summary>
public sealed class PhotoDateScanResult
{
    /// <summary>Every scanned photo, in input order, with its audit status.</summary>
    public List<PhotoDateCheckItem> Items { get; } = new();

    public int MismatchCount { get; set; }
    public int MatchedCount { get; set; }
    public int NoExifCount { get; set; }
}

/// <summary>Outcome of fixing a batch of file timestamps.</summary>
public sealed class PhotoDateFixResult
{
    public int Ok { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = new();
}
