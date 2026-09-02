using System.Text.Json;
using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>Result of one deep-analysis pass.</summary>
public sealed record DeepAnalysisResult(int Total, int Analyzed, int Failed);

/// <summary>
/// Coordinator for the deep-analysis pass: computes color palette + mono flag (CPU), NIMA
/// aesthetic score, MobileNet feature embedding, and (when CLIP models are installed) the
/// MobileCLIP image embedding + YOLO object detections for every photo that has not been
/// analyzed yet. Results are bulk-written to the index. Runs fully in the background.
/// </summary>
public sealed class DeepAnalysisService
{
    /// <summary>Below this NIMA score a photo is treated as "likely junk" for the low-score list.</summary>
    public const double LowAestheticThreshold = 4.5;

    private readonly PhotoDatabase _db;
    private readonly AestheticScoreService _aesthetic;
    private readonly FeatureEmbeddingService _embedding;
    private readonly ObjectDetectionService _detection;
    private readonly ClipService _clip;
    private volatile string? _lastError;

    public DeepAnalysisService(
        PhotoDatabase db,
        AestheticScoreService aesthetic,
        FeatureEmbeddingService embedding,
        ObjectDetectionService detection,
        ClipService clip)
    {
        _db = db;
        _aesthetic = aesthetic;
        _embedding = embedding;
        _detection = detection;
        _clip = clip;
    }

    public string? LastError => _lastError;

    /// <summary>
    /// Analyzes all photos that have not been deep-analyzed yet. When <paramref name="includeClip"/>
    /// is false the (large) MobileCLIP embeddings are skipped; when true they're computed for any
    /// photo that still lacks one. Returns (total, analyzed, failed).
    /// </summary>
    public async Task<DeepAnalysisResult> AnalyzeLibraryAsync(
        bool includeClip = true,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        var pending = await _db.GetPhotosPendingDeepAnalysisAsync(limit: int.MaxValue);
        bool clipInstalled = ClipService.IsInstalled;
        if (pending.Count == 0 && (!includeClip || !clipInstalled))
        {
            // Even with nothing "pending", still backfill CLIP embeddings for already-analyzed photos.
            var clipPending = includeClip && clipInstalled
                ? await _db.GetPhotosPendingClipEmbeddingAsync(limit: int.MaxValue)
                : [];
            if (clipPending.Count == 0)
            {
                return new DeepAnalysisResult(0, 0, 0);
            }
            pending = clipPending;
        }

        int failed = 0;
        var batch = new List<DeepAnalysisRow>(512);
        int degree = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var now = DateTime.UtcNow;

        await Parallel.ForEachAsync(pending, new ParallelOptions
        {
            MaxDegreeOfParallelism = degree,
            CancellationToken = ct,
        }, (photo, token) =>
        {
            token.ThrowIfCancellationRequested();
            var row = AnalyzeOne(photo, includeClip && clipInstalled, now);
            if (row is null)
            {
                Interlocked.Increment(ref failed);
            }
            else
            {
                lock (batch)
                {
                    batch.Add(row);
                }
            }
            int done;
            lock (batch) { done = batch.Count; }
            if (progress is not null && done % 5 == 0)
            {
                progress.Report((done, pending.Count, Path.GetFileName(photo.FilePath)));
            }
            return ValueTask.CompletedTask;
        });

        await _db.BulkSetDeepAnalysisAsync(batch);
        progress?.Report((batch.Count, pending.Count, ""));

        return new DeepAnalysisResult(pending.Count, batch.Count, failed);
    }

    private DeepAnalysisRow? AnalyzeOne(PhotoRecord photo, bool includeClip, DateTime now)
    {
        try
        {
            // CPU color analysis is the anchor; if it fails the file is undecodable → treat as failed.
            var color = ColorAnalysisService.Analyze(photo.FilePath);
            if (color is null)
            {
                return null;
            }
            double? aesthetic = _aesthetic.Score(photo.FilePath);
            byte[]? embedding = _embedding.Embed(photo.FilePath);
            byte[]? clip = includeClip ? _clip.EmbedImage(photo.FilePath) : null;
            string? objectsJson = SerializeObjects(_detection.Detect(photo.FilePath));

            return new DeepAnalysisRow(
                photo.Id,
                aesthetic,
                color.DominantColorsCsv,
                color.IsMono,
                embedding,
                clip,
                objectsJson,
                now);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return null;
        }
    }

    /// <summary>Serializes YOLO detections to compact JSON for the ObjectsJson column.</summary>
    private static string? SerializeObjects(IReadOnlyList<DetectedObject> objects)
    {
        if (objects.Count == 0)
        {
            return null;
        }
        var arr = objects.Select(o => new
        {
            label = o.Label,
            score = Math.Round(o.Confidence, 3),
        });
        return JsonSerializer.Serialize(arr);
    }

    /// <summary>Parses the top label names from a stored ObjectsJson blob (used for auto tags).</summary>
    public static IEnumerable<string> ParseObjectLabels(string? json)
    {
        foreach (var label in ParseLabelsCore(json))
        {
            yield return label;
        }
    }

    private static IEnumerable<string> ParseLabelsCore(string? json)
    {
        var labels = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return labels;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
                {
                    labels.Add(label.GetString()!);
                }
            }
        }
        catch
        {
            // corrupt JSON → no labels
        }
        return labels;
    }
}
