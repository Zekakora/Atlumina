using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MyAlbum.Core.Data;
using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>Top-k classification result for one photo.</summary>
public sealed record SceneTagResult(string PhotoPath, IReadOnlyList<(string Label, float Score)> Tags);

/// <summary>
/// Scene / object auto-tagging with MobileNet V2 (ImageNet 1000 classes). Preprocesses a
/// photo to 224×224 RGB, normalizes with ImageNet mean/std, runs ONNX Runtime (DirectML —
/// NPU/GPU, falling back to CPU), then softmax + top-k. Results are written back to the
/// library as auto tags (Tags.IsAuto = 1), which surface in the AI-tags sidebar filter.
/// </summary>
public sealed class SceneTaggerService
{
    /// <summary>Default model file name under the models directory.</summary>
    public const string ModelFileName = "mobilenetv2.onnx";
    public const string LabelsFileName = "synset.txt";

    private const int InputSize = 224;

    // ImageNet normalization.
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly PhotoDatabase _db;
    private readonly ConcurrentDictionary<string, Lazy<Task<InferenceSession>>> _sessions = new();
    private readonly object _inferLock = new();

    /// <summary>Number of classes to write per photo.</summary>
    public const int TopK = 3;

    public SceneTaggerService(PhotoDatabase db)
    {
        _db = db;
    }

