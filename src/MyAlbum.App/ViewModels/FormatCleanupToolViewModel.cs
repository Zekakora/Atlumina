using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MyAlbum.Core.Data;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;
using MyAlbum_App.Services;

namespace MyAlbum_App.ViewModels;

/// <summary>A keep-format toggle (checked = this format is kept for selected groups).</summary>
public partial class FormatKeepOption : ObservableObject
{
    public required string Name { get; init; }

    [ObservableProperty]
    public partial bool IsKeep { get; set; } = true;
}

/// <summary>
/// The "格式清理" tool window view model. Groups photos by (folder + base name), lets the
/// user multi-select groups and pick which formats to keep, then deletes the rest into the
/// recycle bin. Groups and their format variants are shown as thumbnails.
/// </summary>
public sealed partial class FormatCleanupToolViewModel : ObservableObject
{
    private static readonly string[] FormatOrder =
    {
        "ARW", "CR2", "CR3", "NEF", "RAF", "ORF", "RW2", "PEF", "SRW", "DNG", "RAW",
        "HIF", "HEIC", "HEIF", "AVIF", "JPG", "JPEG", "PNG", "TIFF", "TIF", "WEBP", "BMP", "GIF",
    };

    private readonly FormatCleanupService _service;
    private readonly ThumbnailService _thumbs;
    private readonly PhotoDatabase _db;
    private readonly AppState _appState;

    private List<FormatGroupItem> _allItems = new();
    private readonly List<FormatKeepOption> _keepOptions = new();

    public FormatCleanupToolViewModel(IReadOnlyList<PhotoRecord> photos)
    {
        _service = App.Services.GetRequiredService<FormatCleanupService>();
        _thumbs = App.Services.GetRequiredService<ThumbnailService>();
        _db = App.Services.GetRequiredService<PhotoDatabase>();
        _photos = photos;
        _appState = App.Services.GetRequiredService<AppState>();
        _appState.PropertyChanged += OnAppStatePropertyChanged;
        IsWriteBlocked = _appState.ProtectOriginalData;
    }

    public ObservableCollection<FormatGroupItem> Groups { get; } = new();

    public ObservableCollection<FormatKeepOption> KeepFormats { get; } = new();

    private readonly IReadOnlyList<PhotoRecord> _photos;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial string PreviewText { get; set; } = "";

    [ObservableProperty]
    public partial bool CanDelete { get; set; }

    /// <summary>True while 「保护原始照片」 is on — the delete button is disabled.</summary>
    [ObservableProperty]
    public partial bool IsWriteBlocked { get; set; }

    [ObservableProperty]
    public partial bool HasGroups { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool SelectAll { get; set; } = true;

    partial void OnSearchTextChanged(string value) => ApplySearch();

    partial void OnSelectAllChanged(bool value)
    {
        foreach (var item in _allItems)
        {
            item.IsSelected = value;
        }
        Recompute();
    }

    public async Task RunAsync()
    {
        IsBusy = true;
        StatusText = "正在按文件名分组…";
        try
        {
            var groups = await Task.Run(() => _service.GroupByPhoto(_photos));
            if (groups.Count == 0)
            {
                StatusText = "没有“同文件名、多格式”的照片组。\n同组 = 同一文件夹下主文件名相同的多个文件（如 123.ARW + 123.HIF + 123.JPG）。";
                HasGroups = false;
                return;
            }

            _allItems = groups.Select(g => new FormatGroupItem(g)).ToList();

            var formats = _allItems
                .SelectMany(i => i.Photos)
                .Select(p => p.Extension.TrimStart('.').ToUpperInvariant())
                .Distinct()
                .OrderBy(FormatPriorityIndex)
                .ThenBy(f => f)
                .ToList();

            _keepOptions.Clear();
            KeepFormats.Clear();
            foreach (var f in formats)
            {
                var opt = new FormatKeepOption { Name = f };
                opt.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FormatKeepOption.IsKeep))
                    {
                        Recompute();
                    }
                };
                _keepOptions.Add(opt);
                KeepFormats.Add(opt);
            }

            foreach (var item in _allItems)
            {
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FormatGroupItem.IsSelected))
                    {
                        Recompute();
                    }
                };
                var thumbs = await Task.WhenAll(item.Photos.Select(p => ToolThumbnailLoader.LoadThumbAsync(_thumbs, _db, p)));
                foreach (var tile in thumbs)
                {
                    if (tile is not null)
                    {
                        item.PhotoItems.Add(tile);
                    }
                }
            }

            HasGroups = true;
            StatusText = $"共 {_allItems.Count} 组（同一文件夹下主文件名相同的文件视为一组）。";
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

    private void OnAppStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.ProtectOriginalData))
        {
            IsWriteBlocked = _appState.ProtectOriginalData;
        }
    }

    partial void OnIsWriteBlockedChanged(bool value) => Recompute();

    private void Recompute()
    {
        var keep = KeptExtensions();
        int del = 0;
        foreach (var item in _allItems.Where(i => i.IsSelected))
        {
            del += item.Photos.Count(p => !keep.Contains(p.Extension.TrimStart('.').ToUpperInvariant()));
        }
        PreviewText = $"选中 {_allItems.Count(i => i.IsSelected)} 组，将删除 {del} 个文件（进入回收站，可从回收站恢复）。";
        CanDelete = del > 0 && !IsWriteBlocked;
    }

    private HashSet<string> KeptExtensions() =>
        new(_keepOptions.Where(o => o.IsKeep).Select(o => o.Name), StringComparer.OrdinalIgnoreCase);

    public async Task DeleteAsync()
    {
        var keep = KeptExtensions();
        var toClean = _allItems.Where(i => i.IsSelected).Select(i => i.Photos).ToList();

        IsBusy = true;
        StatusText = "正在删除未保留格式…";
        int deleted = 0, failed = 0;
        var errors = new List<string>();
        try
        {
            foreach (var group in toClean)
            {
                var r = await Task.Run(async () => await _service.DeleteNonKeptAsync(group, keep));
                deleted += r.DeletedCount;
                failed += r.FailedCount;
                errors.AddRange(r.Errors);
            }

            StatusText = failed == 0
                ? $"格式清理完成：删除 {deleted} 个文件（已放入回收站，可从回收站恢复）。"
                : $"格式清理完成：删除 {deleted} 个文件，失败 {failed} 个。\n" + string.Join("\n", errors.Take(5));

            // The deleted files are no longer in the index; refresh the home grid.
            var home = App.Services.GetRequiredService<HomeViewModel>();
            await home.RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static int FormatPriorityIndex(string format)
    {
        int idx = Array.IndexOf(FormatOrder, format);
        return idx < 0 ? FormatOrder.Length : idx;
    }
}
