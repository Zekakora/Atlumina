using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MyAlbum.Core.Data;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;
using MyAlbum_App.Services;

namespace MyAlbum_App.ViewModels;

/// <summary>A single photo listed by the AI page (blurry photos or a similar-photo group).</summary>
public partial class AiPhotoItem : ObservableObject
{
    public long Id { get; }
    public string FileName { get; }
    public string? DirectoryPath { get; }
    public BitmapImage? Thumbnail { get; }

    [ObservableProperty]
    public partial double BlurScore { get; set; }

    [ObservableProperty]
    public partial string BlurText { get; set; } = "";

    public AiPhotoItem(PhotoRecord photo, string? thumbnailPath, double? blurScore)
    {
        Id = photo.Id;
        FileName = photo.FileName;
        DirectoryPath = photo.DirectoryPath;
        BlurScore = blurScore ?? 0;
        BlurText = blurScore is { } b ? $"锐度 {b:0}" : "";
        if (thumbnailPath is not null && File.Exists(thumbnailPath))
        {
            Thumbnail = new BitmapImage(new Uri(thumbnailPath));
        }
    }
}

/// <summary>A group of visually similar photos shown on the AI page.</summary>
public partial class AiSimilarGroup : ObservableObject
{
    public string Title { get; }
    public string Detail { get; }
    public ObservableCollection<AiPhotoItem> Photos { get; } = new();

    public AiSimilarGroup(string title, string detail)
    {
        Title = title;
        Detail = detail;
    }
}

/// <summary>
/// Drives the AI page: reports the probed compute device, runs the CPU vision-analysis
/// pass (pHash + Laplacian blur), and surfaces the results (blurry photos, similar groups).
/// Also manages the ONNX model download and MobileNet scene auto-tagging. All heavy work
/// runs on background tasks; the UI only receives throttled progress.
/// </summary>
public partial class AiViewModel : ObservableObject
{
    private readonly PhotoDatabase _db;
    private readonly VisionAnalysisService _vision;
    private readonly DuplicateService _dupes;
    private readonly SceneTaggerService _tagger;
    private readonly FaceClusteringService _faceClustering;
    private readonly HomeViewModel _home;
    private readonly DeepAnalysisService _deep;
    private readonly ClipService _clip;
    private readonly GpsPlaceService _placeService;
    private readonly AddressNormalizeService _addressNormalizer;
    private readonly AppState _appState;
    private CancellationTokenSource? _analyzeCts;
    private CancellationTokenSource? _tagCts;
    private CancellationTokenSource? _faceCts;
    private CancellationTokenSource? _deepCts;
    private CancellationTokenSource? _placeCts;
    private CancellationTokenSource? _addressCts;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string DeviceText { get; set; } = "";

    [ObservableProperty]
    public partial string DeviceDetail { get; set; } = "";

    [ObservableProperty]
    public partial string ModelsText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsTagging { get; set; }

