using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using MyAlbum.Core.Services;

namespace MyAlbum_App.Services;

/// <summary>
/// Shared, app-wide UI state (singleton). Used by pages that need to stay in sync,
/// e.g. the reminder banner toggle lives in Settings but renders on Home, and the
/// right-panel info fields are chosen in Settings but rendered on Home.
/// Persisted to %LOCALAPPDATA%\MyAlbum\settings.json.
/// </summary>
public partial class AppState : ObservableObject
{
    [ObservableProperty]
    public partial bool IsReminderVisible { get; set; } = true;

    // Right-panel info field toggles (Settings → 右栏信息).
    [ObservableProperty]
    public partial bool ShowFileName { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowTakenTime { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowCamera { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowLens { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowExposure { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowDimensions { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowGps { get; set; } = true;

    /// <summary>Map tile source key: "tencent"/"amap" (国内 GCJ-02) or "osm" (OpenStreetMap).</summary>
    [ObservableProperty]
    public partial string MapTileSource { get; set; } = "tencent";

    /// <summary>Whether to create a database backup automatically when the app exits.</summary>
    [ObservableProperty]
    public partial bool EnableAutoBackup { get; set; }

    /// <summary>Directory where database backups are stored (empty = default app-data/backups).</summary>
    [ObservableProperty]
    public partial string BackupDirectory { get; set; } = "";

    /// <summary>
    /// Appearance mode: "system" (跟随系统) / "light" (浅色) / "dark" (深色).
    /// Applied to every window via <see cref="ThemeManager"/>.
    /// </summary>
    [ObservableProperty]
    public partial string AppTheme { get; set; } = "system";

    /// <summary>
    /// "保护原始照片": when on, every operation that writes to / deletes / renames the
    /// original photo files is blocked (EXIF edit, GPS write-back, date fix, format/dedup
    /// cleanup, batch rename). Mirrored into <see cref="OriginalDataProtection"/> so the
    /// Core services can enforce it.
    /// </summary>
    [ObservableProperty]
    public partial bool ProtectOriginalData { get; set; }

    // 大语言模型 API（用于地点地址规范化），持久化到 settings.json。
    [ObservableProperty]
    public partial string LlmModel { get; set; } = "deepseek-v4-flash";

    [ObservableProperty]
    public partial string LlmApiKey { get; set; } = "";

    [ObservableProperty]
    public partial string LlmBaseUrl { get; set; } = "https://api.deepseek.com";

    /// <summary>右栏是否显示「拍摄地址」（LLM 规范化后的五级地址）。</summary>
    [ObservableProperty]
    public partial bool ShowLocation { get; set; } = true;

    /// <summary>反地理编码源："osm"（Nominatim）或 "amap"（高德，需 API 密钥）。</summary>
    [ObservableProperty]
    public partial string GeocodeSource { get; set; } = "osm";

    [ObservableProperty]
    public partial string AmapApiKey { get; set; } = "";

    /// <summary>高德安全密钥（用于 regeo 的 sig 数字签名）。</summary>
    [ObservableProperty]
    public partial string AmapApiSecret { get; set; } = "";

    /// <summary>经纬度解析并行数（1..500）。</summary>
    [ObservableProperty]
    public partial int GeocodeParallelism { get; set; } = 4;

    /// <summary>LLM 地址规范化并行数（1..500）。</summary>
    [ObservableProperty]
    public partial int LlmParallelism { get; set; } = 16;

    /// <summary>每次 LLM 请求处理的地点名数量（20..100）。</summary>
    [ObservableProperty]
    public partial int LlmBatchSize { get; set; } = 40;

    /// <summary>扫描 / 导入时的文件级并发数（1..64）。硬盘快可调高，文件夹之间也会并行，
    /// 但同时在解码的文件总数被限制为该值。</summary>
    [ObservableProperty]
    public partial int ScanParallelism { get; set; } = 8;

    // AI page card expand/collapse toggles (AI 功能页各卡片默认展开).
    [ObservableProperty]
    public partial bool IsSceneExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsFaceExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBlurryExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsSimilarExpanded { get; set; } = true;

    private static string SettingsPath =>
        Path.Combine(MyAlbum.Core.Infrastructure.AppPaths.AppDataDirectory, "settings.json");

    private readonly AsyncLock _saveLock = new();

    partial void OnProtectOriginalDataChanged(bool value) => OriginalDataProtection.SetEnabled(value);

    partial void OnAppThemeChanged(string value) => ThemeManager.Apply(ThemeManager.MapMode(value));

    partial void OnLlmModelChanged(string value) => SyncLlmConfig();
    partial void OnLlmApiKeyChanged(string value) => SyncLlmConfig();
    partial void OnLlmBaseUrlChanged(string value) => SyncLlmConfig();

    private void SyncLlmConfig() =>
        MyAlbum.Core.Services.LlmConfig.Set(LlmModel, LlmApiKey, LlmBaseUrl);

    partial void OnGeocodeSourceChanged(string value) => SyncGeocodeConfig();
    partial void OnAmapApiKeyChanged(string value) => SyncGeocodeConfig();
    partial void OnAmapApiSecretChanged(string value) => SyncGeocodeConfig();

    private void SyncGeocodeConfig() =>
        MyAlbum.Core.Services.GeocodeConfig.Set(GeocodeSource, AmapApiKey, AmapApiSecret);

    partial void OnGeocodeParallelismChanged(int value) =>
        MyAlbum.Core.Services.ProcessingConfig.SetGeocodeParallelism(value);

    partial void OnLlmParallelismChanged(int value) =>
        MyAlbum.Core.Services.ProcessingConfig.SetLlmParallelism(value);

    partial void OnLlmBatchSizeChanged(int value) =>
        MyAlbum.Core.Services.ProcessingConfig.SetLlmBatchSize(value);

    public AppState()
    {
        PropertyChanged += OnPropertyChanged;
    }

    private async void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsPath))
        {
            return;
        }
        await SaveAsync();
    }

    /// <summary>Loads persisted settings (called during app startup, off the UI thread).</summary>
    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }
            var json = await File.ReadAllTextAsync(SettingsPath);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data is null)
            {
                return;
            }
            PropertyChanged -= OnPropertyChanged;
            try
            {
                IsReminderVisible = data.IsReminderVisible;
                ShowFileName = data.ShowFileName;
                ShowTakenTime = data.ShowTakenTime;
                ShowCamera = data.ShowCamera;
                ShowLens = data.ShowLens;
                ShowExposure = data.ShowExposure;
                ShowDimensions = data.ShowDimensions;
                ShowGps = data.ShowGps;
                if (!string.IsNullOrWhiteSpace(data.MapTileSource))
                {
                    MapTileSource = data.MapTileSource;
                }
                EnableAutoBackup = data.EnableAutoBackup;
                BackupDirectory = data.BackupDirectory ?? "";
                ProtectOriginalData = data.ProtectOriginalData;
                if (!string.IsNullOrWhiteSpace(data.AppTheme))
                {
                    AppTheme = data.AppTheme;
                }
                IsSceneExpanded = data.IsSceneExpanded;
                IsFaceExpanded = data.IsFaceExpanded;
                IsBlurryExpanded = data.IsBlurryExpanded;
                IsSimilarExpanded = data.IsSimilarExpanded;
                LlmModel = string.IsNullOrWhiteSpace(data.LlmModel) ? "deepseek-v4-flash" : data.LlmModel;
                LlmApiKey = data.LlmApiKey ?? "";
                LlmBaseUrl = string.IsNullOrWhiteSpace(data.LlmBaseUrl) ? "https://api.deepseek.com" : data.LlmBaseUrl;
                ShowLocation = data.ShowLocation;
                GeocodeSource = string.IsNullOrWhiteSpace(data.GeocodeSource) ? "osm" : data.GeocodeSource;
                AmapApiKey = data.AmapApiKey ?? "";
                AmapApiSecret = data.AmapApiSecret ?? "";
                GeocodeParallelism = Math.Clamp(data.GeocodeParallelism <= 0 ? 4 : data.GeocodeParallelism, 1, MyAlbum.Core.Services.ProcessingConfig.MaxParallelism);
                LlmParallelism = Math.Clamp(data.LlmParallelism <= 0 ? 16 : data.LlmParallelism, 1, MyAlbum.Core.Services.ProcessingConfig.MaxParallelism);
                LlmBatchSize = Math.Clamp(data.LlmBatchSize <= 0 ? 40 : data.LlmBatchSize, MyAlbum.Core.Services.ProcessingConfig.MinLlmBatch, MyAlbum.Core.Services.ProcessingConfig.MaxLlmBatch);
                ScanParallelism = Math.Clamp(data.ScanParallelism <= 0 ? 8 : data.ScanParallelism, 1, 64);
                SyncLlmConfig();
                SyncGeocodeConfig();
                MyAlbum.Core.Services.ProcessingConfig.SetGeocodeParallelism(GeocodeParallelism);
                MyAlbum.Core.Services.ProcessingConfig.SetLlmParallelism(LlmParallelism);
                MyAlbum.Core.Services.ProcessingConfig.SetLlmBatchSize(LlmBatchSize);
            }
            finally
            {
                PropertyChanged += OnPropertyChanged;
            }
        }
        catch
        {
            // corrupted settings are ignored
        }
    }

    private async Task SaveAsync()
    {
        var data = new SettingsData
        {
            IsReminderVisible = IsReminderVisible,
            ShowFileName = ShowFileName,
            ShowTakenTime = ShowTakenTime,
            ShowCamera = ShowCamera,
            ShowLens = ShowLens,
            ShowExposure = ShowExposure,
            ShowDimensions = ShowDimensions,
            ShowGps = ShowGps,
            MapTileSource = MapTileSource,
            EnableAutoBackup = EnableAutoBackup,
            BackupDirectory = BackupDirectory,
            ProtectOriginalData = ProtectOriginalData,
            AppTheme = AppTheme,
            IsSceneExpanded = IsSceneExpanded,
            IsFaceExpanded = IsFaceExpanded,
            IsBlurryExpanded = IsBlurryExpanded,
            IsSimilarExpanded = IsSimilarExpanded,
            LlmModel = LlmModel,
            LlmApiKey = LlmApiKey,
            LlmBaseUrl = LlmBaseUrl,
            ShowLocation = ShowLocation,
            GeocodeSource = GeocodeSource,
            AmapApiKey = AmapApiKey,
            AmapApiSecret = AmapApiSecret,
            GeocodeParallelism = GeocodeParallelism,
            LlmParallelism = LlmParallelism,
                LlmBatchSize = LlmBatchSize,
                ScanParallelism = ScanParallelism,
            };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        using (await _saveLock.LockAsync())
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(SettingsPath, json);
            }
            catch
            {
                // best effort persist
            }
        }
    }

    private sealed class SettingsData
    {
        public bool IsReminderVisible { get; set; } = true;
        public bool ShowFileName { get; set; } = true;
        public bool ShowTakenTime { get; set; } = true;
        public bool ShowCamera { get; set; } = true;
        public bool ShowLens { get; set; } = true;
        public bool ShowExposure { get; set; } = true;
        public bool ShowDimensions { get; set; } = true;
        public bool ShowGps { get; set; } = true;
        public string MapTileSource { get; set; } = "osm";
        public bool EnableAutoBackup { get; set; }
        public string BackupDirectory { get; set; } = "";
        public bool ProtectOriginalData { get; set; }
        public string AppTheme { get; set; } = "system";
        public bool IsSceneExpanded { get; set; } = true;
        public bool IsFaceExpanded { get; set; } = true;
        public bool IsBlurryExpanded { get; set; } = true;
        public bool IsSimilarExpanded { get; set; } = true;
        public string LlmModel { get; set; } = "deepseek-v4-flash";
        public string LlmApiKey { get; set; } = "";
        public string LlmBaseUrl { get; set; } = "https://api.deepseek.com";
        public bool ShowLocation { get; set; } = true;
        public string GeocodeSource { get; set; } = "osm";
        public string AmapApiKey { get; set; } = "";
        public string AmapApiSecret { get; set; } = "";
        public int GeocodeParallelism { get; set; } = 4;
        public int LlmParallelism { get; set; } = 16;
        public int LlmBatchSize { get; set; } = 40;
        public int ScanParallelism { get; set; } = 8;
    }

    /// <summary>Minimal async lock to serialize file writes from multiple settings changes.</summary>
    private sealed class AsyncLock
    {
        private readonly SemaphoreSlim _sem = new(1, 1);

        public async Task<IDisposable> LockAsync()
        {
            await _sem.WaitAsync();
            return new Releaser(_sem);
        }

        private sealed class Releaser : IDisposable
        {
            private readonly SemaphoreSlim _sem;
            public Releaser(SemaphoreSlim sem) => _sem = sem;
            public void Dispose() => _sem.Release();
        }
    }
}
