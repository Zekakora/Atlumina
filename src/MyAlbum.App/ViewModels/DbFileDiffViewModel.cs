using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MyAlbum.Core.Data;
using MyAlbum.Core.Services;
using MyAlbum_App.Services;

namespace MyAlbum_App.ViewModels;

/// <summary>Filter option for the diff list (全部 / 有差异 / 无差异 / 文件缺失).</summary>
public sealed record DiffFilterOption(string Label, DiffStatus? Status);

/// <summary>One row of the 数据库与文件对比 window.</summary>
public partial class DbFileDiffItem : ObservableObject
{
    public required PhotoDiffItem Diff { get; init; }
    public required string FileName { get; init; }
    public required string Folder { get; init; }
    public required string StatusText { get; init; }
    public required string ChangedText { get; init; }
    public required string DbModifiedText { get; init; }
    public required string FileModifiedText { get; init; }
    public required string FileSizeText { get; init; }
    public required string CameraText { get; init; }
    public required string TakenText { get; init; }
    public required bool HasDiff { get; init; }
    public required bool IsMatch { get; init; }
    public required bool IsMissing { get; init; }
    public required bool CanAction { get; init; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// The "数据库与文件对比" tool: scans the whole library, compares each indexed row with its
/// source file, and lets the user sync either direction — overwrite the DB from the files,
/// or write the DB's data back into the files (blocked while 保护原始照片 is on).
/// </summary>
public sealed partial class DbFileDiffViewModel : ObservableObject
{
    private readonly DbFileDiffService _service;
    private readonly PhotoDatabase _db;
    private readonly AppState _appState;
    private readonly List<DbFileDiffItem> _allItems = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<DbFileDiffItem> Items { get; } = new();

    public IReadOnlyList<DiffFilterOption> FilterOptions { get; } = new[]
    {
        new DiffFilterOption("全部", null),
        new DiffFilterOption("有差异", DiffStatus.Differs),
        new DiffFilterOption("文件缺失", DiffStatus.FileMissing),
        new DiffFilterOption("无差异", DiffStatus.Match),
    };

    [ObservableProperty]
    public partial DiffFilterOption? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial bool CanOverwrite { get; set; }

    [ObservableProperty]
    public partial bool CanWriteBack { get; set; }

    [ObservableProperty]
    public partial bool ProtectionEnabled { get; set; }

    public string SelectedText => SelectedCount == 0
        ? "未选择照片"
        : $"已选 {SelectedCount} 张";

    public IAsyncRelayCommand ScanCommand { get; }
    public IRelayCommand CancelScanCommand { get; }

    public DbFileDiffViewModel(DbFileDiffService service, PhotoDatabase db, AppState appState)
    {
        _service = service;
        _db = db;
        _appState = appState;
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        CancelScanCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        SelectedFilter = FilterOptions[0];
        _appState.PropertyChanged += AppState_OnPropertyChanged;
    }

    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedFilterChanged(DiffFilterOption? value) => ApplyFilter();

    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(SelectedText));

    private void AppState_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.ProtectOriginalData))
        {
            UpdateActionState();
        }
    }

    public async Task ScanAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        Progress = 0;
        ProgressText = "正在读取索引…";
        try
        {
            var photos = await _db.GetPhotosAsync(limit: int.MaxValue);
            ProgressText = "正在对比文件…";
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                ProgressText = $"已对比 {p.Done}/{p.Total}";
            });
            var result = await Task.Run(() => _service.ScanAsync(photos, progress, ct), ct);

            _allItems.Clear();
            foreach (var item in result.Items)
            {
                _allItems.Add(BuildItem(item));
            }
            foreach (var item in _allItems)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
            ApplyFilter();

            StatusText = ct.IsCancellationRequested
                ? "已取消扫描。"
                : $"共 {result.Total} 张：无差异 {result.Matched} · 有差异 {result.Differs} · 文件缺失 {result.Missing}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消扫描。";
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败：" + ex.Message;
        }
        finally
        {
            Progress = 0;
            IsBusy = false;
        }
    }

    private static DbFileDiffItem BuildItem(PhotoDiffItem d)
    {
        var db = d.Db;
        bool hasDiff = d.Status == DiffStatus.Differs;
        return new DbFileDiffItem
        {
            Diff = d,
            FileName = db.FileName,
            Folder = db.DirectoryPath,
            StatusText = d.Status switch
            {
                DiffStatus.Match => "无差异",
                DiffStatus.Differs => "有差异",
                _ => "文件缺失",
            },
            ChangedText = string.Join(" · ", d.ChangedFields),
            DbModifiedText = db.FileModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            FileModifiedText = d.ActualModifiedUtc is { } m ? m.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—",
            FileSizeText = FormatBytes(d.ActualSizeBytes ?? db.FileSizeBytes),
            CameraText = $"{db.CameraMake} {db.CameraModel}".Trim() is { Length: > 0 } c ? c : "—",
            TakenText = db.TakenAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "—",
            HasDiff = hasDiff,
            IsMatch = d.Status == DiffStatus.Match,
            IsMissing = d.Status == DiffStatus.FileMissing,
            CanAction = d.Status != DiffStatus.FileMissing,
        };
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DbFileDiffItem.IsSelected))
        {
            UpdateActionState();
        }
    }

    private void ApplyFilter()
    {
        var filter = SelectedFilter?.Status;
        Items.Clear();
        foreach (var item in _allItems)
        {
            if (filter is null || StatusOf(item.Diff) == filter)
            {
                Items.Add(item);
            }
        }
        UpdateActionState();
    }

    private static DiffStatus StatusOf(PhotoDiffItem d) => d.Status;

    public void SelectAll()
    {
        foreach (var item in Items)
        {
            if (item.CanAction)
            {
                item.IsSelected = true;
            }
        }
        UpdateActionState();
    }

    public void ClearSelection()
    {
        foreach (var item in _allItems)
        {
            item.IsSelected = false;
        }
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        int actionable = _allItems.Count(i => i.IsSelected && i.CanAction);
        SelectedCount = actionable;
        ProtectionEnabled = OriginalDataProtection.IsEnabled;
        CanOverwrite = actionable > 0;
        CanWriteBack = actionable > 0 && !OriginalDataProtection.IsEnabled;
        OnPropertyChanged(nameof(SelectedText));
    }

    /// <summary>照片 → 数据库：把所选照片的元数据覆盖进索引。返回提示消息（null 表示直接更新了状态栏）。</summary>
    public async Task<string?> OverwriteDatabaseAsync()
    {
        var selected = _allItems.Where(i => i.IsSelected && i.CanAction).Select(i => i.Diff).ToList();
        if (selected.Count == 0)
        {
            return null;
        }
        IsBusy = true;
        ProgressText = "正在覆盖数据库…";
        try
        {
            int ok = await Task.Run(() => _service.OverwriteDatabaseFromFilesAsync(selected));
            StatusText = $"已将 {ok} 张照片的元数据覆盖到数据库（大小/修改时间/拍摄时间/相机/尺寸/位置）。评分、标签、缩略图未改动。";
            await App.Services.GetRequiredService<HomeViewModel>().RefreshAsync();
            return null;
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
    }

    /// <summary>数据库 → 照片：把所选照片的库内数据（拍摄时间/评分/GPS）写入文件 EXIF。返回提示消息。</summary>
    public async Task<string?> WriteBackAsync()
    {
        var selected = _allItems.Where(i => i.IsSelected && i.CanAction).Select(i => i.Diff).ToList();
        if (selected.Count == 0)
        {
            return null;
        }
        if (OriginalDataProtection.IsEnabled)
        {
            return OriginalDataProtection.BlockedMessage;
        }

        IsBusy = true;
        ProgressText = "正在写入照片…";
        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                ProgressText = $"已写入 {p.Done}/{p.Total}";
            });
            var result = await Task.Run(() => _service.WriteDatabaseToFilesAsync(selected, progress));
            StatusText = result.Failed == 0
                ? $"已将数据库数据写入 {result.Succeeded} 张照片（源文件旁保留 .original 备份）。"
                : $"完成：成功 {result.Succeeded}，失败 {result.Failed}。\n" + string.Join("\n", result.Errors.Take(5));
            await App.Services.GetRequiredService<HomeViewModel>().RefreshAsync();
            return null;
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
    }
}
