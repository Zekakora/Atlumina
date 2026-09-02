using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MyAlbum.Core.Services;

/// <summary>
/// Feature embedding via the already-downloaded MobileNetV2 classifier. Because this model's
/// intermediate pooling node isn't exposed as a runnable output, we use the final 1000-dim
/// ImageNet logits as a semantic feature vector (L2-normalized and stored in Photos.Embedding).
/// This reuses the model that SceneTaggerService already installs (no extra download) and gives
/// a real learned feature for similarity search that complements the 64-bit pHash. When the
/// MobileCLIP stack is installed, semantic search uses its 512-dim embeddings instead.
/// </summary>
public sealed class FeatureEmbeddingService
{
    private const int EmbeddingSize = 1000;
    private const int InputSize = 224;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly OnnxSessionCache _cache = new();

    /// <summary>Model path when the classifier (and thus the embedding) is available.</summary>
    public static string? InstalledModelPath => SceneTaggerService.InstalledModelPath;

    /// <summary>
    /// Computes the L2-normalized 1000-dim embedding of <paramref name="filePath"/> as float32
    /// bytes, or null when the model is missing / the photo can't be decoded.
    /// </summary>
    public byte[]? Embed(string filePath)
    {
        var modelPath = InstalledModelPath;
        if (modelPath is null)
        {
            return null;
        }
        var rgb = WicGrayscale.GetRgbFixed(filePath, InputSize, InputSize);
        if (rgb is null)
        {
            return null;
        }

        var session = _cache.Get(modelPath);
        var tensor = Preprocess(rgb.Rgb);
        using var results = _cache.Run(session, new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", tensor),
        });
        var output = results.First().AsTensor<float>();
        if (output.Length != EmbeddingSize)
        {
            return null;
        }
        return NormalizeToBytes(output);
    }

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

    private static byte[] NormalizeToBytes(Tensor<float> pool)
    {
        var vec = new float[EmbeddingSize];
        float norm = 0;
        for (int i = 0; i < EmbeddingSize; i++)
        {
            vec[i] = pool.GetValue(i);
            norm += vec[i] * vec[i];
        }
        norm = MathF.Sqrt(Math.Max(1e-9f, norm));
        for (int i = 0; i < EmbeddingSize; i++)
        {
            vec[i] /= norm;
        }
        var bytes = new byte[EmbeddingSize * 4];
        Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Decodes a stored float32 embedding and computes cosine similarity.</summary>
    public static float Cosine(byte[] a, byte[] b)
    {
        var va = new float[a.Length / 4];
        var vb = new float[b.Length / 4];
        Buffer.BlockCopy(a, 0, va, 0, a.Length);
        Buffer.BlockCopy(b, 0, vb, 0, b.Length);
        float dot = 0;
        for (int i = 0; i < va.Length; i++)
        {
            dot += va[i] * vb[i];
        }
        return dot; // both normalized → cosine
    }
}
