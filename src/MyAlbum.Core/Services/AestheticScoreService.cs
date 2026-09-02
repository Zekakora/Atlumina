using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MyAlbum.Core.Services;

/// <summary>
/// NIMA (Neural Image Assessment) aesthetic scoring via ONNX Runtime. The MobileNet-based
/// model outputs a 10-bin probability distribution over scores 1..10; the expected value is
/// the photo's aesthetic score. This is the model-backed half of the "无意义照片" heuristic:
/// a low score (together with a low blur score / low colorfulness) flags a photo as a throwaway.
/// </summary>
public sealed class AestheticScoreService
{
    public const string ModelFileName = "nima.onnx";

    private const int InputSize = 224;

    // NIMA was trained with MobileNet-style normalization (ImageNet mean/std).
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly OnnxSessionCache _cache = new();

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
    /// Scores one photo. Returns the expected aesthetic score in [1,10] (higher = better),
    /// or null when the model is missing / the photo cannot be decoded / inference fails.
    /// </summary>
    public double? Score(string filePath)
    {
        var modelPath = InstalledModelPath;
        if (modelPath is null)
        {
            return null;
        }
        var session = _cache.Get(modelPath);
        var rgb = WicGrayscale.GetRgbFixed(filePath, InputSize, InputSize);
        if (rgb is null)
        {
            return null;
        }

        var tensor = Preprocess(rgb.Rgb);
        using var results = _cache.Run(session, new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", tensor),
        });
        var probs = results.First().AsTensor<float>();
        return ExpectedScore(probs);
    }

    /// <summary>
    /// Converts interleaved [0,255] RGB into a normalized float tensor. This NIMA export
    /// expects NHWC layout (1×224×224×3) rather than the usual NCHW.
    /// </summary>
    private static DenseTensor<float> Preprocess(byte[] rgb)
    {
        int count = InputSize * InputSize;
        var tensor = new DenseTensor<float>(new[] { 1, InputSize, InputSize, 3 });
        for (int i = 0; i < count; i++)
        {
            int o = i * 3;
            int y = i / InputSize, x = i % InputSize;
            tensor[0, y, x, 0] = (rgb[o] / 255f - Mean[0]) / Std[0];
            tensor[0, y, x, 1] = (rgb[o + 1] / 255f - Mean[1]) / Std[1];
            tensor[0, y, x, 2] = (rgb[o + 2] / 255f - Mean[2]) / Std[2];
        }
        return tensor;
    }

    /// <summary>Expected value of a 10-bin score distribution over scores 1..10.</summary>
    private static double ExpectedScore(Tensor<float> probs)
    {
        // Softmax (the raw output is usually logits; applying it here is harmless if already soft).
        int n = checked((int)probs.Length);
        var exp = new float[n];
        float sum = 0;
        for (int i = 0; i < n; i++)
        {
            exp[i] = MathF.Exp(probs.GetValue(i));
            sum += exp[i];
        }
        double score = 0;
        for (int i = 0; i < n; i++)
        {
            double p = exp[i] / Math.Max(1e-12f, sum);
            score += (i + 1) * p;
        }
        return Math.Clamp(score, 1.0, 10.0);
    }
}
