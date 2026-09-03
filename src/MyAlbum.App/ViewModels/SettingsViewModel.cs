using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MyAlbum.Core.Data;
using MyAlbum.Core.Infrastructure;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;
using MyAlbum_App.Services;

namespace MyAlbum_App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly PhotoDatabase _db;
    private readonly LibraryService _library;
    private readonly FolderWatcherService _watcher;
    private readonly AppState _appState;
    private readonly ExifWriterService _exif;
    private readonly ExifToolInstallerService _exifInstaller;
    private readonly DatabaseBackupService _backup;
    private readonly DatabaseMaintenanceService _maintenance;
    private CancellationTokenSource? _importCts;
    private CancellationTokenSource? _exifDownloadCts;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string CurrentFileText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsExifToolDownloading { get; set; }

    [ObservableProperty]
    public partial double ExifToolDownloadProgress { get; set; }

    [ObservableProperty]
    public partial string ExifToolProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string ExifToolStatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBackingUp { get; set; }

    [ObservableProperty]
    public partial string BackupStatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial bool EnableAutoBackup { get; set; }

    [ObservableProperty]
    public partial string BackupDirectory { get; set; } = "";

    [ObservableProperty]
    public partial bool IsDatabaseBusy { get; set; }

    [ObservableProperty]
    public partial string DatabaseStatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial string DatabaseProgressText { get; set; } = "";

    public bool IsExifToolAvailable => _exif.IsAvailable;

    public string ExifToolInstallDir => ExifWriterService.SuggestedInstallDir;

    public bool IsReminderVisible
    {
        get => _appState.IsReminderVisible;
        set => _appState.IsReminderVisible = value;
    }

    // Right-panel info field toggles — stored in AppState so Home renders from the same source.
    public bool ShowFileName
    {
        get => _appState.ShowFileName;
        set => _appState.ShowFileName = value;
    }

    public bool ShowTakenTime
    {
        get => _appState.ShowTakenTime;
        set => _appState.ShowTakenTime = value;
    }

    public bool ShowCamera
    {
        get => _appState.ShowCamera;
        set => _appState.ShowCamera = value;
    }

    public bool ShowLens
    {
        get => _appState.ShowLens;
        set => _appState.ShowLens = value;
    }

    public bool ShowExposure
    {
        get => _appState.ShowExposure;
        set => _appState.ShowExposure = value;
    }

    public bool ShowDimensions
    {
        get => _appState.ShowDimensions;
        set => _appState.ShowDimensions = value;
    }

    public bool ShowGps
    {
        get => _appState.ShowGps;
        set => _appState.ShowGps = value;
    }

    // Map tile source — stored in AppState so the map views read the same value.
    public string MapTileSource
    {
        get => _appState.MapTileSource;
        set => _appState.MapTileSource = value;
    }

    // 大语言模型 API 配置（用于地点地址规范化）。
    public string LlmModel
    {
        get => _appState.LlmModel;
        set => _appState.LlmModel = value;
    }

    public string LlmApiKey
    {
        get => _appState.LlmApiKey;
        set => _appState.LlmApiKey = value;
    }

    public string LlmBaseUrl
    {
        get => _appState.LlmBaseUrl;
        set => _appState.LlmBaseUrl = value;
    }

    public bool ShowLocation
    {
        get => _appState.ShowLocation;
        set => _appState.ShowLocation = value;
    }

    // 反地理编码源 + 并行度。
    public string GeocodeSource
    {
        get => _appState.GeocodeSource;
        set => _appState.GeocodeSource = value;
    }

    public string AmapApiKey
    {
        get => _appState.AmapApiKey;
        set => _appState.AmapApiKey = value;
    }

    public string AmapApiSecret
    {
        get => _appState.AmapApiSecret;
        set => _appState.AmapApiSecret = value;
    }

    public int GeocodeParallelism
    {
        get => _appState.GeocodeParallelism;
        set => _appState.GeocodeParallelism = value;
    }

    public int LlmParallelism
    {
        get => _appState.LlmParallelism;
        set => _appState.LlmParallelism = value;
    }

    public int LlmBatchSize
    {
        get => _appState.LlmBatchSize;
        set => _appState.LlmBatchSize = value;
    }

    /// <summary>扫描 / 导入文件级并发数（用户可调，硬盘快就调高）。</summary>
    public int ScanParallelism
    {
        get => _appState.ScanParallelism;
        set => _appState.ScanParallelism = value;
    }

    public IReadOnlyList<MapTileOption> GeocodeSourceOptions { get; } = new[]
    {
        new MapTileOption("osm", "OpenStreetMap（OSM / Nominatim，国际）"),
        new MapTileOption("amap", "高德地图（国内，需 API 密钥，可并发）"),
    };

    // Original-photo protection — stored in AppState so every page reads the same value.
    public bool ProtectOriginalData
    {
        get => _appState.ProtectOriginalData;
        set => _appState.ProtectOriginalData = value;
    }

    // Appearance mode — stored in AppState and applied to every window.
    public string AppTheme
    {
        get => _appState.AppTheme;
        set => _appState.AppTheme = value;
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption("system", "跟随系统"),
        new ThemeOption("light", "浅色"),
        new ThemeOption("dark", "深色"),
    };

    public IReadOnlyList<MapTileOption> MapTileOptions { get; } = new[]
    {
        new MapTileOption("tencent", "腾讯地图（国内矢量，路网/地名全，GCJ-02，自动校正偏移）"),
        new MapTileOption("tencent_sat", "腾讯地图（国内卫星影像，GCJ-02，自动校正偏移）"),
        new MapTileOption("amap", "高德地图（国内矢量，GCJ-02，自动校正偏移）"),
        new MapTileOption("amap_sat", "高德地图（国内卫星，GCJ-02，自动校正偏移）"),
        new MapTileOption("osm", "OpenStreetMap（国际，WGS-84，国内访问可能较慢）"),
    };

    public IAsyncRelayCommand ImportFolderCommand { get; }
    public IRelayCommand ExitCommand { get; }
    public IRelayCommand CancelImportCommand { get; }
    public IAsyncRelayCommand DownloadExifToolCommand { get; }
    public IRelayCommand CancelExifToolDownloadCommand { get; }
    public IRelayCommand OpenExifToolDirCommand { get; }
    public IAsyncRelayCommand BackupNowCommand { get; }
    public IAsyncRelayCommand ChooseBackupDirectoryCommand { get; }
    public IRelayCommand OpenBackupDirectoryCommand { get; }
    public IAsyncRelayCommand VerifyDatabaseCommand { get; }
    public IAsyncRelayCommand CleanupDatabaseCommand { get; }

    public SettingsViewModel(
        PhotoDatabase db,
        LibraryService library,
        FolderWatcherService watcher,
        AppState appState,
        ExifWriterService exif,
        ExifToolInstallerService exifInstaller,
        DatabaseBackupService backup,
        DatabaseMaintenanceService maintenance)
    {
        _db = db;
        _library = library;
        _watcher = watcher;
        _appState = appState;
        _exif = exif;
        _exifInstaller = exifInstaller;
        _backup = backup;
        _maintenance = maintenance;
        ImportFolderCommand = new AsyncRelayCommand(ImportFolderAsync, () => !IsBusy);
        CancelImportCommand = new RelayCommand(() => _importCts?.Cancel(), () => IsBusy);
        ExitCommand = new RelayCommand(() => App.Window.Close());
        DownloadExifToolCommand = new AsyncRelayCommand(DownloadExifToolAsync, () => !IsExifToolDownloading);
        CancelExifToolDownloadCommand = new RelayCommand(() => _exifDownloadCts?.Cancel(), () => IsExifToolDownloading);
        OpenExifToolDirCommand = new RelayCommand(OpenExifToolDir);
        BackupNowCommand = new AsyncRelayCommand(BackupNowAsync, () => !IsBackingUp);
        ChooseBackupDirectoryCommand = new AsyncRelayCommand(ChooseBackupDirectoryAsync);
        OpenBackupDirectoryCommand = new RelayCommand(OpenBackupDirectory);
        VerifyDatabaseCommand = new AsyncRelayCommand(VerifyDatabaseAsync, () => !IsDatabaseBusy);
        CleanupDatabaseCommand = new AsyncRelayCommand(CleanupDatabaseAsync, () => !IsDatabaseBusy);

        EnableAutoBackup = _appState.EnableAutoBackup;
        BackupDirectory = string.IsNullOrWhiteSpace(_appState.BackupDirectory)
            ? _backup.ResolveDirectory("")
            : _appState.BackupDirectory;
        _appState.PropertyChanged += AppState_OnPropertyChanged;
        RefreshExifToolStatus();
    }

    partial void OnIsBusyChanged(bool value)
    {
        ImportFolderCommand.NotifyCanExecuteChanged();
        CancelImportCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsExifToolDownloadingChanged(bool value)
    {
        DownloadExifToolCommand.NotifyCanExecuteChanged();
        CancelExifToolDownloadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsExifToolAvailable));
    }

    partial void OnIsBackingUpChanged(bool value)
    {
        BackupNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDatabaseBusyChanged(bool value)
    {
        VerifyDatabaseCommand.NotifyCanExecuteChanged();
        CleanupDatabaseCommand.NotifyCanExecuteChanged();
    }

    partial void OnEnableAutoBackupChanged(bool value) => _appState.EnableAutoBackup = value;

    partial void OnBackupDirectoryChanged(string value) => _appState.BackupDirectory = value;

    private void AppState_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Mirror AppState changes back so the settings page stays in sync (e.g. the
        // auto-backup setting read at startup and applied to the toggle later).
        if (e.PropertyName == nameof(AppState.EnableAutoBackup))
        {
            EnableAutoBackup = _appState.EnableAutoBackup;
        }
        else if (e.PropertyName == nameof(AppState.BackupDirectory))
        {
            BackupDirectory = _appState.BackupDirectory;
        }
        else if (e.PropertyName == nameof(AppState.ProtectOriginalData))
        {
            OnPropertyChanged(nameof(ProtectOriginalData));
        }
        else if (e.PropertyName == nameof(AppState.AppTheme))
        {
            OnPropertyChanged(nameof(AppTheme));
        }
    }

    /// <summary>Refreshes the "installed / not installed" status text (also called after a download).</summary>
    public void RefreshExifToolStatus()
    {
        var tool = _exif.FindExifTool();
        ExifToolStatusText = tool is null
            ? $"未安装。将安装到 {ExifWriterService.SuggestedInstallDir}"
            : $"已安装：{tool}";
    }

    private async Task DownloadExifToolAsync()
    {
        _exifDownloadCts?.Dispose();
        _exifDownloadCts = new CancellationTokenSource();
        var ct = _exifDownloadCts.Token;

        IsExifToolDownloading = true;
        ExifToolDownloadProgress = 0;
        ExifToolProgressText = "正在获取版本号…";
        try
        {
            var progress = new Progress<(double Fraction, string Status)>(p =>
            {
                ExifToolDownloadProgress = p.Fraction;
                ExifToolProgressText = p.Status;
            });
            await _exifInstaller.DownloadAndInstallAsync(progress, ct);
            _exif.InvalidateCache();
            RefreshExifToolStatus();
            ExifToolProgressText = ct.IsCancellationRequested ? "已取消" : "ExifTool 安装完成。";
        }
        catch (OperationCanceledException)
        {
            ExifToolProgressText = "下载已取消";
        }
        catch (Exception ex)
        {
            ExifToolProgressText = "下载失败：" + ex.Message;
        }
        finally
        {
            IsExifToolDownloading = false;
        }
    }

    private static void OpenExifToolDir()
    {
        try
        {
            var dir = ExifWriterService.SuggestedInstallDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch
        {
            // opening the folder is best-effort
        }
    }

    private async Task BackupNowAsync()
    {
        if (IsBackingUp)
        {
            return;
        }
        IsBackingUp = true;
        BackupStatusText = "正在备份…";
        try
        {
            var dir = _backup.ResolveDirectory(BackupDirectory);
            var path = await _backup.BackupAsync(dir, "manual");
            BackupStatusText = $"已备份到 {path}";
        }
        catch (Exception ex)
        {
            BackupStatusText = "备份失败：" + ex.Message;
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    private async Task ChooseBackupDirectoryAsync()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Window.AppWindow.Id)
        {
            Title = "选择数据库备份目录",
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        var result = await picker.PickSingleFolderAsync();
        if (result?.Path is string path)
        {
            BackupDirectory = path;
        }
    }

    private void OpenBackupDirectory()
    {
        try
        {
            var dir = _backup.ResolveDirectory(BackupDirectory);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch
        {
            // opening the folder is best-effort
        }
    }

    /// <summary>
    /// Restores the index from <paramref name="backupPath"/>, overwriting the current
    /// database, then refreshes every view and re-syncs the folder watchers.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> RestoreFromBackupAsync(string backupPath)
    {
        if (IsBackingUp)
        {
            return false;
        }
        IsBackingUp = true;
        BackupStatusText = "正在恢复…";
        try
        {
            bool settingsRestored = await _backup.RestoreAsync(backupPath);
            if (settingsRestored)
            {
                // 重新读取还原后的 settings.json，并重新应用 LLM/反编码/主题等配置。
                await _appState.LoadAsync();
            }
            await NotifyLibraryChangedAsync();
            await ResyncWatchersAsync();
            BackupStatusText = settingsRestored
                ? "恢复完成，索引与设置已还原。"
                : "恢复完成，索引已刷新。";
            return true;
        }
        catch (Exception ex)
        {
            BackupStatusText = "恢复失败：" + ex.Message;
            return false;
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    private async Task ResyncWatchersAsync()
    {
        foreach (var path in _watcher.WatchedFolders.ToList())
        {
            _watcher.UnwatchFolder(path);
        }
        foreach (var folder in await _db.GetFoldersAsync())
        {
            if (folder.IsWatched && Directory.Exists(folder.Path))
            {
                _watcher.WatchFolder(folder.Path);
            }
        }
    }

    public async Task ImportFolderAsync()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Window.AppWindow.Id)
        {
            Title = "选择照片文件夹",
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
        };
        var result = await picker.PickSingleFolderAsync();
        var path = result?.Path;
        if (path is null)
        {
            return;
        }

        _importCts?.Dispose();
        _importCts = new CancellationTokenSource();
        var ct = _importCts.Token;

        IsBusy = true;
        Progress = 0;
        StatusText = $"正在导入 {Path.GetFileName(path)}...";
        ProgressText = "正在扫描文件…";
        CurrentFileText = "";
        try
        {
            var progress = new Progress<ScanProgress>(sp =>
            {
                Progress = sp.Fraction;
                ProgressText = $"已处理 {sp.Processed}/{sp.TotalFiles}　新增 {sp.Indexed}　跳过 {sp.Skipped}　失败 {sp.Failed}";
                CurrentFileText = sp.CurrentFile;
            });
            using var scanSem = new SemaphoreSlim(ScanParallelism);
            var scanResult = await _library.ScanFolderAsync(path, progress, ct, scanSem);
            StatusText = ct.IsCancellationRequested
                ? "导入已取消"
                : $"导入完成: 新增 {scanResult.Indexed}，跳过 {scanResult.Skipped}，失败 {scanResult.Failed}";
            if (!ct.IsCancellationRequested)
            {
                await NotifyLibraryChangedAsync();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "导入已取消";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<List<FolderVisibilityItem>> GetFoldersForManagementAsync()
    {
        var result = new List<FolderVisibilityItem>();
        foreach (var folder in await _db.GetFoldersAsync())
        {
            var name = folder.Path.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries)[^1];
            result.Add(new FolderVisibilityItem(name, folder.Path, !folder.IsHidden));
        }
        return result;
    }

    public async Task SetFolderVisibilityAsync(string path, bool visible)
    {
        await _db.SetFolderHiddenAsync(path, !visible);
        await NotifyLibraryChangedAsync();
    }

    /// <summary>
    /// Removes a folder from the library: drops the folder record, all photos under it
    /// (and their tags/thumbnails) from the index, and stops watching it. Files on disk
    /// are untouched.
    /// </summary>
    public async Task RemoveFolderAsync(string path)
    {
        _watcher.UnwatchFolder(path);
        var thumbs = await _db.RemoveFolderAsync(path);
        foreach (var thumb in thumbs)
        {
            try
            {
                if (File.Exists(thumb))
                {
                    File.Delete(thumb);
                }
            }
            catch
            {
                // deleting cache files is best-effort
            }
        }
        await NotifyLibraryChangedAsync();
    }

    private async Task NotifyLibraryChangedAsync()
    {
        await App.Services.GetRequiredService<HomeViewModel>().RefreshAsync();
        await App.Services.GetRequiredService<AlbumsViewModel>().ReloadAsync();
    }

    // ---------- 数据库校验 / 冗余清理 ----------

    public async Task VerifyDatabaseAsync()
    {
        if (IsDatabaseBusy)
        {
            return;
        }
        IsDatabaseBusy = true;
        DatabaseStatusText = "正在校验数据库…";
        DatabaseProgressText = "";
        try
        {
            var progress = new Progress<string>(s => DatabaseProgressText = s);
            var report = await Task.Run(() => _maintenance.VerifyAsync(progress));
            DatabaseStatusText = BuildHealthSummary(report);
            DatabaseProgressText = "";
        }
        catch (Exception ex)
        {
            DatabaseStatusText = "校验失败：" + ex.Message;
        }
        finally
        {
            IsDatabaseBusy = false;
        }
    }

    public async Task CleanupDatabaseAsync()
    {
        if (IsDatabaseBusy)
        {
            return;
        }
        IsDatabaseBusy = true;
        DatabaseStatusText = "正在清理冗余数据…";
        DatabaseProgressText = "";
        try
        {
            var progress = new Progress<string>(s => DatabaseProgressText = s);
            var result = await Task.Run(() => _maintenance.CleanupAsync(progress));
            // 清理后不再重复全量 Verify（那会再扫一遍缩略图与完整性），改用清理前已算好的计数。
            var removed = new List<string>();
            if (result.RemovedMissingPhotos > 0) removed.Add($"移除缺失照片记录 {result.RemovedMissingPhotos} 个");
            if (result.RemovedCaseDuplicates > 0) removed.Add($"移除重复路径记录 {result.RemovedCaseDuplicates} 个");
            if (result.RemovedOrphanThumbnails > 0) removed.Add($"删除孤立缩略图 {result.RemovedOrphanThumbnails} 个（释放 {FormatBytes(result.FreedThumbnailBytes)}）");
            if (result.RemovedOrphanPhotoTags > 0) removed.Add($"移除孤立标签关联 {result.RemovedOrphanPhotoTags} 条");
            if (result.RemovedOrphanFaces > 0) removed.Add($"移除孤立人脸记录 {result.RemovedOrphanFaces} 条");
            DatabaseStatusText = removed.Count == 0
                ? "未发现需要清理的冗余数据。"
                : "清理完成：" + string.Join("，", removed) + "。";
            DatabaseProgressText = "";
        }
        catch (Exception ex)
        {
            DatabaseStatusText = "清理失败：" + ex.Message;
        }
        finally
        {
            IsDatabaseBusy = false;
        }
    }

    /// <summary>
    /// Empties the whole library: stops the folder watchers, deletes all index rows,
    /// clears the thumbnail cache and refreshes every view. Not reversible.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        if (IsDatabaseBusy)
        {
            return;
        }
        IsDatabaseBusy = true;
        DatabaseStatusText = "正在重置数据库…";
        try
        {
            foreach (var path in _watcher.WatchedFolders.ToList())
            {
                _watcher.UnwatchFolder(path);
            }
            await _db.ResetLibraryAsync();
            ClearThumbnailCache();
            await NotifyLibraryChangedAsync();
            DatabaseStatusText = "数据库已重置：照片索引、文件夹、标签、智能相册、人脸与缩略图缓存均已清空。";
        }
        catch (Exception ex)
        {
            DatabaseStatusText = "重置失败：" + ex.Message;
        }
        finally
        {
            IsDatabaseBusy = false;
        }
    }

    private static void ClearThumbnailCache()
    {
        try
        {
            var dir = AppPaths.ThumbnailCacheDirectory;
            if (!Directory.Exists(dir))
            {
                return;
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // best effort
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Builds the confirmation text shown before cleaning: lists what redundant data will
    /// be removed. Returns (summary, hasWork) so the caller can skip the confirmation
    /// dialog when there is nothing to clean.
    /// </summary>
    public async Task<(string Summary, bool HasWork)> BuildCleanupPreviewAsync()
    {
        var report = await Task.Run(() => _maintenance.VerifyAsync());
        bool hasWork = report.RedundantMissingPhotoCount > 0
            || report.CaseDuplicateCount > 0
            || report.OrphanThumbnailCount > 0
            || report.OrphanPhotoTagCount > 0
            || report.OrphanFaceCount > 0;
        if (!hasWork)
        {
            return ("未发现需要清理的冗余数据。", false);
        }

        var lines = new List<string>
        {
            "将删除以下冗余数据（仅影响索引记录与应用缓存，不会修改或删除原始照片文件）：",
        };
        if (report.RedundantMissingPhotoCount > 0)
        {
            lines.Add($"· 缺失照片记录 {report.RedundantMissingPhotoCount} 个（文件已不存在）");
        }
        if (report.CaseDuplicateCount > 0)
        {
            lines.Add($"· 重复路径记录 {report.CaseDuplicateCount} 组（同一文件因大小写不同被重复索引）");
        }
        if (report.OrphanThumbnailCount > 0)
        {
            lines.Add($"· 孤立缩略图 {report.OrphanThumbnailCount} 个（{FormatBytes(report.OrphanThumbnailBytes)}）");
        }
        if (report.OrphanPhotoTagCount > 0)
        {
            lines.Add($"· 孤立标签关联 {report.OrphanPhotoTagCount} 条");
        }
        if (report.OrphanFaceCount > 0)
        {
            lines.Add($"· 孤立人脸记录 {report.OrphanFaceCount} 条");
        }
        return (string.Join("\n", lines), true);
    }

    private static string BuildHealthSummary(DatabaseHealthReport report)
    {
        var integrity = report.IntegrityOk
            ? "数据库完整性正常"
            : "⚠ 数据库完整性异常：" + report.IntegrityMessage;
        var parts = new List<string>
        {
            integrity,
            $"照片记录 {report.PhotoCount} 个（正常 {report.ActivePhotoCount}）",
            $"缺失照片记录 {report.RedundantMissingPhotoCount} 个（文件已不存在，可清理）",
            report.CaseDuplicateCount > 0
                ? $"重复路径记录 {report.CaseDuplicateCount} 组（同一文件因大小写不同被重复索引，可清理）"
                : "重复路径记录 0 组",
            $"缩略图 {report.TotalThumbnailCount} 个，其中孤立 {report.OrphanThumbnailCount} 个（{FormatBytes(report.OrphanThumbnailBytes)}）",
            $"孤立标签关联 {report.OrphanPhotoTagCount} 条 · 孤立人脸 {report.OrphanFaceCount} 条",
            $"数据库体积 {FormatBytes(report.DatabaseBytes)}",
        };
        return string.Join("\n", parts);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
    }
}

/// <summary>A map tile source option for the settings picker.</summary>
public sealed record MapTileOption(string Key, string Label);

/// <summary>An appearance-mode option for the settings picker (浅色 / 深色 / 跟随系统).</summary>
public sealed record ThemeOption(string Key, string Label);
