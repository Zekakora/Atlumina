using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MyAlbum.Core.Services;

/// <summary>One detected object.</summary>
public sealed record DetectedObject(string Label, float Confidence, double X1, double Y1, double X2, double Y2);

/// <summary>Result of a YOLO detection pass on one photo.</summary>
public sealed record DetectionResult(IReadOnlyList<DetectedObject> Objects);

/// <summary>
/// YOLO11n (COCO 80 classes) object detection via ONNX Runtime. Preprocesses a photo with a
/// 640×640 letterbox, runs the model, then decodes the [1, 84, 8400] output (4 box coords +
/// 80 class scores) into boxes, applies score thresholding + non-max suppression, and returns
/// the top detections. Detections are stored as JSON in Photos.ObjectsJson and surfaced as
/// auto tags ("人", "猫", "车" ...) when written to the library.
/// </summary>
public sealed class ObjectDetectionService
{
    public const string ModelFileName = "yolo11n.onnx";
    public const int InputSize = 640;
    public const float ConfThreshold = 0.25f;
    public const float NmsThreshold = 0.45f;
    public const int MaxDetections = 20;

    private static readonly string[] CocoLabels = CreateCocoLabels();
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
    /// Detects objects in <paramref name="filePath"/>. Returns an empty list when the model is
    /// missing or the photo cannot be decoded.
    /// </summary>
    public IReadOnlyList<DetectedObject> Detect(string filePath)
    {
        var modelPath = InstalledModelPath;
        if (modelPath is null)
        {
            return [];
        }

        // Decode a reasonable working sample (keeps memory bounded for huge RAWs), then letterbox.
        var src = WicGrayscale.GetRgbFixed(filePath, InputSize, InputSize);
        if (src is null)
        {
            return [];
        }
        // GetRgbFixed stretches to 640×640, so the letterbox is already square; the resize
        // is acceptable for detection (YOLO is scale-robust). Build the 1×3×640×640 tensor.
        var tensor = ToTensor(src.Rgb);
        var session = _cache.Get(modelPath);
        using var results = _cache.Run(session, new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("images", tensor),
        });

        var output = results.First().AsTensor<float>();
        return Postprocess(output);
    }

    private static DenseTensor<float> ToTensor(byte[] rgb)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        int count = InputSize * InputSize;
        for (int i = 0; i < count; i++)
        {
            int o = i * 3;
            tensor[0, 0, i / InputSize, i % InputSize] = rgb[o] / 255f;
            tensor[0, 1, i / InputSize, i % InputSize] = rgb[o + 1] / 255f;
            tensor[0, 2, i / InputSize, i % InputSize] = rgb[o + 2] / 255f;
        }
        return tensor;
    }

    /// <summary>
    /// Decodes the [1,84,N] output (cx,cy,w,h + 80 class scores), applies score thresholding,
    /// then non-max suppression. The model was fed a 640×640 (stretched) image, so box
    /// coordinates are already in that space and are returned as-is.
    /// </summary>
    private static IReadOnlyList<DetectedObject> Postprocess(Tensor<float> output)
    {
        // Output layout: [1, 84, anchors]. Flat index = c * anchors + i.
        int anchors = output.Dimensions.Length >= 3 ? output.Dimensions[2] : (int)(output.Length / 84);
        var boxes = new List<(float X1, float Y1, float X2, float Y2, float Score, int Class)>();
        for (int i = 0; i < anchors; i++)
        {
            float cx = output.GetValue(i);
            float cy = output.GetValue(anchors + i);
            float w = output.GetValue(2 * anchors + i);
            float h = output.GetValue(3 * anchors + i);
            int bestClass = -1;
            float bestScore = 0;
            for (int c = 0; c < 80; c++)
            {
                float s = output.GetValue((4 + c) * anchors + i);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestClass = c;
                }
            }
            if (bestScore < ConfThreshold)
            {
                continue;
            }
            float x1 = cx - w / 2f, y1 = cy - h / 2f, x2 = cx + w / 2f, y2 = cy + h / 2f;
            boxes.Add((x1, y1, x2, y2, bestScore, bestClass));
        }

        var kept = NonMaxSuppression(boxes);
        var result = new List<DetectedObject>();
        foreach (var b in kept)
        {
            string label = b.Class >= 0 && b.Class < CocoLabels.Length ? CocoLabels[b.Class] : $"class-{b.Class}";
            result.Add(new DetectedObject(label, b.Score, b.X1, b.Y1, b.X2, b.Y2));
        }
        return result;
    }

    /// <summary>Greedy NMS by score, dropping boxes whose IoU with a kept box exceeds the threshold.</summary>
    private static List<(float X1, float Y1, float X2, float Y2, float Score, int Class)> NonMaxSuppression(
        List<(float X1, float Y1, float X2, float Y2, float Score, int Class)> boxes)
    {
        var ordered = boxes.OrderByDescending(b => b.Score).ToList();
        var kept = new List<(float, float, float, float, float, int)>();
        while (ordered.Count > 0 && kept.Count < MaxDetections)
        {
            var best = ordered[0];
            ordered.RemoveAt(0);
            kept.Add(best);
            ordered = ordered.Where(b => b.Class != best.Class || IoU(best, b) < NmsThreshold).ToList();
        }
        return kept;
    }

    private static float IoU((float X1, float Y1, float X2, float Y2, float Score, int Class) a, (float X1, float Y1, float X2, float Y2, float Score, int Class) b)
    {
        float ix1 = Math.Max(a.X1, b.X1), iy1 = Math.Max(a.Y1, b.Y1);
        float ix2 = Math.Min(a.X2, b.X2), iy2 = Math.Min(a.Y2, b.Y2);
        float iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        float inter = iw * ih;
        float union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>COCO 80 class names (matching YOLOv8/YOLO11 default training).</summary>
    private static string[] CreateCocoLabels() => new[]
    {
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
        "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
        "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
        "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
        "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
        "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
        "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair",
        "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse",
        "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink", "refrigerator",
        "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush",
    };
}
