using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using MyAlbum.Core.Data;
using MyAlbum.Core.Services;
using MyAlbum_App.Services;

namespace MyAlbum_App.ViewModels;

/// <summary>
/// The "低质量照片清理" tool view model. Runs the blur + aesthetic analysis passes, shows the
/// low-quality photos grouped into logical photo groups (same folder + base name = one shot),
/// and deletes whole groups into the recycle bin.
/// </summary>
public sealed partial class QualityToolViewModel : ObservableObject
{
    private readonly LowQualityPhotoService _service;
    private readonly PhotoDatabase _db;
    private readonly AppState _appState;
    private CancellationTokenSource? _analyzeCts;

    private List<QualityGroupItem> _allItems = new();

    public QualityToolViewModel()
    {
        _service = App.Services.GetRequiredService<LowQualityPhotoService>();
        _db = App.Services.GetRequiredService<PhotoDatabase>();
        _appState = App.Services.GetRequiredService<AppState>();
        _appState.PropertyChanged += OnAppStatePropertyChanged;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy);
        CancelAnalyzeCommand = new RelayCommand(() => _analyzeCts?.Cancel(), () => IsAnalyzing);
        DeleteCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => CanDelete && !IsDeleting);
        RefreshCommand = new AsyncRelayCommand(LoadGroupsAsync, () => !IsBusy);
        // 必须在命令创建之后再赋值，避免 OnIsWriteBlockedChanged 对 null 命令 NotifyCanExecuteChanged。
        IsWriteBlocked = _appState.ProtectOriginalData;
    }

    public ObservableCollection<QualityGroupItem> Groups { get; } = new();

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsAnalyzing { get; set; }

    [ObservableProperty]
    public partial bool IsDeleting { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial int GroupCount { get; set; }

    [ObservableProperty]
    public partial int SelectedGroupCount { get; set; }

    [ObservableProperty]
    public partial int SelectedPhotoCount { get; set; }

    [ObservableProperty]
    public partial bool CanDelete { get; set; }

    /// <summary>True while 「保护原始照片」 is on — the delete button is disabled.</summary>
    [ObservableProperty]
    public partial bool IsWriteBlocked { get; set; }

    [ObservableProperty]
    public partial bool HasGroups { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    partial void OnSearchTextChanged(string value) => ApplySearch();

    public string SelectedText => SelectedGroupCount == 0
        ? "未选择照片组"
        : $"已选 {SelectedGroupCount} 组 / {SelectedPhotoCount} 个文件";

    partial void OnSelectedGroupCountChanged(int value) => OnPropertyChanged(nameof(SelectedText));
    partial void OnSelectedPhotoCountChanged(int value) => OnPropertyChanged(nameof(SelectedText));

    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IRelayCommand CancelAnalyzeCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }

    partial void OnIsAnalyzingChanged(bool value) => CancelAnalyzeCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDeletingChanged(bool value) => DeleteCommand.NotifyCanExecuteChanged();

    private void OnAppStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.ProtectOriginalData))
        {
            IsWriteBlocked = _appState.ProtectOriginalData;
        }
    }

    partial void OnIsWriteBlockedChanged(bool value) => Recompute();

    /// <summary>
    /// Runs the pending blur (照片质量分析) + NIMA aesthetic (照片美学分析) passes, then
    /// loads the low-quality groups.
    /// </summary>
    public async Task AnalyzeAsync()
    {
        _analyzeCts?.Dispose();
        _analyzeCts = new CancellationTokenSource();
        var ct = _analyzeCts.Token;

        IsAnalyzing = true;
        IsBusy = true;
        Progress = 0;
        StatusText = "正在分析照片质量（清晰度 + 美学评分）…";
        ProgressText = "";
        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                Progress = p.Total == 0 ? 0 : p.Done / (double)p.Total;
                ProgressText = $"已分析 {p.Done}/{p.Total}";
            });
            var result = await _service.AnalyzePendingAsync(progress, ct);
            StatusText = ct.IsCancellationRequested
                ? "分析已取消"
                : $"分析完成：共 {result.Analyzed} 张完成（失败 {result.Failed}）。";
            if (!ct.IsCancellationRequested)
            {
                await LoadGroupsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "分析已取消";
        }
        catch (Exception ex)
        {
            StatusText = "分析失败：" + ex.Message;
        }
        finally
        {
            IsAnalyzing = false;
            IsBusy = false;
        }
    }

    /// <summary>Loads the low-quality photo groups (blurry and/or low-scoring).</summary>
    public async Task LoadGroupsAsync()
    {
        IsBusy = true;
        try
        {
            var groups = await _service.GetLowQualityGroupsAsync(10000);
            _allItems = groups.Select(g => new QualityGroupItem(g)).ToList();
            foreach (var item in _allItems)
            {
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(QualityGroupItem.IsSelected))
                    {
                        Recompute();
                    }
                };
                item.LoadThumbnails();
            }
            HasGroups = _allItems.Count > 0;
            GroupCount = _allItems.Count;
            StatusText = _allItems.Count == 0
                ? "没有发现低质量照片（模糊或美学低分）。"
                : $"发现 {_allItems.Count} 组低质量照片（同一文件夹下主文件名相同的文件视为一组）。";
            ApplySearch();
            Recompute();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySearch()
    {
        var q = SearchText.Trim();
        Groups.Clear();
        foreach (var item in q.Length == 0
                     ? _allItems
                     : _allItems.Where(i => i.Stem.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            Groups.Add(item);
        }
    }

    private void Recompute()
    {
        var selected = _allItems.Where(i => i.IsSelected).ToList();
        SelectedGroupCount = selected.Count;
        SelectedPhotoCount = selected.Sum(g => g.PhotoCount);
        CanDelete = selected.Count > 0 && !IsWriteBlocked;
        DeleteCommand.NotifyCanExecuteChanged();
    }

    public async Task DeleteSelectedAsync()
    {
        var groups = _allItems.Where(i => i.IsSelected).Select(i => i.Group).ToList();
        if (groups.Count == 0)
        {
            return;
        }
        IsDeleting = true;
        IsBusy = true;
        StatusText = "正在删除到回收站…";
        try
        {
            var (deleted, failed) = await _service.DeleteGroupsAsync(groups);
            StatusText = failed == 0
                ? $"已删除 {deleted} 个文件（进入回收站，可从回收站恢复）。"
                : $"删除完成：成功 {deleted}，失败 {failed}。";
            await LoadGroupsAsync();
            // The library index changed; refresh the home grid.
            await App.Services.GetRequiredService<HomeViewModel>().RefreshAsync();
        }
        finally
        {
            IsDeleting = false;
            IsBusy = false;
        }
    }
}