    [ObservableProperty]
    public partial bool IsClearingAiTags { get; set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial bool IsDownloadingFaceModels { get; set; }

    [ObservableProperty]
    public partial bool IsFaceAnalyzing { get; set; }

    [ObservableProperty]
    public partial string TagStatusText { get; set; } = "";

    [ObservableProperty]
    public partial string TagProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string FaceStatusText { get; set; } = "";

    [ObservableProperty]
    public partial string FaceProgressText { get; set; } = "";

    [ObservableProperty]
    public partial int PersonGroupCount { get; set; }

    // ---- Deep analysis (color / aesthetic / embedding / objects / semantic search) ----

    [ObservableProperty]
    public partial bool IsDeepAnalyzing { get; set; }

    [ObservableProperty]
    public partial double DeepProgress { get; set; }

    [ObservableProperty]
    public partial string DeepProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string DeepStatusText { get; set; } = "";

    [ObservableProperty]
    public partial int DeepAnalyzedCount { get; set; }

    [ObservableProperty]
    public partial int LowAestheticCount { get; set; }

    [ObservableProperty]
    public partial int MonoCount { get; set; }

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = "";

    [ObservableProperty]
    public partial string SearchStatusText { get; set; } = "";

    /// <summary>True when the MobileCLIP semantic-search stack is installed.</summary>
    public bool IsClipInstalled => ClipService.IsInstalled;

    /// <summary>Display text for the semantic-search model status.</summary>
    public string ClipInstallText => IsClipInstalled ? "已安装" : "未安装（约 400MB）";

    /// <summary>Display text for the deep-analysis coverage.</summary>
    public string DeepAnalyzedText => $"已分析 {DeepAnalyzedCount} 张 · 低分（无意义）{LowAestheticCount} · 黑白 {MonoCount}";

    partial void OnDeepAnalyzedCountChanged(int value) => OnPropertyChanged(nameof(DeepAnalyzedText));
    partial void OnLowAestheticCountChanged(int value) => OnPropertyChanged(nameof(DeepAnalyzedText));
    partial void OnMonoCountChanged(int value) => OnPropertyChanged(nameof(DeepAnalyzedText));

    // ---- GPS 位置回填（离线城市 + 在线反向地理编码）----

    [ObservableProperty]
    public partial bool IsPlaceBackfilling { get; set; }

    [ObservableProperty]
    public partial bool IsResettingPlaces { get; set; }

    [ObservableProperty]
    public partial double PlaceProgress { get; set; }

    [ObservableProperty]
    public partial string PlaceProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string PlaceStatusText { get; set; } = "";

    [ObservableProperty]
    public partial int PlaceResolvedCount { get; set; }

    [ObservableProperty]
    public partial int PlaceOsmCount { get; set; }

    [ObservableProperty]
    public partial int PlaceAmapCount { get; set; }

    [ObservableProperty]
    public partial int PlaceReuseCount { get; set; }

    /// <summary>Display text for the place backfill coverage (with per-source breakdown).</summary>
    public string PlaceCoverageText
    {
        get
        {
            var parts = new List<string>();
            if (PlaceOsmCount > 0) parts.Add($"OSM {PlaceOsmCount}");
            if (PlaceAmapCount > 0) parts.Add($"高德 {PlaceAmapCount}");
            if (PlaceReuseCount > 0) parts.Add($"邻近复用 {PlaceReuseCount}");
            string detail = parts.Count == 0 ? "" : "（" + string.Join(" · ", parts) + "）";
            return $"已解析位置 {PlaceResolvedCount} 张{detail}；500 米内有已解析照片则直接复用（不耗流量），切换来源后增量重试、不覆盖已解析结果";
        }
    }

    partial void OnPlaceResolvedCountChanged(int value) => OnPropertyChanged(nameof(PlaceCoverageText));
    partial void OnPlaceOsmCountChanged(int value) => OnPropertyChanged(nameof(PlaceCoverageText));
    partial void OnPlaceAmapCountChanged(int value) => OnPropertyChanged(nameof(PlaceCoverageText));
    partial void OnPlaceReuseCountChanged(int value) => OnPropertyChanged(nameof(PlaceCoverageText));

    // ---- 地址规范化（LLM 把 GpsPlace 规范为五级结构）----

    [ObservableProperty]
    public partial bool IsNormalizingAddresses { get; set; }

    [ObservableProperty]
    public partial double NormalizeProgress { get; set; }

    [ObservableProperty]
    public partial string NormalizeProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string NormalizeStatusText { get; set; } = "";

    [ObservableProperty]
    public partial int AddressNormalizedCount { get; set; }

    public string AddressCoverageText => $"已规范地址 {AddressNormalizedCount} 张（大语言模型按「国家-省-市-区县-地标」结构化）";

    partial void OnAddressNormalizedCountChanged(int value) => OnPropertyChanged(nameof(AddressCoverageText));

    /// <summary>Photos the last normalization pass could not resolve (model returned nothing / empty).</summary>
    public IReadOnlyList<SkippedAddress> SkippedAddresses { get; private set; } = Array.Empty<SkippedAddress>();

    /// <summary>True when the last pass left some photos un-normalized.</summary>
    public bool HasSkippedAddresses => SkippedAddresses.Count > 0;

    /// <summary>True when the MobileNet model + labels are installed.</summary>
    public bool IsModelInstalled => AiModelDownloader.IsInstalled(AiModelDownloader.MobileNet);

    /// <summary>True when both face models (YuNet + ArcFace) are installed.</summary>
    public bool IsFaceModelInstalled => FaceService.IsInstalled;

    /// <summary>Display text under the scene-tagging section describing model status.</summary>
    public string ModelInstallText => IsModelInstalled ? "已安装" : "未安装（约 14MB）";

    /// <summary>Display text under the face section describing model status.</summary>
    public string FaceModelInstallText => IsFaceModelInstalled ? "已安装" : "未安装（约 64MB）";

    /// <summary>Display text for the person-group count.</summary>
    public string PersonGroupCountText => PersonGroupCount == 0 ? "尚未生成人物分组" : $"已识别 {PersonGroupCount} 个不同人物";

    [ObservableProperty]
    public partial string AnalyzedText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasResults { get; set; }

    [ObservableProperty]
    public partial int BlurryCount { get; set; }

    [ObservableProperty]
    public partial int SimilarGroupCount { get; set; }

    // Expand / collapse toggles for the image-display cards on the AI page.
    // Persisted in AppState (settings.json) so the state survives restarts.
    public bool IsSceneExpanded
    {
        get => _appState.IsSceneExpanded;
        set
        {
            if (_appState.IsSceneExpanded != value)
            {
                _appState.IsSceneExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsFaceExpanded
    {
        get => _appState.IsFaceExpanded;
        set
        {
            if (_appState.IsFaceExpanded != value)
            {
                _appState.IsFaceExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsBlurryExpanded
    {
        get => _appState.IsBlurryExpanded;
        set
        {
            if (_appState.IsBlurryExpanded != value)
            {
                _appState.IsBlurryExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsSimilarExpanded
    {
        get => _appState.IsSimilarExpanded;
        set
        {
            if (_appState.IsSimilarExpanded != value)
            {
                _appState.IsSimilarExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Display text for the blurry-photo count.</summary>
    public string BlurryCountText => BlurryCount == 0 ? "未发现明显模糊照片" : $"共 {BlurryCount} 张可能模糊（锐度 ≤ {VisionAnalysisService.BlurThreshold:0}）";

    /// <summary>Display text for the similar-group count.</summary>
    public string SimilarGroupCountText => SimilarGroupCount == 0 ? "未发现近重复照片" : $"共 {SimilarGroupCount} 组近重复";

    partial void OnBlurryCountChanged(int value) => OnPropertyChanged(nameof(BlurryCountText));
    partial void OnSimilarGroupCountChanged(int value) => OnPropertyChanged(nameof(SimilarGroupCountText));

    public ObservableCollection<AiPhotoItem> BlurryPhotos { get; } = new();
    public ObservableCollection<AiSimilarGroup> SimilarGroups { get; } = new();
    public ObservableCollection<AiPhotoItem> SearchResults { get; } = new();

    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand DownloadModelCommand { get; }
    public IAsyncRelayCommand DownloadFaceModelsCommand { get; }
    public IAsyncRelayCommand TagCommand { get; }
    public IRelayCommand CancelTagCommand { get; }
    public IAsyncRelayCommand AnalyzeFacesCommand { get; }
    public IRelayCommand CancelFacesCommand { get; }
    public IAsyncRelayCommand ClearAiTagsCommand { get; }
    public IAsyncRelayCommand DeepAnalyzeCommand { get; }
    public IRelayCommand CancelDeepCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand DownloadClipCommand { get; }
    public IAsyncRelayCommand BackfillPlacesCommand { get; }
    public IRelayCommand CancelPlaceCommand { get; }
    public IAsyncRelayCommand ResetPlacesCommand { get; }
    public IAsyncRelayCommand NormalizeAddressesCommand { get; }
    public IRelayCommand CancelNormalizeCommand { get; }

    public AiViewModel(PhotoDatabase db, VisionAnalysisService vision, DuplicateService dupes, SceneTaggerService tagger, FaceClusteringService faceClustering, HomeViewModel home, AppState appState, DeepAnalysisService deep, ClipService clip, GpsPlaceService placeService, AddressNormalizeService addressNormalizer)
    {
        _db = db;
        _vision = vision;
        _dupes = dupes;
        _tagger = tagger;
        _faceClustering = faceClustering;
        _home = home;
        _appState = appState;
        _deep = deep;
        _clip = clip;
        _placeService = placeService;
        _addressNormalizer = addressNormalizer;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _analyzeCts?.Cancel(), () => IsBusy);
        DownloadModelCommand = new AsyncRelayCommand(DownloadModelAsync, () => !IsDownloading && !IsModelInstalled);
        DownloadFaceModelsCommand = new AsyncRelayCommand(DownloadFaceModelsAsync, () => !IsDownloadingFaceModels && !IsFaceModelInstalled);
        TagCommand = new AsyncRelayCommand(TagLibraryAsync, () => !IsTagging && IsModelInstalled);
        CancelTagCommand = new RelayCommand(() => _tagCts?.Cancel(), () => IsTagging);
        AnalyzeFacesCommand = new AsyncRelayCommand(AnalyzeFacesAsync, () => !IsFaceAnalyzing && IsFaceModelInstalled);
        CancelFacesCommand = new RelayCommand(() => _faceCts?.Cancel(), () => IsFaceAnalyzing);
        ClearAiTagsCommand = new AsyncRelayCommand(ClearAiTagsAsync, () => !IsClearingAiTags);
        DeepAnalyzeCommand = new AsyncRelayCommand(DeepAnalyzeAsync, () => !IsDeepAnalyzing);
        CancelDeepCommand = new RelayCommand(() => _deepCts?.Cancel(), () => IsDeepAnalyzing);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsSearching && IsClipInstalled);
        DownloadClipCommand = new AsyncRelayCommand(DownloadClipAsync, () => !IsDownloading && !IsClipInstalled);
        BackfillPlacesCommand = new AsyncRelayCommand(BackfillPlacesAsync, () => !IsPlaceBackfilling);
        CancelPlaceCommand = new RelayCommand(() => _placeCts?.Cancel(), () => IsPlaceBackfilling);
        ResetPlacesCommand = new AsyncRelayCommand(ResetPlacesAsync, () => !IsResettingPlaces);
        NormalizeAddressesCommand = new AsyncRelayCommand(NormalizeAddressesAsync, () => !IsNormalizingAddresses);
        CancelNormalizeCommand = new RelayCommand(() => _addressCts?.Cancel(), () => IsNormalizingAddresses);
    }

    partial void OnIsBusyChanged(bool value)
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDownloadingChanged(bool value) => DownloadModelCommand.NotifyCanExecuteChanged();
    partial void OnIsDownloadingFaceModelsChanged(bool value) => DownloadFaceModelsCommand.NotifyCanExecuteChanged();
    partial void OnIsTaggingChanged(bool value)
    {
        TagCommand.NotifyCanExecuteChanged();
        CancelTagCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsClearingAiTagsChanged(bool value) => ClearAiTagsCommand.NotifyCanExecuteChanged();

    partial void OnIsFaceAnalyzingChanged(bool value)
    {
        AnalyzeFacesCommand.NotifyCanExecuteChanged();
        CancelFacesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDeepAnalyzingChanged(bool value)
    {
        DeepAnalyzeCommand.NotifyCanExecuteChanged();
        CancelDeepCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSearchingChanged(bool value) => SearchCommand.NotifyCanExecuteChanged();

    partial void OnIsPlaceBackfillingChanged(bool value)
    {
        BackfillPlacesCommand.NotifyCanExecuteChanged();
        CancelPlaceCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsResettingPlacesChanged(bool value) => ResetPlacesCommand.NotifyCanExecuteChanged();

    partial void OnIsNormalizingAddressesChanged(bool value)
    {
        NormalizeAddressesCommand.NotifyCanExecuteChanged();
        CancelNormalizeCommand.NotifyCanExecuteChanged();
    }

    partial void OnPersonGroupCountChanged(int value) => OnPropertyChanged(nameof(PersonGroupCountText));

    /// <summary>Probes the machine and refreshes the device status (no analysis).</summary>
    public void RefreshDeviceInfo()
    {
        var probe = AiEngine.Probe();
        DeviceText = $"计算设备：{probe.BestName}";
        var adapters = AiEngine.EnumerateAdapters();
        DeviceDetail = adapters.Count == 0
            ? "DirectML 可用：" + (AiEngine.IsDirectMlAvailable ? "是" : "否")
            : "DirectML 可用：" + (AiEngine.IsDirectMlAvailable ? "是" : "否") +
              "；适配器：" + string.Join("、", adapters.Where(a => !a.IsSoftware).Select(a => (a.IsNpu ? "NPU " : "") + a.Name));

        var models = AiEngine.DiscoverModels();
        ModelsText = IsModelInstalled
            ? "MobileNet V2 已就绪（场景/物体自动打标）"
            : "未发现 ONNX 模型（可点击「下载 MobileNet 模型」）";
        OnPropertyChanged(nameof(IsModelInstalled));
        OnPropertyChanged(nameof(IsFaceModelInstalled));
        OnPropertyChanged(nameof(ModelInstallText));
        OnPropertyChanged(nameof(FaceModelInstallText));
        OnPropertyChanged(nameof(IsClipInstalled));
        OnPropertyChanged(nameof(ClipInstallText));
        TagCommand.NotifyCanExecuteChanged();
        AnalyzeFacesCommand.NotifyCanExecuteChanged();
        DownloadModelCommand.NotifyCanExecuteChanged();
        DownloadFaceModelsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Downloads YuNet + ArcFace (the two face models) sequentially.</summary>
    public async Task DownloadFaceModelsAsync()
    {
        IsDownloadingFaceModels = true;
        StatusText = "正在下载人脸模型…";
        try
        {
            var downloader = new AiModelDownloader();
            foreach (var model in new[] { AiModelDownloader.YuNet, AiModelDownloader.ArcFace })
            {
                if (AiModelDownloader.IsInstalled(model))
                {
                    continue;
                }
                var progress = new Progress<(long Received, long Total, string File)>(p =>
                {
                    TagProgressText = p.Total > 0
                        ? $"{p.File} {p.Received / 1048576.0:0.0} / {p.Total / 1048576.0:0.0} MB"
                        : $"{p.File} {p.Received / 1048576.0:0.0} MB";
                });
                var path = await downloader.DownloadAsync(model, progress);
                if (path is null)
                {
                    StatusText = $"{model.DisplayName} 下载失败（请检查网络/代理）";
                    return;
                }
            }
            StatusText = "人脸模型已就绪";
            RefreshDeviceInfo();
        }
        finally
        {
            IsDownloadingFaceModels = false;
        }
    }

    /// <summary>Runs YuNet + ArcFace over the library and clusters faces into people.</summary>
    public async Task AnalyzeFacesAsync()
    {
        if (!IsFaceModelInstalled)
        {
            FaceStatusText = "请先安装 YuNet + ArcFace 模型";
            return;
        }
        _faceCts?.Dispose();
        _faceCts = new CancellationTokenSource();
        var ct = _faceCts.Token;

        IsFaceAnalyzing = true;
        FaceStatusText = "开始人脸分析…";
        FaceProgressText = "";
        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                FaceProgressText = p.Total == 0 ? "" : $"已分析 {p.Done}/{p.Total}";
            });
            // Incremental: skip photos already analyzed, so existing faces (and their names)
            // are preserved while only new photos get scanned. Clustering then reassigns
            // PersonIds but propagates names across each cluster.
            var (facesStored, _) = await _faceClustering.AnalyzeLibraryAsync(incremental: true, progress, ct);
            var clusters = await _faceClustering.ClusterAsync(ct);
            PersonGroupCount = clusters.Count;
            FaceStatusText = ct.IsCancellationRequested
                ? "人脸分析已取消"
                : $"人脸分析完成：新增 {facesStored} 张脸，识别 {clusters.Count} 个不同人物";
        }
        catch (OperationCanceledException)
        {
            FaceStatusText = "人脸分析已取消";
        }
        finally
        {
            IsFaceAnalyzing = false;
        }
    }

    /// <summary>Downloads the MobileNet model + labels with progress.</summary>
    public async Task DownloadModelAsync()
    {
        IsDownloading = true;
        StatusText = "正在下载 MobileNet 模型…";
        try
        {
            var progress = new Progress<(long Received, long Total, string File)>(p =>
            {
                TagProgressText = p.Total > 0
                    ? $"{p.File} {p.Received / 1048576.0:0.0} / {p.Total / 1048576.0:0.0} MB"
                    : $"{p.File} {p.Received / 1048576.0:0.0} MB";
            });
            var downloader = new AiModelDownloader();
            var path = await downloader.DownloadAsync(AiModelDownloader.MobileNet, progress);
            if (path is null)
            {
                StatusText = "模型下载失败（请检查网络/代理）";
            }
            else
            {
                StatusText = "MobileNet 模型已就绪";
                RefreshDeviceInfo();
            }
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Runs MobileNet scene auto-tagging over all photos with progress.</summary>
    public async Task TagLibraryAsync()
    {
        if (!IsModelInstalled)
        {
            StatusText = "请先下载 MobileNet 模型";
            return;
        }
        _tagCts?.Dispose();
        _tagCts = new CancellationTokenSource();
        var ct = _tagCts.Token;

        IsTagging = true;
        TagStatusText = "开始分类…";
        TagProgressText = "";
        try
        {
            var photos = await Task.Run(() => _db.GetPhotosWithoutAutoTagsAsync(int.MaxValue).GetAwaiter().GetResult());
            if (photos.Count == 0)
            {
                TagStatusText = "所有照片都已打上 AI 标签";
                return;
            }
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                TagProgressText = p.Total == 0 ? "" : $"已分类 {p.Done}/{p.Total}";
            });
            var (tagged, failed) = await _tagger.TagLibraryAsync(photos, progress, ct);
            TagStatusText = ct.IsCancellationRequested
                ? "自动打标已取消"
                : failed > 0
                    ? $"自动打标完成：成功 {tagged}，失败 {failed}"
                    : $"自动打标完成：共 {tagged} 张";
        }
        catch (OperationCanceledException)
        {
            TagStatusText = "自动打标已取消";
        }
        finally
        {
            IsTagging = false;
        }
    }

    /// <summary>Deletes every AI scene/object tag so photos can be re-tagged from scratch.</summary>
    public async Task ClearAiTagsAsync()
    {
        IsClearingAiTags = true;
        TagStatusText = "正在删除全部 AI 标签…";
        try
        {
            await _db.DeleteAllAutoTagsAsync();
            TagStatusText = "已删除全部 AI 标签，可重新打标";
            await _home.RefreshAsync();
        }
        finally
        {
            IsClearingAiTags = false;
        }
    }

    /// <summary>Runs the analysis pass over all un-analyzed photos and refreshes results.</summary>
    public async Task AnalyzeAsync()
    {
        _analyzeCts?.Dispose();
        _analyzeCts = new CancellationTokenSource();
        var ct = _analyzeCts.Token;

        IsBusy = true;
        Progress = 0;
        StatusText = "开始分析…";
        ProgressText = "";
        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                Progress = p.Total == 0 ? 0 : p.Done / (double)p.Total;
                ProgressText = $"已分析 {p.Done}/{p.Total}";
            });
            var result = await _vision.AnalyzeLibraryAsync(progress, ct);
            StatusText = ct.IsCancellationRequested
                ? "分析已取消"
                : result.Failed > 0
                    ? $"分析完成：成功 {result.Analyzed}，失败 {result.Failed}"
                    : $"分析完成：共 {result.Analyzed} 张";
            if (!ct.IsCancellationRequested)
            {
                await LoadResultsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "分析已取消";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Loads blurry photos and similar-photo groups from the index.</summary>
    public async Task LoadResultsAsync()
    {
        long analyzed = await _db.CountAnalyzedPhotosAsync();
        long total = await _db.GetPhotoCountAsync();
        AnalyzedText = $"已分析 {analyzed}/{total} 张";

        // Blurry photos (lowest Laplacian variance first).
        var blurry = await _db.GetBlurryPhotosAsync(VisionAnalysisService.BlurThreshold, 200);
        BlurryCount = blurry.Count;
        BlurryPhotos.Clear();
        foreach (var p in blurry)
        {
            var thumb = p.ThumbnailCachePath is not null && File.Exists(p.ThumbnailCachePath)
                ? p.ThumbnailCachePath
                : null;
            BlurryPhotos.Add(new AiPhotoItem(p, thumb, p.BlurScore));
        }

        // Similar-photo groups from stored pHash (DuplicateService reads them from the DB).
        // FindDuplicates runs on a background thread: photos without a stored content hash
        // are fully read to compute SHA-256, which must never block the UI thread.
        var all = await _db.GetPhotosAsync(10000);
        var groups = await Task.Run(() =>
            _dupes.FindDuplicates(all)
                .Where(g => !g.IsExact)
                .OrderByDescending(g => g.Photos.Count)
                .Take(50)
                .ToList());
        SimilarGroupCount = groups.Count;
        SimilarGroups.Clear();
        int groupIndex = 0;
        foreach (var g in groups)
        {
            groupIndex++;
            var keep = g.KeepPaths is { Count: > 0 }
                ? string.Join(" + ", g.KeepPaths.Select(Path.GetFileName))
                : (g.SuggestedKeepPath is null ? "" : Path.GetFileName(g.SuggestedKeepPath));
            var group = new AiSimilarGroup(
                $"相似组 {groupIndex}（{g.Photos.Count} 张）",
                $"pHash 距离 {g.PhashDistance}；建议保留 {keep}");
            foreach (var p in g.Photos.OrderByDescending(pp => (pp.Width ?? 0) * (long)(pp.Height ?? 0)))
            {
                var thumb = p.ThumbnailCachePath is not null && File.Exists(p.ThumbnailCachePath)
                    ? p.ThumbnailCachePath
                    : null;
                group.Photos.Add(new AiPhotoItem(p, thumb, p.BlurScore));
            }
            SimilarGroups.Add(group);
        }

        HasResults = true;
    }

    // ---------- Deep analysis (color / aesthetic / embedding / objects) ----------

    /// <summary>Runs the deep-analysis pass (CPU color + NIMA + MobileNet embedding + YOLO + optional CLIP).</summary>
    public async Task DeepAnalyzeAsync()
    {
        _deepCts?.Dispose();
        _deepCts = new CancellationTokenSource();
        var ct = _deepCts.Token;

        IsDeepAnalyzing = true;
        DeepProgress = 0;
        DeepStatusText = "开始深度分析…";
        DeepProgressText = "";
        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                DeepProgress = p.Total == 0 ? 0 : p.Done / (double)p.Total;
                DeepProgressText = $"已分析 {p.Done}/{p.Total}";
            });
            var result = await _deep.AnalyzeLibraryAsync(includeClip: true, progress, ct);
            await LoadDeepResultsAsync();
            DeepStatusText = ct.IsCancellationRequested
                ? "深度分析已取消"
                : $"深度分析完成：成功 {result.Analyzed}，失败 {result.Failed}";
        }
        catch (OperationCanceledException)
        {
            DeepStatusText = "深度分析已取消";
        }
        finally
        {
            IsDeepAnalyzing = false;
        }
    }

    /// <summary>Loads the deep-analysis summary (coverage / low-score / mono counts).</summary>
    public async Task LoadDeepResultsAsync()
    {
        DeepAnalyzedCount = (int)await _db.CountDeepAnalyzedPhotosAsync();
        var low = await _db.GetLowAestheticPhotosAsync(DeepAnalysisService.LowAestheticThreshold, int.MaxValue);
        LowAestheticCount = low.Count;
        var mono = await _db.GetMonoPhotosAsync(int.MaxValue);
        MonoCount = mono.Count;
    }

    /// <summary>Loads the GPS-place coverage count (how many photos already have a place name, by source).</summary>
    public async Task LoadPlaceCoverageAsync()
    {
        PlaceResolvedCount = (int)await _db.CountGpsPhotosWithPlaceAsync();
        var bySource = await _db.CountGpsPhotosBySourceAsync();
        PlaceOsmCount = (int)bySource.GetValueOrDefault("osm");
        PlaceAmapCount = (int)bySource.GetValueOrDefault("amap");
        PlaceReuseCount = (int)bySource.GetValueOrDefault("reuse");
    }

    /// <summary>
    /// Background pass that reverse-geocodes every GPS photo lacking a stored place name.
    /// Offline city hits are instant; others hit the OSM Nominatim API (throttled 1 req/s).
    /// </summary>
    public async Task BackfillPlacesAsync()
    {
        _placeCts?.Dispose();
        _placeCts = new CancellationTokenSource();
        var ct = _placeCts.Token;

        IsPlaceBackfilling = true;
        PlaceProgress = 0;
        PlaceStatusText = "正在解析 GPS 位置…";
        PlaceProgressText = "";
        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                PlaceProgress = p.Total == 0 ? 0 : p.Done / (double)p.Total;
                PlaceProgressText = $"已解析 {p.Done}/{p.Total}";
            });
            var result = await _placeService.BackfillAsync(progress, ct);
            await LoadPlaceCoverageAsync();
            PlaceStatusText = ct.IsCancellationRequested
                ? $"位置解析已取消：已保存当前已完成的 {PlaceResolvedCount} 张解析结果。"
                : result.Total == 0
                    ? "所有 GPS 照片都已解析位置。"
                    : $"位置解析完成：成功 {result.Resolved}，跳过 {result.Skipped}";
        }
        catch (OperationCanceledException)
        {
            PlaceStatusText = "位置解析已取消";
        }
        catch (Exception ex)
        {
            PlaceStatusText = "位置解析失败：" + ex.Message;
        }
        finally
        {
            IsPlaceBackfilling = false;
        }
    }

    /// <summary>
    /// Clears every derived location field for all photos (reverse-geocoded place + normalized
    /// address + failed markers), keeping the raw GPS, so the backfill / normalize passes can
    /// be re-run cleanly (e.g. after switching the geocode source or fixing the reuse ratio).
    /// </summary>
    public async Task ResetPlacesAsync()
    {
        IsResettingPlaces = true;
        PlaceStatusText = "正在重置位置信息…";
        try
        {
            await _db.ResetAllPlacesAsync();
            await LoadPlaceCoverageAsync();
            await LoadAddressCoverageAsync();
            PlaceStatusText = "已清空全部解析位置与规范地址，可重新运行解析与规范。";
            await _home.RefreshAsync();
        }
        catch (Exception ex)
        {
            PlaceStatusText = "重置失败：" + ex.Message;
        }
        finally
        {
            IsResettingPlaces = false;
        }
    }

    /// <summary>Loads how many photos already have a normalized (LLM) address.</summary>
    public async Task LoadAddressCoverageAsync()
    {
        AddressNormalizedCount = (int)await _db.CountPhotosWithNormalizedAddressAsync();
    }

    /// <summary>
    /// Background pass that normalizes every reverse-geocoded place name into the five-level
    /// address via the configured LLM (batched). Needs LLM config in 设置 → 大语言模型.
    /// </summary>
    public async Task NormalizeAddressesAsync()
    {
        if (!LlmConfig.IsConfigured)
        {
            NormalizeStatusText = "未配置大语言模型：请在「设置 → 大语言模型」填写 API 密钥。";
            return;
        }
        _addressCts?.Dispose();
        _addressCts = new CancellationTokenSource();
        var ct = _addressCts.Token;

        IsNormalizingAddresses = true;
        NormalizeProgress = 0;
        NormalizeStatusText = "正在规范地址…";
        NormalizeProgressText = "";
        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                NormalizeProgress = p.Total == 0 ? 0 : p.Done / (double)p.Total;
                NormalizeProgressText = string.IsNullOrWhiteSpace(p.File)
                    ? $"已处理 {p.Done}/{p.Total}"
                    : p.File;
            });
            var result = await _addressNormalizer.NormalizePendingAsync(progress, ct);
            SkippedAddresses = result.SkippedItems;
            OnPropertyChanged(nameof(SkippedAddresses));
            OnPropertyChanged(nameof(HasSkippedAddresses));
            // Pre-warm the cached home-page photo records so the right-panel location shows
            // immediately when the user navigates back (the home page is cached and won't reload).
            await _home.RefreshPlaceAddressesAsync();
            await LoadAddressCoverageAsync();
            NormalizeStatusText = ct.IsCancellationRequested
                ? $"地址规范化已取消：已保存当前已完成的 {AddressNormalizedCount} 张规范结果。"
                : result.Total == 0
                    ? "所有已解析位置的照片都已规范化。"
                    : result.FailedBatches > 0
                        ? $"地址规范化完成：成功 {result.Resolved}，跳过 {result.Skipped}；失败 {result.FailedBatches} 批将在下次运行自动续跑。" +
                          (result.LastError is { } e ? $" 原因：{e}" : "")
                        : $"地址规范化完成：成功 {result.Resolved}，跳过 {result.Skipped}。";
        }
        catch (OperationCanceledException)
        {
            NormalizeStatusText = "地址规范化已取消";
        }
        catch (Exception ex)
        {
            NormalizeStatusText = "地址规范化失败：" + ex.Message;
        }
        finally
        {
            IsNormalizingAddresses = false;
        }
    }

    /// <summary>Downloads the MobileCLIP semantic-search stack (image + text + tokenizer files).</summary>
    public async Task DownloadClipAsync()
    {
        IsDownloading = true;
        DeepStatusText = "正在下载 MobileCLIP 模型…";
        try
        {
            var downloader = new AiModelDownloader();
            foreach (var model in new[] { AiModelDownloader.ClipVision, AiModelDownloader.ClipText, AiModelDownloader.ClipVocab, AiModelDownloader.ClipMerges })
            {
                if (AiModelDownloader.IsInstalled(model))
                {
                    continue;
                }
                var progress = new Progress<(long Received, long Total, string File)>(p =>
                {
                    DeepProgressText = p.Total > 0
                        ? $"{p.File} {p.Received / 1048576.0:0.0} / {p.Total / 1048576.0:0.0} MB"
                        : $"{p.File} {p.Received / 1048576.0:0.0} MB";
                });
                var path = await downloader.DownloadAsync(model, progress);
                if (path is null)
                {
                    DeepStatusText = $"{model.DisplayName} 下载失败（请检查网络/代理）";
                    return;
                }
            }
            DeepStatusText = "MobileCLIP 模型已就绪";
            RefreshDeviceInfo();
            SearchCommand.NotifyCanExecuteChanged();
            DownloadClipCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Semantic search: encodes the query and returns the top similar photos by cosine.</summary>
    public async Task SearchAsync()
    {
        if (!IsClipInstalled)
        {
            SearchStatusText = "请先下载 MobileCLIP 模型";
            return;
        }
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchStatusText = "请输入搜索词，如「海边的狗」或「red car」";
            return;
        }

        IsSearching = true;
        SearchStatusText = "正在搜索…";
        SearchResults.Clear();
        try
        {
            var queryVec = await Task.Run(() => _clip.EmbedText(SearchQuery));
            if (queryVec is null)
            {
                SearchStatusText = "文本编码失败（模型或词表缺失）";
                return;
            }
            var all = await _db.GetAllClipEmbeddingsAsync();
            if (all.Count == 0)
            {
                SearchStatusText = "尚未有任何照片的 CLIP 向量，请先运行「深度分析」";
                return;
            }
            var ranked = await Task.Run(() => all
                .Select(x => (Id: x.Id, Vec: x.Embedding, Score: ClipService.Cosine(queryVec, x.Embedding)))
                .OrderByDescending(x => x.Score)
                .Take(50)
                .ToList());

            // Load matching photo rows.
            var ids = ranked.Select(r => r.Id).ToList();
            var photos = await _db.GetPhotosByIdsAsync(ids);
            var byId = photos.ToDictionary(p => p.Id);
            foreach (var (id, _, score) in ranked)
            {
                if (byId.TryGetValue(id, out var photo))
                {
                    var thumb = photo.ThumbnailCachePath is not null && File.Exists(photo.ThumbnailCachePath)
                        ? photo.ThumbnailCachePath
                        : null;
                    var item = new AiPhotoItem(photo, thumb, photo.BlurScore);
                    item.BlurText = $"相似度 {score:0.000}";
                    SearchResults.Add(item);
                }
            }
            // Keep the ordered matching photos so the top search bar can show them on Home.
            LastSearchPhotos = ranked
                .Select(r => byId.GetValueOrDefault(r.Id))
                .Where(p => p is not null)
                .Cast<PhotoRecord>()
                .ToList();
            LastSearchQuery = SearchQuery.Trim();
            SearchStatusText = SearchResults.Count == 0
                ? "没有找到匹配的照片"
                : $"找到 {SearchResults.Count} 张最相似照片（按相似度排序）";
        }
        catch (OperationCanceledException)
        {
            SearchStatusText = "搜索已取消";
        }
        catch (Exception ex)
        {
            SearchStatusText = "搜索失败：" + ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Ordered (by similarity) photo rows of the last semantic search, for Home display.</summary>
    public List<PhotoRecord>? LastSearchPhotos { get; private set; }

    /// <summary>Query text of the last semantic search.</summary>
    public string LastSearchQuery { get; private set; } = "";
}
