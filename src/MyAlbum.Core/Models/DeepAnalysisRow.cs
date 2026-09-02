namespace MyAlbum.Core.Models;

/// <summary>
/// One row of the deep-analysis pass: aesthetic score, dominant-color palette, mono flag,
/// feature embeddings (MobileNet penultimate + MobileCLIP), YOLO object detections, and the
/// timestamp the pass ran. All fields are optional so a single bulk write can cover partial
/// runs (e.g. semantic-search only computes the clip embedding).
/// </summary>
public sealed record DeepAnalysisRow(
    long Id,
    double? AestheticScore = null,
    string? DominantColors = null,
    bool IsMono = false,
    byte[]? Embedding = null,
    byte[]? ClipEmbedding = null,
    string? ObjectsJson = null,
    DateTime? DeepAnalyzedAtUtc = null)
{
    /// <summary>Timestamp used for the DeepAnalyzedAtUtc column when none was supplied.</summary>
    public DateTime EffectiveAnalyzedAt => DeepAnalyzedAtUtc ?? DateTime.UtcNow;
}
