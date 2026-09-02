namespace MyAlbum.Core.Models;

/// <summary>分组类型：组内有带 GPS 的照片即可自动链式归类，否则需手动设置。</summary>
public enum GpsGroupKind
{
    Auto = 0,
    Manual = 1,
}

/// <summary>一张无 GPS 照片的归类结果与建议位置。</summary>
public sealed class GpnAssignment
{
    public required PhotoRecord Photo { get; init; }

    /// <summary>链式/手动给出的建议坐标（未确认时也用于地图预览）。</summary>
    public double? AssignedLat { get; set; }
    public double? AssignedLon { get; set; }
    public double? AssignedAlt { get; set; }

    /// <summary>时间上最近的带 GPS 锚点（链式来源）。</summary>
    public PhotoRecord? NearestAnchor { get; set; }

    /// <summary>与最近锚点的拍摄时间差（秒）。</summary>
    public double? TimeGapSeconds { get; set; }

    /// <summary>与最近锚点文件名的序号循环距离（DSC99999↔DSC00001 视为连续），无法提取时为 null。</summary>
    public int? FilenameCircularDistance { get; set; }

    /// <summary>时间差距超过阈值，建议人工确认。</summary>
    public bool NeedsReview { get; set; }

    /// <summary>由用户在地图上手动设置（红钉），而非链式推断。</summary>
    public bool ManuallySet { get; set; }
}

/// <summary>一次归类的一个时间组。</summary>
public sealed class GpsGroup
{
    public GpsGroupKind Kind { get; set; }
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public int AnchorCount { get; set; }
    public List<GpnAssignment> GpnItems { get; set; } = new();
}

/// <summary>整个库的归类结果。</summary>
public sealed class GpsGroupingResult
{
    public List<GpsGroup> Groups { get; set; } = new();
    /// <summary>没有拍摄时间、无法按时间归类的照片。</summary>
    public List<PhotoRecord> NoTimePhotos { get; set; } = new();
    public int AnchorCount { get; set; }
    public int GpnCount { get; set; }
}
