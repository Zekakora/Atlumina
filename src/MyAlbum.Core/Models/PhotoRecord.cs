namespace MyAlbum.Core.Models;

public enum PhotoKind
{
    Unknown = 0,
    Jpeg = 1,
    Raw = 2,
    Heif = 3,
    Png = 4,
    Webp = 5,
    Gif = 6,
    Bmp = 7,
    Tiff = 8,
    Other = 99,
}

/// <summary>
/// A single indexed photo/RAW entry in the library.
/// Metadata is cached in SQLite (L2 index) to avoid re-parsing EXIF on every load.
/// </summary>
public sealed class PhotoRecord
{
    public long Id { get; set; }
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public string Extension { get; set; } = "";
    public PhotoKind Kind { get; set; } = PhotoKind.Unknown;

    public long FileSizeBytes { get; set; }
    public DateTime FileModifiedUtc { get; set; }
    public string? ContentHash { get; set; }

    public DateTime? TakenAtUtc { get; set; }
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public string? LensModel { get; set; }
    public int? Iso { get; set; }
    public string? ShutterSpeed { get; set; }
    public double? Aperture { get; set; }
    public double? FocalLengthMm { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Orientation { get; set; }

    public double? GpsLatitude { get; set; }
    public double? GpsLongitude { get; set; }
    public double? GpsAltitude { get; set; }

    /// <summary>Reverse-geocoded place name (e.g. "四川省成都市"), backfilled in the background.</summary>
    public string? GpsPlace { get; set; }

    /// <summary>Which source produced GpsPlace: "amap" / "osm" / "offline". Null = not resolved yet.</summary>
    public string? GpsPlaceSource { get; set; }

    /// <summary>
    /// Comma-separated list of sources that already failed for this photo (e.g. "amap").
    /// Used so switching sources only re-tries photos that still have no successful place and
    /// were not already attempted by the current source (incremental, no overwrite).
    /// </summary>
    public string? GpsPlaceFailed { get; set; }

    /// <summary>LLM-normalized five-level address: 国家 → 省/州 → 市 → 区/县/街道 → 周边地标。</summary>
    public string? PlaceCountry { get; set; }
    public string? PlaceProvince { get; set; }
    public string? PlaceCity { get; set; }
    public string? PlaceDistrict { get; set; }
    public string? PlaceLandmark { get; set; }

    public string? Artist { get; set; }
    public string? Description { get; set; }
    public string? Copyright { get; set; }

    public int Rating { get; set; }
    public string? Tags { get; set; }

    public string? ThumbnailCachePath { get; set; }
    public string? PHash { get; set; }

    /// <summary>Laplacian variance of the photo (higher = sharper). Null when not analyzed.</summary>
    public double? BlurScore { get; set; }
    /// <summary>When the last AI/vision analysis (pHash / blur) ran for this photo.</summary>
    public DateTime? AiAnalyzedAtUtc { get; set; }

    /// <summary>NIMA aesthetic score in [1,10] (higher = better). Null when not scored.</summary>
    public double? AestheticScore { get; set; }

    /// <summary>Comma-separated dominant color hexes (e.g. "#3a5f7a,#c8a24b"). Null when not analyzed.</summary>
    public string? DominantColors { get; set; }

    /// <summary>1 when the photo is monochrome (B/W or sepia), 0 otherwise.</summary>
    public bool IsMono { get; set; }

    /// <summary>MobileNet penultimate-layer feature vector (1280 floats) for similarity search.</summary>
    public byte[]? Embedding { get; set; }

    /// <summary>MobileCLIP image embedding (512 floats) for semantic search.</summary>
    public byte[]? ClipEmbedding { get; set; }

    /// <summary>YOLO detections as JSON (label, score, box). Null when not analyzed.</summary>
    public string? ObjectsJson { get; set; }

    /// <summary>When the deep analysis pass (color / aesthetic / embedding / objects) last ran.</summary>
    public DateTime? DeepAnalyzedAtUtc { get; set; }

    public DateTime IndexedAtUtc { get; set; }
    public bool IsMissing { get; set; }
}
