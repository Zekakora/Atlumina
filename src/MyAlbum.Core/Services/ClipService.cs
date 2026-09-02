using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MyAlbum.Core.Services;

/// <summary>
/// MobileCLIP-S2 semantic search. The image encoder (clip-vision.onnx) maps a 256×256 photo to
/// a 512-dim L2-normalized vector; the text encoder (clip-text.onnx) maps a CLIP-tokenized
/// query into the same space. Cosine similarity between the two finds photos matching a natural
/// language description ("海边的狗", "红色跑车"). Tokenization uses the standard CLIP BPE
/// (vocab.json + merges.txt), shared by all CLIP variants.
/// </summary>
public sealed class ClipService
{
    public const string VisionFileName = "clip-vision.onnx";
    public const string TextFileName = "clip-text.onnx";
    public const string VocabFileName = "clip-vocab.json";
    public const string MergesFileName = "clip-merges.txt";

    public const int ImageSize = 256;
    public const int MaxTokens = 77;
    public const int EmbeddingDim = 512;

    private readonly OnnxSessionCache _cache = new();
    private readonly object _textLock = new();

    /// <summary>True when the whole stack (image + text + tokenizer) is installed.</summary>
    public static bool IsInstalled => AiModelDownloader.IsClipInstalled;

    /// <summary>Full model path for the vision encoder, or null.</summary>
    public static string? VisionModelPath => Path.Combine(AiEngine.ModelsDirectory, VisionFileName) is { } p && File.Exists(p) ? p : null;

    /// <summary>Full model path for the text encoder, or null.</summary>
    public static string? TextModelPath => Path.Combine(AiEngine.ModelsDirectory, TextFileName) is { } p && File.Exists(p) ? p : null;

    // ---------- Image encoding ----------

