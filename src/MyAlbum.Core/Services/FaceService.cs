using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MyAlbum.Core.Models;
using Windows.Graphics.Imaging;

namespace MyAlbum.Core.Services;

/// <summary>A detected face: bounding box (in original image pixels) plus 5 landmarks.</summary>
public readonly record struct FaceDetectResult(float X, float Y, float Width, float Height, float Score, float[] Landmarks)
{
    /// <summary>Center of the face bounding box.</summary>
    public (float X, float Y) Center => (X + Width / 2f, Y + Height / 2f);
}

/// <summary>One detected face with its 512-dim ArcFace embedding.</summary>
public sealed class FaceEmbedding
{
    public long PhotoId { get; init; }
    public string PhotoPath { get; init; } = "";
    public FaceDetectResult Box { get; init; }
    public float[] Vector { get; init; } = [];
}

/// <summary>
/// Face detection + recognition: YuNet detects faces (with 5 landmarks), each face is
/// aligned to 112×112 and fed to ArcFace (LResNet100E-IR int8) for a 512-dim embedding.
/// Embeddings are cosine-similarity comparable and can be clustered into people albums.
/// Runs fully in the background; ONNX sessions are created once per process and cached.
/// </summary>
public sealed class FaceService
{
    public const string YuNetFileName = "yunet.onnx";
    public const string ArcFaceFileName = "arcface.onnx";

    /// <summary>
    /// YuNet confidence threshold. With the raw [0,255] BGR input the obj branch is valid and
    /// score = sqrt(cls*obj), so 0.5 matches OpenCV's default behavior for this export.
    /// </summary>
    public const float DetectionThreshold = 0.5f;

    private const int DetectorSize = 640;
    private const int EmbedSize = 112;

    private static readonly float[] EmbedMean = [0.5f, 0.5f, 0.5f];
    private static readonly float[] EmbedStd = [0.5f, 0.5f, 0.5f];

    private readonly ConcurrentDictionary<string, Lazy<InferenceSession>> _sessions = new();

    /// <summary>Model paths when both are installed, or null.</summary>
    public static bool IsInstalled =>
        File.Exists(Path.Combine(AiEngine.ModelsDirectory, YuNetFileName)) &&
        File.Exists(Path.Combine(AiEngine.ModelsDirectory, ArcFaceFileName));

    /// <summary>
    /// Detects faces in one photo and returns their embeddings. Returns an empty list when
    /// the photo has no detectable face (or the models are missing).
    /// </summary>
    public async Task<IReadOnlyList<FaceEmbedding>> ExtractAsync(PhotoRecord photo, CancellationToken ct = default)
    {
        if (!IsInstalled)
        {
            return [];
        }
        var det = await DetectFacesAsync(photo.FilePath, ct).ConfigureAwait(false);
        if (det.Count == 0)
        {
            return [];
        }
        var embedSession = await GetOrCreateSessionAsync(ArcFaceFileName).ConfigureAwait(false);
        var embeddings = new List<FaceEmbedding>(det.Count);
        foreach (var face in det)
        {
            ct.ThrowIfCancellationRequested();
            var vec = await EmbedFaceAsync(embedSession, photo.FilePath, face).ConfigureAwait(false);
            if (vec is not null)
            {
                embeddings.Add(new FaceEmbedding
                {
                    PhotoId = photo.Id,
                    PhotoPath = photo.FilePath,
                    Box = face,
                    Vector = vec,
                });
            }
        }
        return embeddings;
    }

    /// <summary>Runs YuNet on a photo, returning the raw detected faces (no NMS yet).</summary>
    public async Task<IReadOnlyList<FaceDetectResult>> DetectFacesAsync(string filePath, CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(YuNetFileName).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var (tensor, scaleX, scaleY) = BuildDetectorInput(filePath);
            if (tensor is null)
            {
                return new List<FaceDetectResult>();
            }
            using var result = session.Run(new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", tensor),
            });
            return DecodeYuNet(result, scaleX, scaleY);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Aligned + cropped 112×112 face tensor for ArcFace, or null on decode failure.</summary>
    public async Task<DenseTensor<float>?> BuildEmbedInputAsync(string filePath, FaceDetectResult face, CancellationToken ct = default)
    {
        return await Task.Run(() => BuildEmbedInput(filePath, face), ct).ConfigureAwait(false);
    }

