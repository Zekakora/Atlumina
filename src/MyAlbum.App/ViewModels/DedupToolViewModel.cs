using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MyAlbum.Core.Data;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;
using MyAlbum_App.Services;

namespace MyAlbum_App.ViewModels;

/// <summary>One name-based duplicate group shown in the "去重检测" tool window.</summary>
public sealed class DedupGroupItem
{
    /// <summary>File name without extension.</summary>
    public required string Stem { get; init; }

    /// <summary>Short header, e.g. "DSC05361 · 3 个位置".</summary>
    public required string Title { get; init; }

    /// <summary>Every file's full path (one per line) for the group hover tooltip.</summary>
    public required string AllPathsText { get; init; }

    public ObservableCollection<DedupOccurrenceItem> Occurrences { get; } = new();
}

/// <summary>One folder occurrence of a duplicate stem (all its format variants).</summary>
public partial class DedupOccurrenceItem : ObservableObject
{
    public required string Directory { get; init; }

    public required bool IsSuggestedKeep { get; init; }

    public required string FormatsText { get; init; }

    public ObservableCollection<DedupPhotoItem> Photos { get; } = new();
}

/// <summary>A single photo tile in an occurrence: suggested-keep flag + mark-for-delete.</summary>
public partial class DedupPhotoItem : ObservableObject
{
    public required PhotoGridItem Grid { get; init; }

    /// <summary>True when this photo belongs to the suggested-keep occurrence.</summary>
    public required bool IsKeep { get; init; }

    [ObservableProperty]
    public partial bool IsMarkedForDelete { get; set; }
}

/// <summary>
/// The "去重检测" tool window view model. Groups photos by file name across folders
/// (each folder = one occurrence with its format variants), suggests the best occurrence
/// to keep (format richness → path organization → bytes → newest) and lets the user mark
/// the rest for deletion (sent to the recycle bin).
/// </summary>
public sealed partial class DedupToolViewModel : ObservableObject
{
    private readonly DuplicateService _dupes;
    private readonly ThumbnailService _thumbs;
    private readonly PhotoDatabase _db;
    private readonly FormatCleanupService _cleanup;
    private readonly AppState _appState;

    /// <summary>Mutable snapshot of the filter's photos so deleted ones can be dropped.</summary>
    private readonly List<PhotoRecord> _photos;

    public DedupToolViewModel(IReadOnlyList<PhotoRecord> photos)
    {
        _photos = photos.ToList();
        _dupes = App.Services.GetRequiredService<DuplicateService>();
        _thumbs = App.Services.GetRequiredService<ThumbnailService>();
        _db = App.Services.GetRequiredService<PhotoDatabase>();
        _cleanup = App.Services.GetRequiredService<FormatCleanupService>();
        _appState = App.Services.GetRequiredService<AppState>();
        _appState.PropertyChanged += OnAppStatePropertyChanged;
        IsWriteBlocked = _appState.ProtectOriginalData;
    }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial bool HasGroups { get; set; }

    [ObservableProperty]
    public partial int MarkedCount { get; set; }

    [ObservableProperty]
    public partial bool CanDelete { get; set; }

    [ObservableProperty]
    public partial string MarkedText { get; set; } = "未选择任何照片";

    /// <summary>True while 「保护原始照片」 is on — the delete button is disabled.</summary>
    [ObservableProperty]
    public partial bool IsWriteBlocked { get; set; }

    public ObservableCollection<DedupGroupItem> Groups { get; } = new();

