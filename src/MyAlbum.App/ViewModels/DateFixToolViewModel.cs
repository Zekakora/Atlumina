using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;
using MyAlbum_App.Services;

namespace MyAlbum_App.ViewModels;

/// <summary>
/// The "拍摄时间修复" tool window view model. Scans the current filter's photos for a
/// mismatch between the Windows file creation time and the EXIF shooting date, shows the
/// current vs. after-fix times, and lets the user pick a filter (which ones need fixing)
/// and which rows to fix.
/// </summary>
public sealed partial class DateFixToolViewModel : ObservableObject
{
    private readonly PhotoDateFixService _service;
    private readonly IReadOnlyList<PhotoRecord> _photos;
    private readonly AppState _appState;

    private List<PhotoDateFixItem> _allItems = new();

    public DateFixToolViewModel(IReadOnlyList<PhotoRecord> photos)
    {
        _photos = photos;
        _service = App.Services.GetRequiredService<PhotoDateFixService>();
        _appState = App.Services.GetRequiredService<AppState>();
        _appState.PropertyChanged += OnAppStatePropertyChanged;
        IsWriteBlocked = _appState.ProtectOriginalData;
    }

    public ObservableCollection<PhotoDateFixItem> Items { get; } = new();

    public IReadOnlyList<DateFixFilterOption> FilterOptions { get; } = new[]
    {
        new DateFixFilterOption("需要修正", PhotoDateStatus.Mismatch),
        new DateFixFilterOption("已一致", PhotoDateStatus.Match),
        new DateFixFilterOption("无拍摄日期", PhotoDateStatus.NoExif),
        new DateFixFilterOption("全部", null),
    };

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial string StatsText { get; set; } = "";

    [ObservableProperty]
    public partial DateFixFilterOption? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial bool SelectAll { get; set; } = true;

    [ObservableProperty]
    public partial bool FixModified { get; set; }

    [ObservableProperty]
    public partial string FixText { get; set; } = "修正 0 个";

    [ObservableProperty]
    public partial bool CanFix { get; set; }

    /// <summary>True while 「保护原始照片」 is on — the fix button is disabled.</summary>
    [ObservableProperty]
    public partial bool IsWriteBlocked { get; set; }

    private void OnAppStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.ProtectOriginalData))
        {
            IsWriteBlocked = _appState.ProtectOriginalData;
        }
    }

    partial void OnIsWriteBlockedChanged(bool value) => UpdateFixText();

    partial void OnSelectedFilterChanged(DateFixFilterOption? value) => ApplyFilter();

    partial void OnSelectAllChanged(bool value)
    {
        foreach (var item in _allItems)
        {
            item.Selected = value;
        }
        UpdateFixText();
    }

    public async Task ScanAsync()
    {
        IsBusy = true;
        StatusText = "正在对比创建日期与拍摄日期…";
        try
        {
            var scan = await Task.Run(() => _service.Scan(_photos));
            RebuildItems(scan);
            StatsText = $"一致 {scan.MatchedCount} 个 · 需要修正 {scan.MismatchCount} 个 · 无拍摄日期 {scan.NoExifCount} 个";
            SelectedFilter = scan.MismatchCount > 0 ? FilterOptions[0] : FilterOptions[3];
            StatusText = scan.MismatchCount == 0
                ? "没有需要修正的照片（创建日期与拍摄日期一致）。"
                : "勾选要修正的照片，点击「修正」将文件创建时间（及可选修改时间）设为 EXIF 拍摄日期。";
            ApplyFilter();
            UpdateFixText();
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RebuildItems(PhotoDateScanResult scan)
    {
        _allItems = scan.Items.Select(i => new PhotoDateFixItem(i)).ToList();
        foreach (var item in _allItems)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PhotoDateFixItem.Selected))
                {
                    UpdateFixText();
                }
            };
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        if (SelectedFilter is not { } filter || filter.Status is null)
        {
            foreach (var item in _allItems)
            {
                Items.Add(item);
            }
        }
        else
        {
            foreach (var item in _allItems.Where(i => i.Item.Status == filter.Status))
            {
                Items.Add(item);
            }
        }
        UpdateFixText();
    }

    private void UpdateFixText()
    {
        int count = _allItems.Count(i => i.IsFixable && i.Selected);
        FixText = $"修正 {count} 个";
        CanFix = count > 0 && !IsWriteBlocked;
    }

    public async Task FixAsync()
    {
        var toFix = _allItems.Where(i => i.IsFixable && i.Selected).Select(i => i.Mismatch!).ToList();
        if (toFix.Count == 0)
        {
            return;
        }

        IsBusy = true;
        StatusText = $"正在修正 {toFix.Count} 个文件时间戳…";
        try
        {
            var fix = await Task.Run(() => _service.Fix(toFix, FixModified));
            StatusText = fix.Failed == 0
                ? $"修正完成：成功 {fix.Ok} 个（仅修改文件系统时间戳，未改动 EXIF 数据）。"
                : $"修正完成：成功 {fix.Ok} 个，失败 {fix.Failed} 个。\n" + string.Join("\n", fix.Errors.Take(5));

            // Re-scan to refresh the list's current vs. after times.
            var scan = await Task.Run(() => _service.Scan(_photos));
            RebuildItems(scan);
            StatsText = $"一致 {scan.MatchedCount} 个 · 需要修正 {scan.MismatchCount} 个 · 无拍摄日期 {scan.NoExifCount} 个";
            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A status filter option for the 拍摄时间修复 window.</summary>
public sealed record DateFixFilterOption(string Label, PhotoDateStatus? Status)
{
    public override string ToString() => Label;
}
