using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MyAlbum.Core.Data;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;
using MyAlbum_App.Services;
using Windows.Data.Json;

namespace MyAlbum_App.ViewModels;

/// <summary>一张待补 GPS 的照片在工具列表里的显示项。</summary>
public partial class GpsToolPhotoItem : ObservableObject
{
    public required GpnAssignment Assignment { get; init; }
    public required string File { get; init; }
    public required string TimeText { get; init; }
    public BitmapImage? ThumbImage { get; init; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string WarningText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasWarning { get; set; }

    /// <summary>提示会归类到哪个锚点，如 "→ 锚点 #3 · 03-27 18:57"（无锚点时 "需手动设置"）。</summary>
    [ObservableProperty]
    public partial string TargetText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>工具列表里的一个时间分组。</summary>
public sealed class GpsToolGroupItem
{
    public required GpsGroup Group { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public ObservableCollection<GpsToolPhotoItem> Photos { get; } = new();
    public bool CanChain => Group.Kind == GpsGroupKind.Auto;
}

/// <summary>分组间隔选项（供下拉框 DisplayMemberPath 使用）。</summary>
public sealed record GpsThresholdOption(string Label, TimeSpan Span);

/// <summary>
/// The "GPS 补全工具" view model: scans the library for photos missing GPS, groups them
/// by shooting-time continuity, and lets the user assign positions via chaining (copy the
/// nearest anchor) or by dragging on the map. Positions are written back to the DB and,
/// when ExifTool is available, to the source files with a ".original" backup.
/// </summary>
public partial class GpsToolViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions ToolJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly GpsThresholdOption[] ThresholdOptions =
    [
        new("15 分钟", TimeSpan.FromMinutes(15)),
        new("1 小时", TimeSpan.FromHours(1)),
        new("3 小时", TimeSpan.FromHours(3)),
        new("12 小时", TimeSpan.FromHours(12)),
        new("24 小时", TimeSpan.FromHours(24)),
    ];

    private readonly PhotoDatabase _db;
    private readonly ExifWriterService _exif;
    private readonly LibraryService _library;
    private readonly MetadataReaderService _reader;
    private readonly AppState _appState;
    private readonly GpsGroupingService _grouping = new();

    private List<PhotoRecord> _anchors = new();
    private List<GpnAssignment> _allAssignments = new();
    private readonly HashSet<long> _selectedIds = new();

    /// <summary>Stable 1-based number per anchor photo (sorted by shooting time), shared by the map pins and the list hints.</summary>
    private Dictionary<long, int> _anchorNumbers = new();

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasData { get; set; }

    [ObservableProperty]
    public partial int AnchorCount { get; set; }

    [ObservableProperty]
    public partial int GpnCount { get; set; }

    [ObservableProperty]
    public partial int ManualCount { get; set; }

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    /// <summary>True while 「保护原始照片」 is on — write-back buttons are disabled.</summary>
    [ObservableProperty]
    public partial bool IsWriteBlocked { get; set; }

    /// <summary>顶栏统计文本。</summary>
    public string StatsText => $"{AnchorCount} 张有 GPS · {GpnCount} 张缺 GPS · {SelectedCount} 已选";

    /// <summary>扫描范围起点（仅处理该日期及之后的照片；null = 不限）。</summary>
    [ObservableProperty]
    public partial DateTimeOffset? DateFrom { get; set; }

    /// <summary>扫描范围终点（仅处理该日期及之前的照片；null = 不限）。</summary>
    [ObservableProperty]
    public partial DateTimeOffset? DateTo { get; set; }

    partial void OnDateFromChanged(DateTimeOffset? value) => OnDateRangeChanged();
    partial void OnDateToChanged(DateTimeOffset? value) => OnDateRangeChanged();

    private void OnDateRangeChanged()
    {
        if (!IsBusy)
        {
            _ = ScanAsync();
        }
    }

    private (string? From, string? To) DateRangeStrings() =>
        (DateFrom?.ToString("yyyy-MM-dd"), DateTo?.ToString("yyyy-MM-dd"));

    private void ClearDateRange()
    {
        DateFrom = null;
        DateTo = null;
        _ = ScanAsync();
    }

    partial void OnAnchorCountChanged(int value) => OnPropertyChanged(nameof(StatsText));
    partial void OnGpnCountChanged(int value) => OnPropertyChanged(nameof(StatsText));
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(StatsText));

    /// <summary>当前已设置好位置、待写回的照片数。</summary>
    public int PendingWriteCount => _allAssignments.Count(a => a.AssignedLat is not null && a.AssignedLon is not null);

    [ObservableProperty]
    public partial ObservableCollection<GpsToolGroupItem> Groups { get; set; } = new();

    /// <summary>当前阈值（分钟），0..4 对应 ThresholdOptions。</summary>
    public int ThresholdIndex { get; private set; }

    public IReadOnlyList<GpsThresholdOption> Thresholds => ThresholdOptions;

    /// <summary>Page 注入：用于把工具载荷推给地图 JS。</summary>
    public Action<string>? PostJson { get; set; }

    public IAsyncRelayCommand ScanCommand { get; }
    public IRelayCommand<string> SetThresholdCommand { get; }
    public IRelayCommand SelectAutoClassifiableCommand { get; }
    public IRelayCommand SelectNeedsReviewCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }
    public IRelayCommand ClearDateRangeCommand { get; }
    public IAsyncRelayCommand WriteBackCommand { get; }
    public IAsyncRelayCommand WriteDbToFilesCommand { get; }