    public async Task RunAsync()
    {
        IsBusy = true;
        StatusText = "正在按文件名分组…";
        try
        {
            var groups = await Task.Run(() => _dupes.FindNameDuplicates(_photos));
            if (groups.Count == 0)
            {
                StatusText = "未发现重复照片（没有同名文件出现在多个文件夹）。";
                HasGroups = false;
                Groups.Clear();
                UpdateDeleteState();
                return;
            }

            Groups.Clear();
            foreach (var g in groups)
            {
                var item = new DedupGroupItem
                {
                    Stem = g.Stem,
                    Title = $"{g.Stem} · {g.Occurrences.Count} 个位置",
                    AllPathsText = string.Join("\n",
                        g.Occurrences.SelectMany(o => o.Photos).Select(p => p.FilePath)),
                };

                foreach (var occ in g.Occurrences)
                {
                    var occItem = new DedupOccurrenceItem
                    {
                        Directory = occ.Directory,
                        IsSuggestedKeep = occ.IsSuggestedKeep,
                        FormatsText = occ.FormatsText,
                    };

                    // Thumbnails load in parallel; missing renders are generated on demand.
                    var thumbs = await Task.WhenAll(occ.Photos.Select(p => ToolThumbnailLoader.LoadThumbAsync(_thumbs, _db, p)));
                    for (int i = 0; i < occ.Photos.Count; i++)
                    {
                        if (thumbs[i] is not { } tile)
                        {
                            continue;
                        }
                        var photo = new DedupPhotoItem
                        {
                            Grid = tile,
                            IsKeep = occ.IsSuggestedKeep,
                        };
                        photo.IsMarkedForDelete = !photo.IsKeep;
                        photo.PropertyChanged += OnPhotoPropertyChanged;
                        occItem.Photos.Add(photo);
                    }
                    item.Occurrences.Add(occItem);
                }
                Groups.Add(item);
            }

            HasGroups = true;
            StatusText = $"检测到 {groups.Count} 组重复照片（同名文件出现在多个文件夹；勾选要删除的，悬停可查看完整路径）。";
            UpdateDeleteState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Pre-selects every photo of the non-suggested-keep occurrences for deletion.</summary>
    public void SelectDeletable()
    {
        foreach (var p in Groups.SelectMany(g => g.Occurrences).SelectMany(o => o.Photos))
        {
            p.IsMarkedForDelete = !p.IsKeep;
        }
        UpdateDeleteState();
    }

    public void ClearSelection()
    {
        foreach (var p in Groups.SelectMany(g => g.Occurrences).SelectMany(o => o.Photos))
        {
            p.IsMarkedForDelete = false;
        }
        UpdateDeleteState();
    }

    private void OnPhotoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DedupPhotoItem.IsMarkedForDelete))
        {
            UpdateDeleteState();
        }
    }

    private void OnAppStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.ProtectOriginalData))
        {
            IsWriteBlocked = _appState.ProtectOriginalData;
        }
    }

    partial void OnIsWriteBlockedChanged(bool value) => UpdateDeleteState();

    private void UpdateDeleteState()
    {
        int count = Groups.SelectMany(g => g.Occurrences).SelectMany(o => o.Photos).Count(p => p.IsMarkedForDelete);
        MarkedCount = count;
        CanDelete = count > 0 && !IsWriteBlocked;
        MarkedText = count > 0 ? $"已选 {count} 张删除" : "未选择任何照片";
    }

    /// <summary>Sends the marked photos to the recycle bin, drops them from the index and re-groups.</summary>
    public async Task DeleteAsync()
    {
        var toDelete = Groups.SelectMany(g => g.Occurrences).SelectMany(o => o.Photos)
            .Where(p => p.IsMarkedForDelete).Select(p => p.Grid.Photo).ToList();
        if (toDelete.Count == 0)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在删除所选照片…";
        FormatCleanupResult result;
        try
        {
            result = await _cleanup.DeletePhotosAsync(toDelete);
        }
        finally
        {
            IsBusy = false;
        }

        var home = App.Services.GetRequiredService<HomeViewModel>();
        await home.RefreshAsync();

        var deleted = new HashSet<string>(toDelete.Select(p => p.FilePath), StringComparer.OrdinalIgnoreCase);
        _photos.RemoveAll(p => deleted.Contains(p.FilePath));

        StatusText = result.FailedCount == 0
            ? $"已删除 {result.DeletedCount} 张重复照片（已放入回收站）。"
            : $"删除完成：成功 {result.DeletedCount}，失败 {result.FailedCount} 个。\n" + string.Join("\n", result.Errors.Take(5));

        await RunAsync();
    }
}
