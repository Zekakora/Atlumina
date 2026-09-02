using System.Net.Http;

namespace MyAlbum.Core.Services;

/// <summary>Metadata describing a downloadable ONNX model plus its label file.</summary>
public sealed record AiModelDefinition(
    string Key,
    string DisplayName,
    string Description,
    string ModelUrl,
    string ModelFileName,
    string? LabelsUrl,
    string? LabelsFileName,
    long ExpectedSizeBytes);

/// <summary>
/// Downloads AI ONNX models (and their label files) into the app's models directory.
/// The model files are intentionally not bundled with the installer; users fetch them
/// on demand. Downloads stream to a temp file so a failed/interrupted transfer never
/// leaves a corrupt .onnx behind.
/// </summary>
public sealed class AiModelDownloader
{
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>MobileNet V2 (ImageNet 1000 classes) — scene/object auto-tagging.</summary>
    public static readonly AiModelDefinition MobileNet = new(
        Key: "mobilenet",
        DisplayName: "MobileNet V2",
        Description: "ImageNet 1000 类场景/物体分类（自动打标签）",
        ModelUrl: "https://github.com/onnx/models/raw/main/validated/vision/classification/mobilenet/model/mobilenetv2-12.onnx",
        ModelFileName: "mobilenetv2.onnx",
        LabelsUrl: "https://raw.githubusercontent.com/onnx/models/main/validated/vision/classification/synset.txt",
        LabelsFileName: "synset.txt",
        ExpectedSizeBytes: 13_964_571);

    /// <summary>YuNet face detector (OpenCV Zoo) — lightweight 640×640 face + landmarks.</summary>
    public static readonly AiModelDefinition YuNet = new(
        Key: "yunet",
        DisplayName: "YuNet 人脸检测",
        Description: "轻量人脸检测（输出人脸框 + 5 点关键点）",
        ModelUrl: "https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx",
        ModelFileName: "yunet.onnx",
        LabelsUrl: null,
        LabelsFileName: null,
        ExpectedSizeBytes: 232_589);

    /// <summary>ArcFace LResNet100E-IR (int8) — 512-dim face embedding for recognition/clustering.</summary>
    public static readonly AiModelDefinition ArcFace = new(
        Key: "arcface",
        DisplayName: "ArcFace 人脸识别",
        Description: "512 维人脸特征（用于聚类成人物相册）",
        ModelUrl: "https://github.com/onnx/models/raw/main/validated/vision/body_analysis/arcface/model/arcfaceresnet100-11-int8.onnx",
        ModelFileName: "arcface.onnx",
        LabelsUrl: null,
        LabelsFileName: null,
        ExpectedSizeBytes: 65_764_892);

    /// <summary>NIMA (MobileNet) — 1-10 aesthetic / technical score for a photo.</summary>
    public static readonly AiModelDefinition Nima = new(
        Key: "nima",
        DisplayName: "NIMA 美学评分",
        Description: "照片美学/技术质量评分（1-10 分）",
        ModelUrl: "https://huggingface.co/cromsc/nima-mobilenet-aesthetic/resolve/main/nima_mobilenet_aesthetic.onnx",
        ModelFileName: "nima.onnx",
        LabelsUrl: null,
        LabelsFileName: null,
        ExpectedSizeBytes: 12_867_270);

    /// <summary>YOLO11n — 80-class COCO object detection (low cost, ~11 MB).</summary>
    public static readonly AiModelDefinition Yolo11n = new(
        Key: "yolo11n",
        DisplayName: "YOLO11n 物体检测",
        Description: "检测照片中的人/动物/车/食物等 80 类物体",
        ModelUrl: "https://github.com/ultralytics/assets/releases/download/v8.3.0/yolo11n.onnx",
        ModelFileName: "yolo11n.onnx",
        LabelsUrl: null,
        LabelsFileName: null,
        ExpectedSizeBytes: 10_930_182);

    /// <summary>MobileCLIP-S2 image encoder — 512-dim visual embedding for semantic search.</summary>
    public static readonly AiModelDefinition ClipVision = new(
        Key: "clip-vision",
        DisplayName: "MobileCLIP-S2 图像编码",
        Description: "把照片编码为 512 维向量（语义搜图/特征嵌入）",
        ModelUrl: "https://huggingface.co/Xenova/mobileclip_s2/resolve/main/onnx/vision_model.onnx",
        ModelFileName: "clip-vision.onnx",
        LabelsUrl: null,
        LabelsFileName: null,
        ExpectedSizeBytes: 143_020_962);