    private async Task<float[]?> EmbedFaceAsync(InferenceSession embedSession, string filePath, FaceDetectResult face)
    {
        var input = BuildEmbedInput(filePath, face);
        if (input is null)
        {
            return null;
        }
        using var result = embedSession.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("data", input),
        });
        var tensor = result.First().AsTensor<float>();
        int n = checked((int)tensor.Length);
        var vec = new float[n];
        for (int i = 0; i < n; i++)
        {
            vec[i] = tensor.GetValue(i);
        }
        // L2 normalize to unit length (ArcFace standard post-processing).
        float norm = MathF.Sqrt(vec.Sum(v => v * v));
        if (norm > 1e-6f)
        {
            for (int i = 0; i < n; i++)
            {
                vec[i] /= norm;
            }
        }
        return vec;
    }

    private async Task<InferenceSession> GetOrCreateSessionAsync(string fileName)
    {
        var path = Path.Combine(AiEngine.ModelsDirectory, fileName);
        var lazy = _sessions.GetOrAdd(fileName, _ => new Lazy<InferenceSession>(
            () => new InferenceSession(path, CreateFaceSessionOptions()),
            LazyThreadSafetyMode.ExecutionAndPublication));
        return await Task.FromResult(lazy.Value).ConfigureAwait(false);
    }

    /// <summary>
    /// Face models are small and run fastest on NPU/GPU via DirectML, but fall back to CPU
    /// if the accelerated provider can't be initialized. Keep the options consistent with
    /// the rest of the AI engine.
    /// </summary>
    private static SessionOptions CreateFaceSessionOptions()
    {
        // Face models are small; run them on CPU for consistent, deterministic results.
        // (DML/NPU can change numerics enough to affect small face boxes.)
        return new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
    }

    /// <summary>Decodes YuNet output into face boxes/landmarks scaled back to the original image.</summary>
    private static IReadOnlyList<FaceDetectResult> DecodeYuNet(IReadOnlyCollection<DisposableNamedOnnxValue> result, float scaleX, float scaleY)
    {
        // YuNet decode (matches OpenCV FaceDetectorYN / libfacedetection):
        //   score = sqrt(clamp01(cls) * clamp01(obj))
        //   cx = (c + bbox[0]) * stride ; cy = (r + bbox[1]) * stride
        //   w = exp(bbox[2]) * stride ; h = exp(bbox[3]) * stride
        //   landmark = (kps[2n] + c) * stride , (kps[2n+1] + r) * stride
        // where (r, c) is the grid row/col and idx = r * gridCols + c.
        var strides = new[] { 8, 16, 32 };
        var faces = new List<(FaceDetectResult Raw, float Score)>();
        foreach (var stride in strides)
        {
            string suffix = $"_{stride}";
            var bbox = result.FirstOrDefault(r => r.Name == "bbox" + suffix)?.AsTensor<float>();
            var kps = result.FirstOrDefault(r => r.Name == "kps" + suffix)?.AsTensor<float>();
            var cls = result.FirstOrDefault(r => r.Name == "cls" + suffix)?.AsTensor<float>();
            var obj = result.FirstOrDefault(r => r.Name == "obj" + suffix)?.AsTensor<float>();
            if (bbox is null || kps is null || cls is null || obj is null)
            {
                continue;
            }

            int gridCols = DetectorSize / stride;
            int gridRows = DetectorSize / stride;
            for (int r = 0; r < gridRows; r++)
            {
                for (int c = 0; c < gridCols; c++)
                {
                    int i = r * gridCols + c;
                    float clsScore = Math.Clamp(cls.GetValue(i), 0f, 1f);
                    float objScore = Math.Clamp(obj.GetValue(i), 0f, 1f);
                    float score = MathF.Sqrt(clsScore * objScore);
                    if (score < DetectionThreshold)
                    {
                        continue;
                    }

                    float cx = (c + bbox.GetValue(i * 4 + 0)) * stride;
                    float cy = (r + bbox.GetValue(i * 4 + 1)) * stride;
                    float w = MathF.Exp(bbox.GetValue(i * 4 + 2)) * stride;
                    float h = MathF.Exp(bbox.GetValue(i * 4 + 3)) * stride;
                    float x = cx - w / 2f;
                    float y = cy - h / 2f;

                    var lm = new float[10];
                    for (int p = 0; p < 5; p++)
                    {
                        lm[p * 2] = (kps.GetValue(i * 10 + p * 2) + c) * stride;
                        lm[p * 2 + 1] = (kps.GetValue(i * 10 + p * 2 + 1) + r) * stride;
                    }
                    var raw = new FaceDetectResult(
                        x / scaleX, y / scaleY, w / scaleX, h / scaleY, score,
                        new[] { lm[0] / scaleX, lm[1] / scaleY, lm[2] / scaleX, lm[3] / scaleY, lm[4] / scaleX, lm[5] / scaleY, lm[6] / scaleX, lm[7] / scaleY, lm[8] / scaleX, lm[9] / scaleY });
                    faces.Add((raw, score));
                }
            }
        }
        faces.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<FaceDetectResult>();
        foreach (var (face, _) in faces)
        {
            bool suppress = kept.Any(k => Iou(k, face) > 0.3f);
            if (!suppress)
            {
                kept.Add(face);
            }
        }
        return kept;
    }

    private static float Iou(FaceDetectResult a, FaceDetectResult b)
    {
        float x1 = MathF.Max(a.X, b.X), y1 = MathF.Max(a.Y, b.Y);
        float x2 = MathF.Min(a.X + a.Width, b.X + b.Width);
        float y2 = MathF.Min(a.Y + a.Height, b.Y + b.Height);
        float inter = MathF.Max(0, x2 - x1) * MathF.Max(0, y2 - y1);
        float areaA = a.Width * a.Height, areaB = b.Width * b.Height;
        return inter / (areaA + areaB - inter + 1e-6f);
    }

    /// <summary>Resizes the photo to 640×640 (letterbox-free stretch) and returns the NCHW tensor plus scale factors.</summary>
    private static (DenseTensor<float>?, float, float) BuildDetectorInput(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        using var ras = fs.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
        var frame = decoder.GetFrameAsync(0).AsTask().GetAwaiter().GetResult();
        uint srcW = frame.PixelWidth, srcH = frame.PixelHeight;
        if (srcW == 0 || srcH == 0)
        {
            return (null, 1, 1);
        }
        float scaleX = srcW / (float)DetectorSize;
        float scaleY = srcH / (float)DetectorSize;

        var transform = new BitmapTransform
        {
            ScaledWidth = DetectorSize,
            ScaledHeight = DetectorSize,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bmp = frame.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
        if (bmp is null)
        {
            return (null, scaleX, scaleY);
        }
        var buffer = new byte[DetectorSize * DetectorSize * 4];
        bmp.CopyToBuffer(buffer.AsBuffer());

        var tensor = new DenseTensor<float>(new[] { 1, 3, DetectorSize, DetectorSize });
        for (int i = 0; i < DetectorSize * DetectorSize; i++)
        {
            int o = i * 4;
            int x = i % DetectorSize, y = i / DetectorSize;
            // YuNet (OpenCV blobFromImage scalefactor=1.0, no swapRB) expects raw [0,255] BGR.
            tensor[0, 0, y, x] = buffer[o];      // B
            tensor[0, 1, y, x] = buffer[o + 1];  // G
            tensor[0, 2, y, x] = buffer[o + 2];  // R
        }
        return (tensor, scaleX, scaleY);
    }

    /// <summary>
    /// Crops the face region from the full image and resizes to 112×112 (bilinear). The face
    /// box from YuNet is already tight; we expand it a little for the forehead. The whole
    /// frame is decoded at a capped size, then the face is cropped and resized in managed code
    /// (WIC's BitmapBounds + ScaledWidth combination is unreliable here).
    /// </summary>
    private static DenseTensor<float>? BuildEmbedInput(string filePath, FaceDetectResult face)
    {
        using var fs = File.OpenRead(filePath);
        using var ras = fs.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
        var frame = decoder.GetFrameAsync(0).AsTask().GetAwaiter().GetResult();

        // Expand the box a touch so the full face + forehead fits, clamped to the image.
        float margin = 0.15f;
        float imgW = frame.PixelWidth, imgH = frame.PixelHeight;
        float x = MathF.Max(0, face.X - face.Width * margin);
        float y = MathF.Max(0, face.Y - face.Height * margin * 1.5f);
        float w = MathF.Min(face.Width * (1 + 2 * margin), imgW - x);
        float h = MathF.Min(face.Height * (1 + 2 * margin + 0.5f * margin), imgH - y);
        if (w < 2 || h < 2)
        {
            return null;
        }

        // Decode the whole frame at a capped size, then crop + bilinear-resize the face.
        int sample = Math.Max(320, (int)MathF.Max(w, h) * 2);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)sample,
            ScaledHeight = (uint)sample,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bmp = frame.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
        if (bmp is null)
        {
            return null;
        }
        var buffer = new byte[sample * sample * 4];
        bmp.CopyToBuffer(buffer.AsBuffer());

        float scale = sample / imgW; // uniform (square source → square sample)
        float fx = x * scale, fy = y * scale, fw = w * scale, fh = h * scale;

        var tensor = new DenseTensor<float>(new[] { 1, 3, EmbedSize, EmbedSize });
        for (int oy = 0; oy < EmbedSize; oy++)
        {
            for (int ox = 0; ox < EmbedSize; ox++)
            {
                float srcX = fx + fw * (ox / (float)(EmbedSize - 1));
                float srcY = fy + fh * (oy / (float)(EmbedSize - 1));
                int x0 = Math.Clamp((int)srcX, 0, sample - 1);
                int y0 = Math.Clamp((int)srcY, 0, sample - 1);
                int x1 = Math.Min(x0 + 1, sample - 1);
                int y1 = Math.Min(y0 + 1, sample - 1);
                float tx = srcX - x0, ty = srcY - y0;
                for (int ch = 0; ch < 3; ch++)
                {
                    int srcCh = 2 - ch; // BGRA buffer → RGB tensor channel
                    float p00 = buffer[(y0 * sample + x0) * 4 + srcCh];
                    float p10 = buffer[(y0 * sample + x1) * 4 + srcCh];
                    float p01 = buffer[(y1 * sample + x0) * 4 + srcCh];
                    float p11 = buffer[(y1 * sample + x1) * 4 + srcCh];
                    float top = p00 + (p10 - p00) * tx;
                    float bot = p01 + (p11 - p01) * tx;
                    float v = top + (bot - top) * ty;
                    tensor[0, ch, oy, ox] = (v / 255f - EmbedMean[ch]) / EmbedStd[ch];
                }
            }
        }
        return tensor;
    }
}