/// <summary>One low-quality photo group row in the tool list.</summary>
public sealed partial class QualityGroupItem : ObservableObject
{
    public LowQualityPhotoGroup Group { get; }
    public string Stem => Group.Title;
    public string Folder => Group.FolderText;
    public string ReasonText => Group.ReasonText;
    public int PhotoCount => Group.Photos.Count;
    public string DetailText => PhotoCount == 1
        ? "1 个文件"
        : $"{PhotoCount} 个文件（同组：相同主文件名不同扩展名）";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public ObservableCollection<PhotoGridItem> PhotoItems { get; } = new();

    public QualityGroupItem(LowQualityPhotoGroup group)
    {
        Group = group;
    }

    /// <summary>Loads member thumbnails (best effort, off the UI thread).</summary>
    public void LoadThumbnails()
    {
        var thumbs = App.Services.GetRequiredService<ThumbnailService>();
        var db = App.Services.GetRequiredService<PhotoDatabase>();
        foreach (var photo in Group.Photos)
        {
            _ = Task.Run(async () =>
            {
                var tile = await ToolThumbnailLoader.LoadThumbAsync(thumbs, db, photo);
                if (tile is not null)
                {
                    App.DispatcherQueue.TryEnqueue(() => PhotoItems.Add(tile));
                }
            });
        }
    }
}