    /// <summary>MobileCLIP-S2 text encoder — encodes a search query into the same 512-dim space.</summary>
    public static readonly AiModelDefinition ClipText = new(
        Key: "clip-text",
        DisplayName: "MobileCLIP-S2 文本编码",
        Description: "把搜索词编码为 512 维向量（与图像向量做余弦相似度）",
        ModelUrl: "https://huggingface.co/Xenova/mobileclip_s2/resolve/main/onnx/text_model.onnx",
        ModelFileName: "clip-text.onnx",
        LabelsUrl: null,
        LabelsFileName: null,
        ExpectedSizeBytes: 253_894_023);

    /// <summary>CLIP BPE tokenizer vocabulary (shared by all CLIP variants).</summary>
    public static readonly AiModelDefinition ClipVocab = new(
        Key: "clip-vocab",
        DisplayName: "CLIP 词表",
        Description: "文本编码所需的 BPE 词表",
        ModelUrl: "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/vocab.json",
        ModelFileName: "clip-vocab.json",
        LabelsUrl: null,
        LabelsFileName: null,
        ExpectedSizeBytes: 862_328);

    /// <summary>CLIP BPE tokenizer merges (shared by all CLIP variants).</summary>
    public static readonly AiModelDefinition ClipMerges = new(
        Key: "clip-merges",
        DisplayName: "CLIP 合并规则",
        Description: "文本编码所需的 BPE 合并规则",
        ModelUrl: "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/merges.txt",
        ModelFileName: "clip-merges.txt",
        LabelsUrl: null,
        LabelsFileName: null,
        ExpectedSizeBytes: 524_619);

    /// <summary>Known downloadable models.</summary>
    public static readonly IReadOnlyList<AiModelDefinition> Catalog = new List<AiModelDefinition>
    {
        MobileNet,
        YuNet,
        ArcFace,
        Nima,
        Yolo11n,
        ClipVision,
        ClipText,
        ClipVocab,
        ClipMerges,
    };

    /// <summary>True when the full MobileCLIP semantic-search stack (image + text + tokenizer) is installed.</summary>
    public static bool IsClipInstalled =>
        IsInstalled(ClipVision) && IsInstalled(ClipText) && IsInstalled(ClipVocab) && IsInstalled(ClipMerges);

    public AiModelDownloader()
    {
    }

    /// <summary>
    /// Downloads <paramref name="model"/> (and its labels) into <see cref="AiEngine.ModelsDirectory"/>,
    /// reporting (receivedBytes, totalBytes, fileName) via <paramref name="progress"/>. Returns the
    /// final model path, or null when cancelled / failed.
    /// </summary>
    public async Task<string?> DownloadAsync(
        AiModelDefinition model,
        IProgress<(long Received, long Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(AiEngine.ModelsDirectory);

        var modelPath = Path.Combine(AiEngine.ModelsDirectory, model.ModelFileName);
        bool ok = await DownloadFileAsync(model.ModelUrl, modelPath, model.ExpectedSizeBytes, progress, ct).ConfigureAwait(false);
        if (!ok)
        {
            return null;
        }

        if (model.LabelsUrl is not null && model.LabelsFileName is not null)
        {
            var labelsPath = Path.Combine(AiEngine.ModelsDirectory, model.LabelsFileName);
            await DownloadFileAsync(model.LabelsUrl, labelsPath, null, progress, ct).ConfigureAwait(false);
        }

        return File.Exists(modelPath) ? modelPath : null;
    }

    /// <summary>True when the model and (if any) its label file are already present.</summary>
    public static bool IsInstalled(AiModelDefinition model)
    {
        var modelPath = Path.Combine(AiEngine.ModelsDirectory, model.ModelFileName);
        if (!File.Exists(modelPath))
        {
            return false;
        }
        if (model.LabelsFileName is not null)
        {
            var labelsPath = Path.Combine(AiEngine.ModelsDirectory, model.LabelsFileName);
            if (!File.Exists(labelsPath))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<bool> DownloadFileAsync(
        string url,
        string destinationPath,
        long? expectedSizeBytes,
        IProgress<(long, long, string)>? progress,
        CancellationToken ct)
    {
        var tempPath = destinationPath + ".part";
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? expectedSizeBytes ?? 0;

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(tempPath))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    progress?.Report((received, total, Path.GetFileName(destinationPath)));
                }
                await target.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            return false;
        }
        catch (Exception)
        {
            TryDelete(tempPath);
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler();
        // Respect system proxy (e.g. a local Clash instance) so downloads work behind a proxy.
        handler.UseDefaultCredentials = true;
        handler.UseProxy = true;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Atlumina/0.1");
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