    public GpsToolViewModel(PhotoDatabase db, ExifWriterService exif, LibraryService library, MetadataReaderService reader, AppState appState)
    {
        _db = db;
        _exif = exif;
        _library = library;
        _reader = reader;
        _appState = appState;
        _appState.PropertyChanged += OnAppStatePropertyChanged;
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        SetThresholdCommand = new RelayCommand<string>(SetThreshold);
        SelectAutoClassifiableCommand = new RelayCommand(SelectAutoClassifiable);
        SelectNeedsReviewCommand = new RelayCommand(SelectNeedsReview);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        ClearDateRangeCommand = new RelayCommand(ClearDateRange);
        WriteBackCommand = new AsyncRelayCommand(WriteBackAsync, () => !IsBusy && !IsWriteBlocked);
        WriteDbToFilesCommand = new AsyncRelayCommand(WriteDbToFilesAsync, () => !IsBusy && !IsWriteBlocked);
        // 必须在命令创建之后再赋值，否则 OnIsWriteBlockedChanged 会对 null 命令 NotifyCanExecuteChanged 而崩溃。
        IsWriteBlocked = _appState.ProtectOriginalData;
    }

    private void OnAppStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.ProtectOriginalData))
        {
            IsWriteBlocked = _appState.ProtectOriginalData;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        WriteBackCommand.NotifyCanExecuteChanged();
        WriteDbToFilesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsWriteBlockedChanged(bool value)
    {
        WriteBackCommand.NotifyCanExecuteChanged();
        WriteDbToFilesCommand.NotifyCanExecuteChanged();
    }

    // ---------- 扫描与归类 ----------

    public async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        StatusText = "正在扫描照片…";
        try
        {
            var (from, to) = DateRangeStrings();
            var photos = await _db.QueryPhotosAsync(dateFrom: from, dateTo: to, limit: int.MaxValue);
            await RecoverMissingFileDataAsync(photos);
            var result = _grouping.Group(photos, ThresholdOptions[ThresholdIndex].Span);

            _anchors = photos.Where(p => p.GpsLatitude is not null && p.GpsLongitude is not null).ToList();
            _allAssignments = result.Groups.SelectMany(g => g.GpnItems).ToList();
            RebuildAnchorNumbers();

            AnchorCount = result.AnchorCount;
            GpnCount = result.GpnCount;
            ManualCount = result.Groups.Count(g => g.Kind == GpsGroupKind.Manual) + result.NoTimePhotos.Count;
            StatusText = from is null && to is null
                ? $"已找到 {result.AnchorCount} 张带 GPS 的照片作为锚点，{result.GpnCount} 张缺少 GPS"
                : $"已按时间范围 {from ?? "…"} ~ {to ?? "…"} 找到 {result.AnchorCount} 张锚点、{result.GpnCount} 张缺 GPS";

            BuildGroups(result);
            PushToolPayload();
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

    private void BuildGroups(GpsGroupingResult result)
    {
        Groups.Clear();
        foreach (var g in result.Groups)
        {
            var item = new GpsToolGroupItem
            {
                Group = g,
                Title = g.Kind == GpsGroupKind.Auto
                    ? $"可自动归类 · {RangeText(g.StartUtc, g.EndUtc)}"
                    : $"需手动设置 · {RangeText(g.StartUtc, g.EndUtc)}",
                Summary = $"{g.GpnItems.Count} 张待设置{(g.Kind == GpsGroupKind.Auto ? $" · {g.AnchorCount} 个锚点" : "")}",
            };
            foreach (var a in g.GpnItems)
            {
                var photo = a.Photo;
                item.Photos.Add(new GpsToolPhotoItem
                {
                    Assignment = a,
                    File = photo.FileName,
                    // TakenAtUtc is the EXIF wall-clock shooting time (local, Unspecified) —
                    // display it as-is; ToLocalTime() would assume UTC and shift by the TZ offset.
                    TimeText = photo.TakenAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "",
                    ThumbImage = photo.ThumbnailCachePath is not null && File.Exists(photo.ThumbnailCachePath)
                        ? new BitmapImage(new Uri(photo.ThumbnailCachePath))
                        : null,
                });
            }
            Groups.Add(item);
        }

        if (result.NoTimePhotos.Count > 0)
        {
            var group = new GpsToolGroupItem
            {
                Group = new GpsGroup
                {
                    Kind = GpsGroupKind.Manual,
                    GpnItems = result.NoTimePhotos.Select(p => new GpnAssignment { Photo = p }).ToList(),
                },
                Title = "无拍摄时间（无法自动归类）",
                Summary = $"{result.NoTimePhotos.Count} 张",
            };
            foreach (var p in result.NoTimePhotos)
            {
                group.Photos.Add(new GpsToolPhotoItem
                {
                    Assignment = new GpnAssignment { Photo = p },
                    File = p.FileName,
                    TimeText = "未知时间",
                    ThumbImage = p.ThumbnailCachePath is not null && File.Exists(p.ThumbnailCachePath)
                        ? new BitmapImage(new Uri(p.ThumbnailCachePath))
                        : null,
                });
            }
            Groups.Add(group);
        }

        foreach (var item in Groups.SelectMany(g => g.Photos))
        {
            item.IsSelected = _selectedIds.Contains(item.Assignment.Photo.Id);
            RefreshItemStatus(item);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GpsToolPhotoItem.IsSelected))
                {
                    ToggleSelection(item);
                }
            };
        }
    }

    /// <summary>
    /// Photos whose DB row lacks a shooting time and/or GPS usually still carry them in the
    /// file's EXIF — re-read the file so the tool groups/displays by the real data instead of
    /// treating GPS-having photos as "需补 GPS" (which would later overwrite their real
    /// coordinates with an anchor's position). The recovered data is persisted via
    /// <see cref="LibraryService.RefreshMetadataAsync"/>.
    /// </summary>
    private async Task RecoverMissingFileDataAsync(List<PhotoRecord> photos)
    {
        var missing = photos
            .Where(p => p.TakenAtUtc is null || (p.GpsLatitude is null && p.GpsLongitude is null))
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (p, _) =>
        {
            try
            {
                var fresh = await _library.RefreshMetadataAsync(p.FilePath);
                if (fresh is null)
                {
                    return;
                }
                if (fresh.TakenAtUtc is { } takenAt)
                {
                    p.TakenAtUtc = takenAt;
                }
                if (fresh.GpsLatitude is { } lat && fresh.GpsLongitude is { } lon)
                {
                    p.GpsLatitude = lat;
                    p.GpsLongitude = lon;
                    p.GpsAltitude = fresh.GpsAltitude;
                }
            }
            catch
            {
                // best effort: keep the DB values if the file cannot be read
            }
        });
    }

    private void RefreshItemStatus(GpsToolPhotoItem item)
    {
        var a = item.Assignment;
        var warnings = new List<string>();
        if (a.ManuallySet)
        {
            item.StatusText = "已手动设置";
        }
        else if (a.NearestAnchor is not null)
        {
            var gap = a.TimeGapSeconds is { } s ? (int)Math.Round(Math.Abs(s) / 60.0) : 0;
            item.StatusText = $"自动 · 距锚点 {gap} 分钟";
            if (a.NeedsReview)
            {
                warnings.Add("时间间隔较远");
            }
            if (a.FilenameCircularDistance is { } d && d > GpsGroupingService.FilenameWarnThreshold)
            {
                warnings.Add("序号断层");
            }
        }
        else
        {
            item.StatusText = "待设置";
        }

        item.TargetText = a.NearestAnchor is { } anchor
            ? $"→ 锚点 #{_anchorNumbers.GetValueOrDefault(anchor.Id)} · {FormatAnchorTime(anchor)}"
            : "需手动设置";

        item.WarningText = string.Join(" · ", warnings);
        item.HasWarning = warnings.Count > 0;
    }

    /// <summary>Numbers every GPS anchor 1..N by shooting time (stable, shared by the map pins and list hints).</summary>
    private void RebuildAnchorNumbers()
    {
        _anchorNumbers = _anchors
            .OrderBy(a => a.TakenAtUtc ?? DateTime.MinValue)
            .Select((a, i) => (a.Id, Num: i + 1))
            .ToDictionary(x => x.Id, x => x.Num);
    }

    private static string FormatAnchorTime(PhotoRecord anchor) =>
        anchor.TakenAtUtc?.ToString("MM-dd HH:mm") ?? "--";

    private static string RangeText(DateTime? start, DateTime? end)
    {
        if (start is null && end is null)
        {
            return "—";
        }
        string s = start?.ToString("MM-dd HH:mm") ?? "?";
        string e = end?.ToString("MM-dd HH:mm") ?? "?";
        return s == e ? s : $"{s} ~ {e}";
    }

    // ---------- 阈值 ----------

    public void SetThreshold(string? label)
    {
        int index = Array.FindIndex(ThresholdOptions, o => o.Label == label);
        if (index < 0)
        {
            return;
        }
        ThresholdIndex = index;
        _ = RegroupAsync();
    }

    private async Task RegroupAsync()
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            var (from, to) = DateRangeStrings();
            var photos = await _db.QueryPhotosAsync(dateFrom: from, dateTo: to, limit: int.MaxValue);
            await RecoverMissingFileDataAsync(photos);
            var result = _grouping.Group(photos, ThresholdOptions[ThresholdIndex].Span);
            _anchors = photos.Where(p => p.GpsLatitude is not null && p.GpsLongitude is not null).ToList();
            _allAssignments = result.Groups.SelectMany(g => g.GpnItems).ToList();
            RebuildAnchorNumbers();
            AnchorCount = result.AnchorCount;
            GpnCount = result.GpnCount;
            ManualCount = result.Groups.Count(g => g.Kind == GpsGroupKind.Manual) + result.NoTimePhotos.Count;
            BuildGroups(result);
            PushToolPayload();
        }
        catch (Exception ex)
        {
            StatusText = "重新归类失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------- 选择 ----------

    private void ToggleSelection(GpsToolPhotoItem item)
    {
        if (item.IsSelected)
        {
            _selectedIds.Add(item.Assignment.Photo.Id);
        }
        else
        {
            _selectedIds.Remove(item.Assignment.Photo.Id);
        }
        SelectedCount = _selectedIds.Count;
        SelectedSummaryChanged();
        PushToolPayload();
    }

    public void ClearSelection()
    {
        _selectedIds.Clear();
        SelectedCount = 0;
        SelectedSummaryChanged();
        foreach (var item in Groups.SelectMany(g => g.Photos))
        {
            item.IsSelected = false;
        }
        PushToolPayload();
    }

    public void SelectNeedsReview()
    {
        foreach (var item in Groups.SelectMany(g => g.Photos))
        {
            bool want = item.HasWarning || item.Assignment.NearestAnchor is null;
            item.IsSelected = want;
            if (want)
            {
                _selectedIds.Add(item.Assignment.Photo.Id);
            }
            else
            {
                _selectedIds.Remove(item.Assignment.Photo.Id);
            }
        }
        SelectedCount = _selectedIds.Count;
        SelectedSummaryChanged();
        PushToolPayload();
    }

    private void SelectedSummaryChanged()
    {
        StatusText = SelectedCount == 0
            ? "在右侧列表勾选照片（或点「自动选中可归类」），再点「提交并写回 GPS」"
            : $"已选 {SelectedCount} 张：点地图锚点/拖红钉手动定位，或直接「提交并写回 GPS」按最近锚点归类";
    }

    // ---------- 选择与提交 ----------

    /// <summary>只做选择：勾选所有能自动归类（有最近锚点）的照片，不改变任何位置。</summary>
    public void SelectAutoClassifiable()
    {
        int selected = 0;
        foreach (var item in Groups.SelectMany(g => g.Photos))
        {
            bool auto = item.Assignment.NearestAnchor is not null;
            item.IsSelected = auto;
            if (auto)
            {
                _selectedIds.Add(item.Assignment.Photo.Id);
                selected++;
            }
            else
            {
                _selectedIds.Remove(item.Assignment.Photo.Id);
            }
        }
        SelectedCount = _selectedIds.Count;
        StatusText = selected > 0
            ? $"已选中 {selected} 张可自动归类的照片，确认后点「提交并写回 GPS」"
            : "没有可自动归类的照片（都缺最近锚点）";
        PushToolPayload();
    }

    /// <summary>
    /// 提交并写回：先对「已选中的、有最近锚点、且未手动定位」的照片应用链式归类，
    /// 再写回所有已定位的照片（含手动拖拽/点锚点设置的）。
    /// </summary>
    public async Task WriteBackAsync()
    {
        if (OriginalDataProtection.IsEnabled)
        {
            StatusText = OriginalDataProtection.BlockedMessage;
            return;
        }

        int chained = 0;
        foreach (var a in SelectedAssignments())
        {
            if (a.AssignedLat is null && a.AssignedLon is null && a.NearestAnchor is { } anchor)
            {
                ApplyChain(a, anchor);
                chained++;
            }
        }
        foreach (var item in Groups.SelectMany(g => g.Photos))
        {
            if (_selectedIds.Contains(item.Assignment.Photo.Id))
            {
                RefreshItemStatus(item);
            }
        }
        PushToolPayload(fit: false);

        var targets = _allAssignments.Where(a => a.AssignedLat is not null && a.AssignedLon is not null).ToList();
        if (targets.Count == 0)
        {
            StatusText = "没有可写回的照片：先勾选照片（或点「自动选中可归类」），再点地图锚点/拖红钉定位，然后提交。";
            return;
        }

        StatusText = chained > 0 ? $"已链式归类 {chained} 张，正在写回…" : "正在写回…";
        await WriteBackCoreAsync(targets);
    }

    private static void ApplyChain(GpnAssignment a, PhotoRecord anchor)
    {
        a.AssignedLat = anchor.GpsLatitude;
        a.AssignedLon = anchor.GpsLongitude;
        a.AssignedAlt = anchor.GpsAltitude;
        a.AssignedPlace = GpsPlaceData.From(anchor);
        a.ManuallySet = false;
    }

    private IEnumerable<GpnAssignment> SelectedAssignments() =>
        _allAssignments.Where(a => _selectedIds.Contains(a.Photo.Id));

    // ---------- 地图载荷 ----------

    private sealed record ToolAnchor(long Id, int Num, double Lat, double Lon, string File, string Time);
    private sealed record ToolSelected(long Id, double? Lat, double? Lon, string File);

    private const int MaxToolAnchors = 800;

    public void PushToolPayload(bool fit = true)
    {
        if (PostJson is null || _anchors.Count == 0 && _selectedIds.Count == 0)
        {
            return;
        }
        // Always show the anchors in shooting-time order so their map numbers are
        // sequential (1..N) and match the "→ 锚点 #N" hints in the photo list.
        var anchors = _anchors
            .OrderBy(a => a.TakenAtUtc ?? DateTime.MinValue)
            .Take(MaxToolAnchors)
            .Select(a => new ToolAnchor(
                a.Id,
                _anchorNumbers.GetValueOrDefault(a.Id),
                a.GpsLatitude!.Value,
                a.GpsLongitude!.Value,
                a.FileName,
                FormatAnchorTime(a)))
            .ToList();
        var selected = SelectedAssignments()
            .Select(a => new ToolSelected(a.Photo.Id, a.AssignedLat, a.AssignedLon, a.Photo.FileName))
            .ToList();
        var payload = new
        {
            type = "tool",
            fit,
            anchors,
            selected,
        };
        PostJson(JsonSerializer.Serialize(payload, ToolJson));
    }

    /// <summary>处理地图 JS 回传的消息（moved / anchor）。</summary>
    public void HandleToolMessage(JsonObject root)
    {
        if (!root.TryGetValue("type", out var typeVal))
        {
            return;
        }
        string type = typeVal.GetString();
        if (type == "moved" && root.TryGetValue("lat", out var latV) && root.TryGetValue("lon", out var lonV))
        {
            double lat = latV.GetNumber();
            double lon = lonV.GetNumber();
            foreach (var a in SelectedAssignments())
            {
                a.AssignedLat = lat;
                a.AssignedLon = lon;
                a.AssignedAlt = null;
                a.ManuallySet = true;
            }
            foreach (var item in Groups.SelectMany(g => g.Photos))
            {
                if (_selectedIds.Contains(item.Assignment.Photo.Id))
                {
                    RefreshItemStatus(item);
                }
            }
            StatusText = $"已将 {_selectedIds.Count} 张选中照片设为 ({lat:0.00000}, {lon:0.00000})";
        }
        else if (type == "anchor" && root.TryGetValue("id", out var idVal))
        {
            long id = (long)idVal.GetNumber();
            var anchor = _anchors.FirstOrDefault(a => a.Id == id);
            if (anchor is not null)
            {
                foreach (var a in SelectedAssignments())
                {
                    a.AssignedLat = anchor.GpsLatitude;
                    a.AssignedLon = anchor.GpsLongitude;
                    a.AssignedAlt = anchor.GpsAltitude;
                    a.AssignedPlace = GpsPlaceData.From(anchor);
                    a.ManuallySet = true;
                }
                foreach (var item in Groups.SelectMany(g => g.Photos))
                {
                    if (_selectedIds.Contains(item.Assignment.Photo.Id))
                    {
                        RefreshItemStatus(item);
                    }
                }
                StatusText = $"已将 {_selectedIds.Count} 张选中照片设为锚点 {anchor.FileName} 的位置";
                PushToolPayload(fit: false);
            }
        }
    }

    // ---------- 写回 ----------

    private async Task WriteBackCoreAsync(List<GpnAssignment> targets)
    {
        IsBusy = true;
        Progress = 0;
        ProgressText = "";
        try
        {
            if (!_exif.IsAvailable)
            {
                await _db.BulkSetGpsWithPlaceAsync(targets.Select(a => (
                    a.Photo.Id, a.AssignedLat!.Value, a.AssignedLon!.Value, a.AssignedAlt, a.AssignedPlace)).ToList());
                StatusText = $"已将 {targets.Count} 张照片的位置写入索引（未检测到 ExifTool，未写回源文件）";
                return;
            }

            var edits = targets.Select(a => new ExifEditOptions
            {
                FilePath = a.Photo.FilePath,
                GpsLatitude = a.AssignedLat,
                GpsLongitude = a.AssignedLon,
                GpsAltitude = a.AssignedAlt,
            }).ToList();

            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                Progress = p.Total == 0 ? 0 : p.Done / (double)p.Total;
                ProgressText = $"已写回 {p.Done}/{p.Total}";
            });

            var results = await _exif.WriteBatchAsync(edits, progress, keepOriginalBackup: true);
            int ok = results.Count(r => r.Success);
            int failed = results.Count - ok;
            for (int i = 0; i < results.Count; i++)
            {
                if (!results[i].Success)
                {
                    continue;
                }
                var a = targets[i];
                await _library.RefreshMetadataAsync(a.Photo.FilePath);
                // The anchor's reverse-geocoded place text is app-side data (not in EXIF) —
                // re-apply it after the file re-read so the DB keeps the copied address.
                await _db.BulkSetGpsWithPlaceAsync([
                    (a.Photo.Id, a.AssignedLat!.Value, a.AssignedLon!.Value, a.AssignedAlt, a.AssignedPlace)]);
            }
            ProgressText = "";
            StatusText = failed == 0
                ? $"已为 {ok} 张照片写回 GPS（源文件旁保留了 .original 备份）"
                : $"完成：成功 {ok}，失败 {failed}（源文件旁保留了 .original 备份）";
        }
        catch (Exception ex)
        {
            StatusText = "写回失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把数据库索引里已有的 GPS 写回原始照片文件——用于之前未装 ExifTool、GPS 只写进了数据库的情况。
    /// </summary>
    public async Task WriteDbToFilesAsync()
    {
        if (OriginalDataProtection.IsEnabled)
        {
            StatusText = OriginalDataProtection.BlockedMessage;
            return;
        }

        if (!_exif.IsAvailable)
        {
            StatusText = "未检测到 ExifTool，无法写回源文件。请先在「设置」页下载安装 ExifTool 后再试。";
            return;
        }

        IsBusy = true;
        StatusText = "正在读取库内带 GPS 的照片…";
        Progress = 0;
        ProgressText = "";
        try
        {
            var photos = await _db.GetGpsPhotosAsync();
            if (photos.Count == 0)
            {
                StatusText = "数据库中没有带 GPS 的照片可写回。";
                return;
            }

            StatusText = "正在比对源文件 GPS…";
            // 只处理源文件里还没有 GPS 的照片，已有 GPS 的保持不动。
            var toWrite = new System.Collections.Concurrent.ConcurrentBag<PhotoRecord>();
            await Task.Run(() =>
            {
                Parallel.ForEach(photos.Where(p => !p.IsMissing), p =>
                {
                    try
                    {
                        var file = _reader.Read(p.FilePath);
                        if (file.GpsLatitude is null || file.GpsLongitude is null)
                        {
                            toWrite.Add(p);
                        }
                    }
                    catch
                    {
                        // 读不到 EXIF 的保守跳过，避免误写/破坏
                    }
                });
            });

            var targets = toWrite.ToList();
            if (targets.Count == 0)
            {
                StatusText = $"全部 {photos.Count} 张照片的源文件已带 GPS，无需写回。";
                return;
            }

            var edits = targets.Select(p => new ExifEditOptions
            {
                FilePath = p.FilePath,
                GpsLatitude = p.GpsLatitude,
                GpsLongitude = p.GpsLongitude,
                GpsAltitude = p.GpsAltitude,
            }).ToList();

            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                Progress = p.Total == 0 ? 0 : p.Done / (double)p.Total;
                ProgressText = $"已写回 {p.Done}/{p.Total}";
            });

            var results = await _exif.WriteBatchAsync(edits, progress, keepOriginalBackup: true);
            int ok = results.Count(r => r.Success);
            int failed = results.Count - ok;
            foreach (var r in results.Where(r => r.Success))
            {
                await _library.RefreshMetadataAsync(r.FilePath);
            }
            ProgressText = "";
            StatusText = failed == 0
                ? $"已把 {ok} 张库内 GPS 写回源文件（保留了 .original 备份）。"
                : $"完成：成功 {ok}，失败 {failed}（保留了 .original 备份）。";
        }
        catch (Exception ex)
        {
            StatusText = "写回失败: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