    /// <summary>
    /// Encodes <paramref name="filePath"/> into a 512-dim L2-normalized vector (float32 bytes),
    /// or null when models are missing / the photo can't be decoded / inference fails.
    /// </summary>
    public byte[]? EmbedImage(string filePath)
    {
        var modelPath = VisionModelPath;
        if (modelPath is null)
        {
            return null;
        }
        var src = WicGrayscale.GetRgbFixed(filePath, ImageSize, ImageSize);
        if (src is null)
        {
            return null;
        }

        var tensor = ImageTensor(src);
        var session = _cache.Get(modelPath);
        string inputName = PickInputName(session, "pixel_values", "image");
        string? outputName = PickOutputName(session, "image_embeds", "image_features", "image_embedding");
        using var results = _cache.Run(session, new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor),
        });
        return ExtractEmbedding(results, outputName);
    }

    /// <summary>
    /// Encodes a Chinese/English query into a 512-dim L2-normalized vector (float32 bytes),
    /// or null when the text model / tokenizer files are missing.
    /// </summary>
    public byte[]? EmbedText(string query)
    {
        var modelPath = TextModelPath;
        if (modelPath is null || !File.Exists(VocabPath()) || !File.Exists(MergesPath()))
        {
            return null;
        }
        var tokens = Tokenize(query);
        if (tokens is null)
        {
            return null;
        }

        var inputIds = new DenseTensor<long>(new[] { 1, MaxTokens });
        for (int i = 0; i < MaxTokens; i++)
        {
            inputIds[0, i] = tokens[i];
        }

        var session = _cache.Get(modelPath);
        string inputName = PickInputName(session, "input_ids", "input");
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputIds) };

        // Some exports also require an attention_mask input; fill with 1s if present.
        foreach (var meta in session.InputMetadata)
        {
            if (meta.Key == inputName || !meta.Key.Contains("mask", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var mask = new DenseTensor<long>(new[] { 1, MaxTokens });
            for (int i = 0; i < MaxTokens; i++)
            {
                mask[0, i] = tokens[i] != 0 ? 1 : 0;
            }
            inputs.Add(NamedOnnxValue.CreateFromTensor(meta.Key, mask));
        }

        string outputName;
        lock (_textLock)
        {
            using var results = _cache.Run(session, inputs);
            outputName = results.First().Name;
            return ExtractEmbedding(results, outputName);
        }
    }

    private static byte[]? ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, string? outputName)
    {
        DisposableNamedOnnxValue? value = null;
        if (outputName is not null)
        {
            value = results.FirstOrDefault(r => r.Name == outputName);
        }
        value ??= results.FirstOrDefault();
        if (value is null)
        {
            return null;
        }
        var tensor = value.AsTensor<float>();
        int dim = (int)tensor.Length;
        var vec = new float[dim];
        float norm = 0;
        for (int i = 0; i < dim; i++)
        {
            vec[i] = tensor.GetValue(i);
            norm += vec[i] * vec[i];
        }
        norm = MathF.Sqrt(Math.Max(1e-9f, norm));
        for (int i = 0; i < dim; i++)
        {
            vec[i] /= norm;
        }
        var bytes = new byte[dim * 4];
        Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static DenseTensor<float> ImageTensor(WicRgbPixels src)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, ImageSize, ImageSize });
        int count = ImageSize * ImageSize;
        for (int i = 0; i < count; i++)
        {
            int o = i * 3;
            tensor[0, 0, i / ImageSize, i % ImageSize] = src.Rgb[o] / 255f;
            tensor[0, 1, i / ImageSize, i % ImageSize] = src.Rgb[o + 1] / 255f;
            tensor[0, 2, i / ImageSize, i % ImageSize] = src.Rgb[o + 2] / 255f;
        }
        return tensor;
    }

    private static string PickInputName(InferenceSession session, params string[] preferred)
    {
        foreach (var p in preferred)
        {
            if (session.InputMetadata.ContainsKey(p))
            {
                return p;
            }
        }
        return session.InputMetadata.Keys.First();
    }

    private static string? PickOutputName(InferenceSession session, params string[] preferred)
    {
        foreach (var p in preferred)
        {
            if (session.OutputMetadata.ContainsKey(p))
            {
                return p;
            }
        }
        return session.OutputMetadata.Keys.FirstOrDefault();
    }

    private static string VocabPath() => Path.Combine(AiEngine.ModelsDirectory, VocabFileName);
    private static string MergesPath() => Path.Combine(AiEngine.ModelsDirectory, MergesFileName);

    // ---------- Cosine ----------

    /// <summary>Cosine similarity between two normalized embeddings (float32 blobs).</summary>
    public static float Cosine(byte[] a, byte[] b)
    {
        var va = new float[a.Length / 4];
        var vb = new float[b.Length / 4];
        Buffer.BlockCopy(a, 0, va, 0, a.Length);
        Buffer.BlockCopy(b, 0, vb, 0, b.Length);
        float dot = 0;
        for (int i = 0; i < va.Length && i < vb.Length; i++)
        {
            dot += va[i] * vb[i];
        }
        return dot;
    }

    // ---------- CLIP BPE tokenizer ----------

    private static Dictionary<string, long>? _vocab;
    private static Dictionary<(string, string), string>? _merges;
    private static readonly object _tokenizerLock = new();

    /// <summary>
    /// Tokenizes <paramref name="query"/> into a fixed-length <see cref="MaxTokens"/> id array
    /// (CLIP format: <code>&lt;|startoftext|&gt;</code> + BPE ids + <code>&lt;|endoftext|&gt;</code>,
    /// zero-padded). Returns null when the vocab/merges files can't be loaded.
    /// </summary>
    public static long[]? Tokenize(string query)
    {
        var (vocab, merges) = LoadTokenizer();
        if (vocab is null || merges is null)
        {
            return null;
        }
        string text = query.Trim().ToLowerInvariant();
        var words = ByteLevelRegex.Split(text).Where(w => w.Length > 0).ToList();

        var ids = new List<long> { vocab["<|startoftext|>"] };
        foreach (var word in words)
        {
            var bpe = BpeEncode(word, vocab, merges);
            if (ids.Count + bpe.Count >= MaxTokens - 1)
            {
                break;
            }
            ids.AddRange(bpe);
        }
        ids.Add(vocab["<|endoftext|>"]);

        var result = new long[MaxTokens];
        for (int i = 0; i < ids.Count && i < MaxTokens; i++)
        {
            result[i] = ids[i];
        }
        return result;
    }

    private static (Dictionary<string, long>?, Dictionary<(string, string), string>?) LoadTokenizer()
    {
        lock (_tokenizerLock)
        {
            if (_vocab is not null && _merges is not null)
            {
                return (_vocab, _merges);
            }
            try
            {
                var vocabJson = File.ReadAllText(VocabPath());
                _vocab = JsonSerializer.Deserialize<Dictionary<string, long>>(vocabJson)
                    ?? throw new InvalidDataException("vocab empty");
                var mergesLines = File.ReadAllLines(MergesPath());
                _merges = new Dictionary<(string, string), string>();
                foreach (var line in mergesLines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    {
                        continue;
                    }
                    var parts = line.Split(' ', 2);
                    if (parts.Length == 2)
                    {
                        _merges[(parts[0], parts[1])] = parts[0] + parts[1];
                    }
                }
                return (_vocab, _merges);
            }
            catch
            {
                return (null, null);
            }
        }
    }

    /// <summary>Simple word splitter approximating GPT-2/CLIP byte-level regex on CJK-safe chars.</summary>
    private static readonly System.Text.RegularExpressions.Regex ByteLevelRegex = new(
        @"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|\p{L}+|\p{N}+|[^\s\p{L}\p{N}]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Byte-level BPE encode of a single word using the given vocab + merges.</summary>
    private static List<long> BpeEncode(string word, Dictionary<string, long> vocab, Dictionary<(string, string), string> merges)
    {
        // Direct hit on a full word.
        if (vocab.TryGetValue(word, out var direct))
        {
            return [direct];
        }
        var chars = new List<string>(word.Length);
        foreach (char c in word)
        {
            chars.Add(c.ToString());
        }
        // Repeatedly merge the highest-priority adjacent pair.
        while (chars.Count > 1)
        {
            string? bestMerge = null;
            int bestIdx = -1;
            for (int i = 0; i < chars.Count - 1; i++)
            {
                if (merges.TryGetValue((chars[i], chars[i + 1]), out var merged) &&
                    (bestIdx < 0 || string.CompareOrdinal(merged, bestMerge) < 0))
                {
                    bestMerge = merged;
                    bestIdx = i;
                }
            }
            if (bestIdx < 0)
            {
                break;
            }
            var next = new List<string>(chars.Count - 1);
            for (int i = 0; i < chars.Count; i++)
            {
                if (i == bestIdx)
                {
                    next.Add(bestMerge!);
                    i++; // skip the second char of the pair
                }
                else
                {
                    next.Add(chars[i]);
                }
            }
            chars = next;
        }

        var ids = new List<long>(chars.Count);
        foreach (var piece in chars)
        {
            if (vocab.TryGetValue(piece, out var id))
            {
                ids.Add(id);
            }
            else if (piece.Length > 1 && vocab.TryGetValue(piece[0].ToString(), out var firstId))
            {
                ids.Add(firstId);
                if (vocab.TryGetValue(piece[1].ToString(), out var secondId))
                {
                    ids.Add(secondId);
                }
            }
            else if (piece.Length > 0)
            {
                // Unmapped unicode → try each char.
                foreach (char c in piece)
                {
                    if (vocab.TryGetValue(c.ToString(), out var cid))
                    {
                        ids.Add(cid);
                    }
                }
            }
        }
        return ids.Count == 0 ? [vocab["<|endoftext|>"]] : ids;
    }
}