    /// <summary>Model path when installed, or null.</summary>
    public static string? InstalledModelPath
    {
        get
        {
            var path = Path.Combine(AiEngine.ModelsDirectory, ModelFileName);
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Tags every photo in <paramref name="photos"/> (callers should pass only photos that
    /// have no auto tag yet — <see cref="PhotoDatabase.GetPhotosWithoutAutoTagsAsync"/>).
    /// Inference runs in parallel (bounded by CPU count); a single shared ONNX session is
    /// used because <see cref="InferenceSession.Run"/> is thread-safe. Cancellation is
    /// honored between photos.
    /// </summary>
    public async Task<(int Tagged, int Failed)> TagLibraryAsync(
        IReadOnlyList<PhotoRecord> photos,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        if (photos.Count == 0 || InstalledModelPath is null)
        {
            return (0, 0);
        }

        var modelPath = InstalledModelPath!;
        var labels = LoadLabels();
        var session = await GetOrCreateSessionAsync(modelPath).ConfigureAwait(false);

        int done = 0, failed = 0;
        int degree = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var gate = new object();

        await Parallel.ForEachAsync(photos, new ParallelOptions
        {
            MaxDegreeOfParallelism = degree,
            CancellationToken = ct,
        }, async (photo, token) =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var tags = await ClassifyAsync(session, labels, photo).ConfigureAwait(false);
                if (tags.Count > 0)
                {
                    foreach (var (label, _) in tags)
                    {
                        await _db.AddTagAsync(photo.Id, label, isAuto: true).ConfigureAwait(false);
                    }
                }
                lock (gate)
                {
                    done++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    if (LastError is null)
                    {
                        LastError = $"{Path.GetFileName(photo.FilePath)}: {ex.GetType().Name}: {ex.Message}";
                    }
                    failed++;
                }
            }
            if (progress is not null)
            {
                int d;
                lock (gate) { d = done; }
                if (d % 5 == 0)
                {
                    progress.Report((d, photos.Count, Path.GetFileName(photo.FilePath)));
                }
            }
        });

        progress?.Report((done, photos.Count, ""));
        return (done, failed);
    }

    /// <summary>Last failure message (diagnostics; null when everything succeeded).</summary>
    public string? LastError { get; private set; }

    private async Task<InferenceSession> GetOrCreateSessionAsync(string modelPath)
    {
        var lazy = _sessions.GetOrAdd(modelPath, _ => new Lazy<Task<InferenceSession>>(
            () => Task.FromResult(new InferenceSession(modelPath, AiEngine.CreateSessionOptions())),
            LazyThreadSafetyMode.ExecutionAndPublication));
        return await lazy.Value.ConfigureAwait(false);
    }

    /// <summary>Runs one photo through the network and returns top-k (label, softmax score).</summary>
    public async Task<IReadOnlyList<(string, float)>> ClassifyAsync(PhotoRecord photo)
    {
        var modelPath = InstalledModelPath;
        if (modelPath is null)
        {
            return [];
        }
        var session = await GetOrCreateSessionAsync(modelPath).ConfigureAwait(false);
        return await ClassifyAsync(session, LoadLabels(), photo).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<(string, float)>> ClassifyAsync(
        InferenceSession session,
        string[]? labels,
        PhotoRecord photo)
    {
        // Decode + preprocess on a worker (WIC is async but we keep it off the caller).
        var tensor = await Task.Run(() =>
        {
            var rgb = WicGrayscale.GetRgbFixed(photo.FilePath, InputSize, InputSize);
            if (rgb is null)
            {
                return null;
            }
            return Preprocess(rgb.Rgb);
        }).ConfigureAwait(false);
        if (tensor is null)
        {
            return [];
        }

        // The DirectML EP is not safe for concurrent Run() calls on a shared session (a
        // native access violation), so inference is serialized here while decoding stays
        // parallel — the throughput loss is small.
        lock (_inferLock)
        {
            using var result = session.Run(new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", tensor),
            });
            var scores = result.First().AsTensor<float>();
            return TopLabels(scores, labels, TopK);
        }
    }

    /// <summary>Converts interleaved [0,255] RGB into a normalized NCHW float tensor.</summary>
    private static DenseTensor<float> Preprocess(byte[] rgb)
    {
        int count = InputSize * InputSize;
        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        for (int i = 0; i < count; i++)
        {
            int o = i * 3;
            tensor[0, 0, i / InputSize, i % InputSize] = (rgb[o] / 255f - Mean[0]) / Std[0];
            tensor[0, 1, i / InputSize, i % InputSize] = (rgb[o + 1] / 255f - Mean[1]) / Std[1];
            tensor[0, 2, i / InputSize, i % InputSize] = (rgb[o + 2] / 255f - Mean[2]) / Std[2];
        }
        return tensor;
    }

    /// <summary>Softmax + top-k labels. Label text comes from synset.txt (n* id + name).</summary>
    private static IReadOnlyList<(string, float)> TopLabels(Tensor<float> scores, string[]? labels, int k)
    {
        int n = checked((int)scores.Length);
        var indexed = new (int Idx, float V)[n];
        for (int i = 0; i < n; i++)
        {
            indexed[i] = (i, scores.GetValue(i));
        }
        Array.Sort(indexed, (a, b) => b.V.CompareTo(a.V));

        // Softmax over the raw logits.
        var exp = new float[n];
        float sum = 0;
        for (int i = 0; i < n; i++)
        {
            exp[i] = MathF.Exp(indexed[i].V);
            sum += exp[i];
        }

        var result = new List<(string, float)>(k);
        for (int i = 0; i < k; i++)
        {
            var label = labels is not null && indexed[i].Idx < labels.Length
                ? CleanLabel(labels[indexed[i].Idx])
                : $"class-{indexed[i].Idx}";
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }
            result.Add((label, exp[indexed[i].Idx] / sum));
        }
        return result;
    }

    /// <summary>Turns "n01440764 tench, Tinca tinca" into "tench".</summary>
    private static string CleanLabel(string synset)
    {
        int space = synset.IndexOf(' ');
        var name = space >= 0 ? synset[(space + 1)..] : synset;
        int comma = name.IndexOf(',');
        return (comma >= 0 ? name[..comma] : name).Trim();
    }

    private static string[]? LoadLabels()
    {
        var path = Path.Combine(AiEngine.ModelsDirectory, LabelsFileName);
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
