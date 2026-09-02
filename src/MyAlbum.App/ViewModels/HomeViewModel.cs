using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MyAlbum.Core.Data;
using MyAlbum.Core.Models;
using MyAlbum.Core.Services;
using MyAlbum_App.Services;

namespace MyAlbum_App.ViewModels;

/// <summary>Center-area view mode for the home page.</summary>
public enum HomeViewMode
{
    Grid,
    Calendar,
}

/// <summary>Zoom level of the hierarchical calendar (year → month → day).</summary>
public enum CalendarZoom
{
    Year,
    Month,
    Day,
}

public partial class HomeViewModel : ObservableObject
{
    private readonly PhotoDatabase _db;
    private readonly ThumbnailService _thumbs;
    private readonly ExifWriterService _exif;
    private readonly LibraryService _library;
    private readonly AppState _appState;
    private readonly FolderWatcherService _watcher;
    private readonly ReverseGeocodeService _geocoder;
    private readonly AddressNormalizeService _addressNormalizer;

    public ObservableCollection<FolderTreeNode> FolderTree { get; } = new();
    public ObservableCollection<LocationNode> LocationTree { get; } = new();
    public ObservableCollection<CameraFilterItem> Cameras { get; } = new();
    public ObservableCollection<PhotoGridItem> Photos { get; } = new();

    /// <summary>True while <see cref="Photos"/> is being repopulated in bulk; subscribers should
    /// defer expensive per-item work (e.g. the timeline ruler) until <see cref="PhotosLoaded"/>.</summary>
    public bool IsBulkUpdating { get; private set; }

    /// <summary>Raised once after a bulk photo load replaces the grid contents.</summary>
    public event Action? PhotosLoaded;
    public ObservableCollection<CalendarCell> CalendarCells { get; } = new();
    public ObservableCollection<RatingOption> RatingOptions { get; } = new()
    {
        new RatingOption("全部评分", 0),
        new RatingOption("1★ 以上", 1),
        new RatingOption("2★ 以上", 2),
        new RatingOption("3★ 以上", 3),
        new RatingOption("4★ 以上", 4),
        new RatingOption("5★", 5),
    };
    public ObservableCollection<TagFilterItem> ManualTags { get; } = new();
    public ObservableCollection<TagFilterItem> AiTags { get; } = new();

    /// <summary>Tags of the currently selected photo (right panel).</summary>
    public ObservableCollection<TagRecord> PhotoTags { get; } = new();

    /// <summary>Icon+label+value rows of the right-panel info section (driven by Settings toggles).</summary>
    public ObservableCollection<InfoRow> InfoRows { get; } = new();

    /// <summary>Justified photo-grid source: interleaved group headers + photo rows.
    /// Replaced wholesale per repack so the ItemsRepeater sees one Reset instead of
    /// 30k incremental Add notifications (each of which can trigger a layout pass).</summary>
    [ObservableProperty]
    public partial ObservableCollection<object> GridSource { get; set; } = new();

    [ObservableProperty]
    public partial double TileSize { get; set; } = 150;

    private string _groupMode = "day";

    /// <summary>Last populated photo list, kept for lazy timeline construction.</summary>
    private List<PhotoGridItem>? _lastItems;

    public bool IsPhotoView => ViewMode == HomeViewMode.Grid;
    public bool IsCalendarView => ViewMode == HomeViewMode.Calendar;
    public bool IsRulerVisible => IsPhotoView;

    /// <summary>Transient single-day drill-down (set by calendar/timeline day click). Does not
    /// mutate the persistent DateFrom/DateTo filter so the calendar/timeline keep their context.</summary>
    private DateTime? _dayFocus;
    private HomeViewMode _drillFrom = HomeViewMode.Calendar;
    public bool HasDayFocus => _dayFocus.HasValue;

    partial void OnViewModeChanged(HomeViewMode value)
    {
        OnPropertyChanged(nameof(IsPhotoView));
        OnPropertyChanged(nameof(IsCalendarView));
        OnPropertyChanged(nameof(IsRulerVisible));
        if (value != HomeViewMode.Grid)
        {
            ClearDayFocus(silent: true);
        }
        // Re-query so the date range / filter reflects the (possibly cleared) day focus,
        // then the current view is (re)built at the end of PopulatePhotosAsync.
        _ = ApplyFilterAsync();
    }

    [ObservableProperty]
    public partial FolderTreeNode? SelectedFolderNode { get; set; }

    [ObservableProperty]
    public partial LocationNode? SelectedLocationNode { get; set; }

    /// <summary>Whole 地点 section collapsed/expanded.</summary>
    [ObservableProperty]
    public partial bool IsLocationSectionOpen { get; set; } = true;

    [ObservableProperty]
    public partial CameraFilterItem? SelectedCamera { get; set; }

    [ObservableProperty]
    public partial TagFilterItem? SelectedTag { get; set; }

    [ObservableProperty]
    public partial int RatingMin { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string DateFrom { get; set; } = "";

    [ObservableProperty]
    public partial string DateTo { get; set; } = "";

    [ObservableProperty]
    public partial HomeViewMode ViewMode { get; set; } = HomeViewMode.Grid;

    [ObservableProperty]
    public partial bool IsLeftPanelOpen { get; set; } = true;

    [ObservableProperty]
    public partial bool IsRightPanelOpen { get; set; } = true;

    [ObservableProperty]
    public partial PhotoGridItem? SelectedPhoto { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    /// <summary>Date range of the currently displayed photos (drives the timeline ruler).</summary>
    [ObservableProperty]
    public partial DateTime? MinDate { get; set; }

    [ObservableProperty]
    public partial DateTime? MaxDate { get; set; }

    // ---- Calendar view ----
    [ObservableProperty]
    public partial int CalendarYear { get; set; }

    [ObservableProperty]
    public partial int CalendarMonth { get; set; }

    [ObservableProperty]
    public partial CalendarZoom CalendarZoomLevel { get; set; } = CalendarZoom.Year;

    public string CalendarTitle => CalendarYear > 0 ? $"{CalendarYear}年{CalendarMonth}月" : "";
    public string CalendarYearLabel => CalendarYear > 0 ? $"{CalendarYear}年" : "";
    public string CalendarMonthLabel => CalendarMonth > 0 ? $"{CalendarMonth}月" : "";

    /// <summary>Breadcrumb-style scope label shown in the calendar header (e.g. "2026年 8月").</summary>
    [ObservableProperty]
    public partial string CalendarScopeText { get; set; } = "年份";

    public bool IsYearZoom => CalendarZoomLevel == CalendarZoom.Year;
    public bool IsMonthZoom => CalendarZoomLevel == CalendarZoom.Month;
    public bool IsDayZoom => CalendarZoomLevel == CalendarZoom.Day;

    partial void OnCalendarYearChanged(int value) => OnPropertyChanged(nameof(CalendarTitle));
    partial void OnCalendarMonthChanged(int value)
    {
        OnPropertyChanged(nameof(CalendarTitle));
        OnPropertyChanged(nameof(CalendarMonthLabel));
    }
    partial void OnCalendarZoomLevelChanged(CalendarZoom value)
    {
        OnPropertyChanged(nameof(IsYearZoom));
        OnPropertyChanged(nameof(IsMonthZoom));
        OnPropertyChanged(nameof(IsDayZoom));
        PrevMonthCommand?.NotifyCanExecuteChanged();
        NextMonthCommand?.NotifyCanExecuteChanged();
        GoUpCommand?.NotifyCanExecuteChanged();
    }

    // Right panel
    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial BitmapImage? PreviewImage { get; set; }

    [ObservableProperty]
    public partial string PreviewFileName { get; set; } = "";

    [ObservableProperty]
    public partial string PreviewCamera { get; set; } = "";

    [ObservableProperty]
    public partial string PreviewTaken { get; set; } = "";

    [ObservableProperty]
    public partial string PreviewLens { get; set; } = "";

    [ObservableProperty]
    public partial string PreviewExposure { get; set; } = "";

    [ObservableProperty]
    public partial string PreviewDimensions { get; set; } = "";

    [ObservableProperty]
    public partial string PreviewGps { get; set; } = "";

    /// <summary>LLM 规范化后的五级拍摄地址（国家 · 省 · 市 · 区县 · 地标）。</summary>
    [ObservableProperty]
    public partial string PreviewLocation { get; set; } = "";

    // Whether each right-panel field has a value (drives per-field visibility when its
    // Settings toggle is on). Kept separate so empty fields never render an empty line.
    [ObservableProperty]
    public partial bool HasFileName { get; set; }

    [ObservableProperty]
    public partial bool HasTakenTime { get; set; }

    [ObservableProperty]
    public partial bool HasCamera { get; set; }

    [ObservableProperty]
    public partial bool HasLens { get; set; }

    [ObservableProperty]
    public partial bool HasExposure { get; set; }

    [ObservableProperty]
    public partial bool HasDimensions { get; set; }

    [ObservableProperty]
    public partial bool HasGps { get; set; }

    /// <summary>True when a normalized five-level shooting address exists.</summary>
    [ObservableProperty]
    public partial bool HasLocation { get; set; }

    /// <summary>True while the per-photo "refresh location" action is running.</summary>
    [ObservableProperty]
    public partial bool IsRefreshingLocation { get; set; }

    [ObservableProperty]
    public partial int Rating { get; set; }

    /// <summary>Re-evaluates the right-panel info rows from the Settings toggles + current selection.</summary>
    private void RebuildInfoRows()
    {
        InfoRows.Clear();
        if (!HasSelection)
        {
            return;
        }
        void Add(string glyph, string label, bool show, string value)
        {
            if (show && !string.IsNullOrEmpty(value))
            {
                InfoRows.Add(new InfoRow(glyph, label, value));
            }
        }
        Add("\uE823", "拍摄时间", _appState.ShowTakenTime, PreviewTaken);
        Add("\uE714", "相机", _appState.ShowCamera, PreviewCamera);
        Add("\uE7C1", "镜头", _appState.ShowLens, PreviewLens);
        Add("\uE950", "曝光", _appState.ShowExposure, PreviewExposure);
        Add("\uE8E8", "尺寸", _appState.ShowDimensions, PreviewDimensions);
        Add("\uE707", "GPS", _appState.ShowGps, PreviewGps);
        Add("\uE707", "拍摄地址", _appState.ShowLocation, PreviewLocation);
    }

    /// <summary>LLM 规范化后的五级地址显示串（国家 · 省 · 市 · 区县 · 地标）。</summary>
    private static string BuildLocationText(MyAlbum.Core.Models.PhotoRecord p) => string.Join(" · ",
        new[] { p.PlaceCountry, p.PlaceProvince, p.PlaceCity, p.PlaceDistrict, p.PlaceLandmark }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Re-evaluates the right-panel info rows when the Settings page changes them.</summary>
    public void RefreshFieldVisibility() => RebuildInfoRows();

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand RefreshLocationCommand { get; }
    public IRelayCommand SetRatingCommand { get; }
    public IRelayCommand SetViewCommand { get; }
    public IRelayCommand ClearFiltersCommand { get; }
    public IRelayCommand ToggleLeftPanelCommand { get; }
    public IRelayCommand ToggleRightPanelCommand { get; }
    public IRelayCommand PrevMonthCommand { get; }
    public IRelayCommand NextMonthCommand { get; }
    public IRelayCommand GoToNewestCommand { get; }
    public IRelayCommand ShowYearCommand { get; }
    public IRelayCommand ShowMonthCommand { get; }
    public IRelayCommand GoUpCommand { get; }
    public IRelayCommand GoToYearCommand { get; }
    public IRelayCommand ClearDayFocusCommand { get; }

    private bool _initializing;
    private string? _activeAlbumName;
    private long? _activePersonId;
    private string? _activePersonName;

    /// <summary>Increments on every selection so stale async preview loads never overwrite a newer one.</summary>
    private int _previewRequestVersion;

    /// <summary>Debounces rapid grid clicks so only the last selection triggers a preview load.</summary>
    private CancellationTokenSource? _selectDebounce;
    private CancellationTokenSource? _thumbGenCts;

    public LibraryFilter CurrentFilter
    {
        get
        {
            var filter = new LibraryFilter
            {
                FolderPath = SelectedFolderNode?.IsAllPhotos == true ? null : SelectedFolderNode?.Path,
                CameraModel = SelectedCamera?.Model,
                RatingMin = RatingMin > 0 ? RatingMin : null,
                TagName = SelectedTag?.Name,
                SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                DateFrom = string.IsNullOrWhiteSpace(DateFrom) ? null : DateFrom,
                DateTo = string.IsNullOrWhiteSpace(DateTo) ? null : DateTo,
                // 地点：LLM 规范后的五级地址树（国家→省→市），「全部地点」根节点 Country 为空表示不筛。
                PlaceCountry = SelectedLocationNode?.Country,
                PlaceProvince = SelectedLocationNode?.Province,
                PlaceCity = SelectedLocationNode?.City,
            };
            return filter;
        }
    }

    public HomeViewModel(PhotoDatabase db, ThumbnailService thumbs, ExifWriterService exif, LibraryService library, AppState appState, FolderWatcherService watcher, ReverseGeocodeService geocoder, AddressNormalizeService addressNormalizer)
    {
        _db = db;
        _thumbs = thumbs;
        _exif = exif;
        _library = library;
        _appState = appState;
        _watcher = watcher;
        _geocoder = geocoder;
        _addressNormalizer = addressNormalizer;
        _watcher.LibraryChanged += OnLibraryChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshAndScanAsync, () => !IsScanning);
        RefreshLocationCommand = new AsyncRelayCommand(RefreshLocationAsync, () => HasGps && !IsRefreshingLocation);
        SetRatingCommand = new RelayCommand<object?>(p => SetRating(p));
        SetViewCommand = new RelayCommand<object?>(v => ViewMode = ParseViewMode(v));
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ToggleLeftPanelCommand = new RelayCommand(() => IsLeftPanelOpen = !IsLeftPanelOpen);
        ToggleRightPanelCommand = new RelayCommand(() => IsRightPanelOpen = !IsRightPanelOpen);
        PrevMonthCommand = new RelayCommand(ShiftPeriodBackward);
        NextMonthCommand = new RelayCommand(ShiftPeriodForward);
        GoToNewestCommand = new RelayCommand(GoToNewest);
        ShowYearCommand = new RelayCommand<object?>(v => ShowYear(ToInt(v)));
        ShowMonthCommand = new RelayCommand<object?>(v => ShowMonth(ToInt(v)));
        GoUpCommand = new RelayCommand(GoUp, () => !IsYearZoom);
        GoToYearCommand = new RelayCommand(GoToYear);
        ClearDayFocusCommand = new RelayCommand(() => ClearDayFocus(silent: false));

        _appState.PropertyChanged += OnAppStatePropertyChanged;
    }

    private void OnAppStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName?.StartsWith("Show", StringComparison.Ordinal) == true)
        {
            RebuildInfoRows();
        }
    }

    private CancellationTokenSource? _libraryChangedDebounce;

    /// <summary>
    /// Folder watcher indexed a new / removed / renamed file. Debounce and marshal to the
    /// UI thread so the grid, counts and the timeline ruler adapt automatically (e.g. when
    /// older photos are added to a watched folder).
    /// </summary>
    private void OnLibraryChanged()
    {
        _libraryChangedDebounce?.Cancel();
        _libraryChangedDebounce = new CancellationTokenSource();
        var token = _libraryChangedDebounce.Token;
        _ = App.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(800, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }
                await RefreshAsync();
            }
            catch (OperationCanceledException)
            {
                // a newer change superseded this refresh
            }
        });
    }

    private bool _initialized;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        await App.DatabaseReady;
        _initializing = true;
        try
        {
            // 串行加载：侧栏与网格各自驱动独立的 ItemsRepeater 布局，并行会在两个
            // ObservableCollection 同时变更时触发 COM 竞态（0x80004005）。
            await LoadSidebarAsync();
            await ApplyFilterAsync();
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>
    /// Re-reads the normalized five-level address (PlaceCountry…PlaceLandmark) for the photos
    /// already loaded in the grid and updates their in-memory records. The home page is cached
    /// (<see cref="NavigationCacheMode.Required"/>) and does not reload photos on navigation, so
    /// an out-of-band bulk write (e.g. the LLM address-normalization pass on the AI page) would
    /// otherwise leave stale empty place fields until the app restarts. Called on each navigation.
    /// </summary>
    public async Task RefreshPlaceAddressesAsync()
    {
        if (Photos.Count == 0)
        {
            return;
        }
        var ids = Photos.Select(p => p.Photo.Id).ToList();
        var addresses = await _db.GetPlaceAddressesAsync(ids);
        if (addresses.Count == 0)
        {
            return;
        }
        foreach (var item in Photos)
        {
            if (addresses.TryGetValue(item.Photo.Id, out var addr))
            {
                item.Photo.PlaceCountry = addr.Country;
                item.Photo.PlaceProvince = addr.Province;
                item.Photo.PlaceCity = addr.City;
                item.Photo.PlaceDistrict = addr.District;
                item.Photo.PlaceLandmark = addr.Landmark;
            }
        }
        // Refresh the right-panel preview if the selected photo was among the updated ones.
        if (SelectedPhoto is not null && addresses.ContainsKey(SelectedPhoto.Photo.Id))
        {
            await LoadPreviewAsync(SelectedPhoto);
        }
    }

    partial void OnSelectedFolderNodeChanged(FolderTreeNode? value) => OnFilterChanged();
    partial void OnSelectedCameraChanged(CameraFilterItem? value) => OnFilterChanged();
    partial void OnSelectedTagChanged(TagFilterItem? value) => OnFilterChanged();
    partial void OnSelectedLocationNodeChanged(LocationNode? value) => OnFilterChanged();
    partial void OnRatingMinChanged(int value) => OnFilterChanged();

    private void OnFilterChanged()
    {
        _activeAlbumName = null;
        if (!_initializing)
        {
            _ = ApplyFilterAsync();
        }
    }

    partial void OnSelectedPhotoChanged(PhotoGridItem? value)
    {
        _ = LoadPreviewAsync(value);
    }

    /// <summary>
    /// Selects a photo from the grid/timeline. Re-selecting the same item still forces
    /// a refresh, and rapid clicks are debounced so only the last selection loads.
    /// </summary>
    public void SelectPhoto(PhotoGridItem item)
    {
        _selectDebounce?.Dispose();
        _selectDebounce = new CancellationTokenSource();
        var token = _selectDebounce.Token;
        _ = Task.Delay(120, token).ContinueWith(_ =>
        {
            App.DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
                if (!ReferenceEquals(SelectedPhoto, item))
                {
                    SelectedPhoto = item;
                }
                else
                {
                    _ = LoadPreviewAsync(item);
                }
            });
        }, token, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Ctrl + mouse wheel zoom: adjusts row height and switches day / month / year grouping.
    /// Tile widths follow each photo's aspect ratio (masonry, same row height).</summary>
    public void ChangeTileSize(int direction)
    {
        TileSize = Math.Clamp(TileSize + direction * 16, 56, 288);
        foreach (var item in Photos)
        {
            item.TileSize = TileSize;
        }

        string mode = TileSize >= 128 ? "day" : TileSize >= 80 ? "month" : "year";
        if (mode != _groupMode)
        {
            _groupMode = mode;
        }
        RebuildGridRows();
    }

    // ---- 自适应瀑布流网格：行等高、整行铺满（Windows 照片风格） ----

    private double _gridWidth;
    private readonly Dictionary<int, double> _photoOffsets = new();
    private readonly Dictionary<int, double> _photoXs = new();

    /// <summary>The page feeds the grid viewport width here; rows re-pack to fill it.</summary>
    public void SetGridWidth(double width)
    {
        if (Math.Abs(width - _gridWidth) < 1)
        {
            return;
        }
        _gridWidth = width;
        RebuildGridRows();
    }

    /// <summary>Vertical scroll offset of the row containing the photo at the given index.</summary>
    public double GetPhotoOffset(int photoIndex) => _photoOffsets.GetValueOrDefault(photoIndex);

    /// <summary>Index of the photo whose row is at (or just above) the given scroll offset — used to keep the view stable across a zoom rebuild.</summary>
    public int GetAnchorPhotoIndex(double scrollY)
    {
        int best = 0;
        foreach (var (idx, y) in _photoOffsets)
        {
            if (y <= scrollY)
            {
                best = idx;
            }
            else
            {
                break;
            }
        }
        return best;
    }

    /// <summary>Returns the photo index whose tile contains the given content-space point, or -1.</summary>
    public int GetPhotoIndexAt(double contentX, double contentY)
    {
        foreach (var (idx, x) in _photoXs)
        {
            if (!_photoOffsets.TryGetValue(idx, out var y))
            {
                continue;
            }
            var tile = Photos[idx];
            if (contentX >= x && contentX <= x + tile.TileWidth
                && contentY >= y && contentY <= y + tile.TileSize)
            {
                return idx;
            }
        }
        return -1;
    }

    /// <summary>Row of the packing plan: photo indices, row height and per-tile widths.</summary>
    private sealed record PackedRow(int[] Indices, double Height, double[] Widths);

    /// <summary>Group section of the packing plan: header key and its rows.</summary>
    private sealed record PackedGroup(string Key, List<PackedRow> Rows);

    /// <summary>
    /// Repacks the grid. The pack is pure arithmetic (aspect ratios + dates), and the result
    /// is applied by REPLACING the whole GridSource instance — the ItemsRepeater sees a single
    /// Reset instead of 30k incremental Add notifications, which avoids per-add layout churn.
    /// </summary>
    private void RebuildGridRows()
    {
        _photoOffsets.Clear();
        _photoXs.Clear();
        if (Photos.Count == 0 || _gridWidth <= 0)
        {
            GridSource = new ObservableCollection<object>();
            return;
        }

        double rowW = Math.Max(300, _gridWidth - 24);
        double hTarget = TileSize;
        string mode = _groupMode;
        var snapshot = Photos.ToList();

        // 同步打包：纯宽高比算术（30k 张几十 ms），避免异步 await 在 ItemsRepeater 布局
        // 间隙触发 GridSource 变更重入（COM 0x80004005）。
        var groups = BuildPackedGroups(snapshot, rowW, hTarget, mode);
        GridSource = BuildGridCollection(groups, snapshot);
    }

    private static List<PackedGroup> BuildPackedGroups(List<PhotoGridItem> photos, double rowW, double hTarget, string mode)
    {
        const double spacing = 8;
        var result = new List<PackedGroup>();
        string? currentKey = null;
        List<PackedRow>? currentRows = null;
        var row = new List<(int Index, double Aspect)>();
        double sum = 0;

        for (int pi = 0; pi < photos.Count; pi++)
        {
            var item = photos[pi];
            string key = GroupKeyStatic(item, mode);
            if (key != currentKey)
            {
                if (row.Count > 0)
                {
                    currentRows!.Add(PackRow(row, rowW, hTarget, spacing));
                    row = new List<(int Index, double Aspect)>();
                    sum = 0;
                }
                currentKey = key;
                var g = new PackedGroup(key, new List<PackedRow>());
                result.Add(g);
                currentRows = g.Rows;
            }

            double aspect = AspectOfStatic(item);
            double ideal = Math.Clamp(aspect * hTarget, 56, 340);
            if (row.Count > 0 && sum + ideal + spacing > rowW)
            {
                currentRows!.Add(PackRow(row, rowW, hTarget, spacing));
                row = new List<(int Index, double Aspect)>();
                sum = 0;
            }
            row.Add((pi, aspect));
            sum += ideal + spacing;
        }
        if (row.Count > 0)
        {
            currentRows!.Add(PackRow(row, rowW, hTarget, spacing));
        }
        return result;
    }

    private static PackedRow PackRow(List<(int Index, double Aspect)> row, double rowW, double hTarget, double spacing)
    {
        double sumA = row.Sum(r => r.Aspect);
        double naturalH = (rowW - spacing * (row.Count - 1)) / Math.Max(0.01, sumA);
        double h = Math.Clamp(naturalH, Math.Max(56, hTarget * 0.5), Math.Min(288, hTarget * 2));
        double totalW = row.Sum(r => r.Aspect * h);
        double scale = totalW > 0 ? (rowW - spacing * (row.Count - 1)) / totalW : 1;

        var indices = new int[row.Count];
        var widths = new double[row.Count];
        double used = 0;
        for (int i = 0; i < row.Count; i++)
        {
            indices[i] = row[i].Index;
            widths[i] = i == row.Count - 1
                ? rowW - spacing * (row.Count - 1) - used
                : Math.Max(20, row[i].Aspect * h * scale);
            used += widths[i];
        }
        return new PackedRow(indices, h, widths);
    }

    private static string GroupKeyStatic(PhotoGridItem p, string mode)
    {
        var d = p.Photo.TakenAtUtc ?? p.Photo.FileModifiedUtc;
        return mode switch
        {
            "year" => d.ToString("yyyy年"),
            "month" => d.ToString("yyyy年MM月"),
            _ => d.ToString("yyyy年MM月dd日"),
        };
    }

    private static double AspectOfStatic(PhotoGridItem item)
    {
        var p = item.Photo;
        if (p.Width is { } w && p.Height is { } hh && hh > 0)
        {
            return w / (double)hh;
        }
        return 1.0;
    }

    /// <summary>Builds the finished plan into a brand-new ObservableCollection (off the live
    /// GridSource) so swapping it in costs the ItemsRepeater exactly one Reset.</summary>
    private ObservableCollection<object> BuildGridCollection(List<PackedGroup> groups, List<PhotoGridItem> snapshot)
    {
        const double spacing = 8;
        var result = new ObservableCollection<object>();
        double y = 0;
        foreach (var group in groups)
        {
            result.Add(new PhotoGroupHeaderItem { Title = group.Key });
            y += 42; // 组头高度 + 间距
            foreach (var row in group.Rows)
            {
                var rowItem = new PhotoGridRow();
                double used = 0;
                double x = 0;
                for (int i = 0; i < row.Indices.Length; i++)
                {
                    int idx = row.Indices[i];
                    var tile = snapshot[idx];
                    double w = row.Widths[i];
                    tile.TileSize = row.Height;
                    tile.TileWidth = w;
                    _photoXs[idx] = x;
                    _photoOffsets[idx] = y;
                    used += w;
                    x += w + spacing;
                    rowItem.Items.Add(tile);
                }
                result.Add(rowItem);
                y += row.Height + spacing;
            }
        }
        return result;
    }

    partial void OnRatingChanged(int value)
    {
        if (SelectedPhoto is null)
        {
            return;
        }
        SelectedPhoto.Rating = value;
        var photo = SelectedPhoto.Photo;
        _ = Task.Run(async () =>
        {
            photo.Rating = value;
            await _db.UpsertPhotoAsync(photo);
        });
    }

    /// <summary>
    /// Force-refreshes the selected photo's location: reverse-geocodes its raw GPS (高德→OSM
    /// fallback) and, when an LLM is configured, normalizes the result into the five-level
    /// address. Writes straight to the DB and refreshes the preview panel.
    /// </summary>
    public async Task RefreshLocationAsync()
    {
        if (SelectedPhoto is null)
        {
            return;
        }
        var photo = SelectedPhoto.Photo;
        if (photo.GpsLatitude is not { } lat || photo.GpsLongitude is not { } lon)
        {
            StatusText = "该照片没有 GPS 信息，无法解析位置。";
            return;
        }

        IsRefreshingLocation = true;
        StatusText = "正在反解位置…";
        try
        {
            var result = await _geocoder.ResolveAsync(lat, lon);
            if (string.IsNullOrWhiteSpace(result.Place))
            {
                StatusText = "位置反解失败（无可用来源或网络错误）。";
                return;
            }
            NormalizedAddress? addr = null;
            if (_addressNormalizer.IsConfigured)
            {
                addr = await _addressNormalizer.NormalizeOneAsync(result.Place);
            }
            await _db.UpdatePhotoPlaceAsync(photo.Id, result.Place, result.Source ?? "osm", addr);

            // Update the in-memory record so the preview panel refreshes immediately.
            photo.GpsPlace = result.Place;
            photo.GpsPlaceSource = result.Source ?? "osm";
            if (addr is not null)
            {
                photo.PlaceCountry = addr.Country;
                photo.PlaceProvince = addr.Province;
                photo.PlaceCity = addr.City;
                photo.PlaceDistrict = addr.District;
                photo.PlaceLandmark = addr.Landmark;
            }

            // Only refresh the preview panel — do NOT rebuild the photo grid (ApplyFilterAsync
            // would reset the scroll position and lose the user's current place in the grid).
            await LoadPreviewAsync(SelectedPhoto);
            StatusText = _addressNormalizer.IsConfigured
                ? "已刷新该照片位置（反解 + AI 规范）。"
                : "已刷新该照片位置（未配置大模型，已跳过 AI 规范）。";
        }
        catch (Exception ex)
        {
            StatusText = "刷新位置失败：" + ex.Message;
        }
        finally
        {
            IsRefreshingLocation = false;
        }
    }

    private void SetRating(object? value)
    {
        int parsed = value switch
        {
            string s when int.TryParse(s, out var v) => v,
            int i => i,
            _ => -1,
        };
        if (parsed < 0)
        {
            return;
        }
        // Tapping the same rating again clears it (0 = unrated).
        Rating = Rating == parsed ? 0 : parsed;
    }

    private void ClearFilters()
    {
        _initializing = true;
        try
        {
            SelectedFolderNode = FolderTree.FirstOrDefault();
            SelectedCamera = Cameras.FirstOrDefault();
            SelectedLocationNode = null;
            SelectedTag = null;
            RatingMin = 0;
            SearchText = "";
            DateFrom = "";
            DateTo = "";
            _activeAlbumName = null;
            _dayFocus = null;
            OnPropertyChanged(nameof(HasDayFocus));
        }
        finally
        {
            _initializing = false;
        }
        _ = ApplyFilterAsync();
    }

    /// <summary>Applies the current filter and shows that day's photos (timeline scrub commit).</summary>
    public void SetDateRange(string from, string to)
    {
        _initializing = true;
        try
        {
            DateFrom = from;
            DateTo = to;
        }
        finally
        {
            _initializing = false;
        }
        _ = ApplyFilterAsync();
    }

    public void ClearDateRange()
    {
        _initializing = true;
        try
        {
            DateFrom = "";
            DateTo = "";
        }
        finally
        {
            _initializing = false;
        }
        _ = ApplyFilterAsync();
    }

    /// <summary>Applies a smart album's filter criteria (called from the Albums page).</summary>
    public void ApplySmartAlbum(SmartAlbumItem item)
    {
        var f = item.Filter;
        _initializing = true;
        try
        {
            _activeAlbumName = item.Name;
            _activePersonId = null;
            SelectedFolderNode = FolderTree.FirstOrDefault(n => !n.IsAllPhotos && string.Equals(n.Path, f.FolderPath, StringComparison.OrdinalIgnoreCase))
                              ?? FolderTree.FirstOrDefault(n => n.IsAllPhotos);
            SelectedCamera = f.CameraModel is null
                ? Cameras.FirstOrDefault()
                : Cameras.FirstOrDefault(c => string.Equals(c.Model, f.CameraModel, StringComparison.OrdinalIgnoreCase)) ?? Cameras.FirstOrDefault();
            SelectedTag = f.TagName is null
                ? null
                : ManualTags.FirstOrDefault(t => string.Equals(t.Name, f.TagName, StringComparison.OrdinalIgnoreCase))
                  ?? AiTags.FirstOrDefault(t => string.Equals(t.Name, f.TagName, StringComparison.OrdinalIgnoreCase));
            RatingMin = f.RatingMin ?? 0;
            SearchText = f.SearchText ?? "";
            DateFrom = f.DateFrom ?? "";
            DateTo = f.DateTo ?? "";
        }
        finally
        {
            _initializing = false;
        }
        _ = ApplyFilterAsync();
    }

    /// <summary>Applies a search term (called from the top search box).</summary>
    public void ApplySearch(string text)
    {
        _initializing = true;
        try
        {
            SearchText = text;
            _activePersonId = null;
        }
        finally
        {
            _initializing = false;
        }
        _ = ApplyFilterAsync();
    }

    /// <summary>
    /// Filters the photo view to the photos containing a specific detected person
    /// (called from the people-album page). Pass null to clear the person filter.
    /// </summary>
    public void ApplyPerson(long? personId, string? personName = null)
    {
        _activePersonId = personId;
        _activePersonName = personName ?? $"人物 {personId}";
        _ = ApplyFilterAsync();
    }

    public async Task RefreshAsync()
    {
        await LoadSidebarAsync();
        await ApplyFilterAsync();
        if (SelectedPhoto is not null)
        {
            await LoadPreviewAsync(SelectedPhoto);
        }
    }

    // ---- 刷新并对比文件夹（主页刷新按钮）----

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial double ScanProgress { get; set; }

    [ObservableProperty]
    public partial string ScanProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string ScanSummary { get; set; } = "";

    /// <summary>True when a refresh-scan summary should be shown (transient InfoBar).</summary>
    public bool HasScanSummary => !string.IsNullOrWhiteSpace(ScanSummary);

    partial void OnIsScanningChanged(bool value) => RefreshCommand.NotifyCanExecuteChanged();
    partial void OnHasGpsChanged(bool value) => RefreshLocationCommand.NotifyCanExecuteChanged();

    partial void OnScanSummaryChanged(string value) => OnPropertyChanged(nameof(HasScanSummary));

    /// <summary>
    /// 刷新按钮：对每个注册且存在的文件夹做增量扫描（对比磁盘与索引差异），
    /// 用进度条展示进度，完成后在工具条下方提示「新增 x · 删除 y」。
    /// </summary>
    public async Task RefreshAndScanAsync()
    {
        if (IsScanning)
        {
            return;
        }
        IsScanning = true;
        ScanProgress = 0;
        ScanSummary = "";
        try
        {
            var folders = (await _db.GetFoldersAsync()).Where(f => !f.IsHidden).ToList();
            int total = folders.Count;
            int added = 0, removed = 0, failed = 0;

            if (total == 0)
            {
                ScanProgress = 0;
            }
            else
            {
                int budget = Math.Clamp(_appState.ScanParallelism, 1, 64);
                var globalSem = new SemaphoreSlim(budget);
                // 各文件夹并行扫描，但同时在解码的文件总数被 globalSem 限制为用户设定的预算；
                // 单根目录大库也会受益于更高的 budget。进度由各文件夹的扫描状态聚合后由 UI 定时器平滑驱动。
                var states = new ConcurrentDictionary<string, (long Total, long Processed)>(StringComparer.OrdinalIgnoreCase);
                var aggTimer = App.DispatcherQueue.CreateTimer();
                aggTimer.Interval = TimeSpan.FromMilliseconds(120);
                aggTimer.Tick += (_, _) =>
                {
                    long tot = 0, proc = 0;
                    foreach (var s in states.Values)
                    {
                        tot += s.Total;
                        proc += s.Processed;
                    }
                    if (tot > 0)
                    {
                        ScanProgress = Math.Min(1.0, (double)proc / tot);
                        ScanProgressText = $"正在对比 {states.Count} 个文件夹（{proc}/{tot}）";
                    }
                };
                aggTimer.Start();
                try
                {
                    await Parallel.ForEachAsync(folders,
                        new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(total, budget)) },
                        async (folder, ct) =>
                        {
                            if (!Directory.Exists(folder.Path))
                            {
                                states[folder.Path] = (0, 0);
                                return;
                            }
                            var progress = new Progress<ScanProgress>(p => states[folder.Path] = (p.TotalFiles, p.Processed));
                            try
                            {
                                var r = await _library.ScanFolderAsync(folder.Path, progress, CancellationToken.None, globalSem);
                                Interlocked.Add(ref added, r.Indexed);
                                Interlocked.Add(ref removed, r.MarkedMissing);
                                Interlocked.Add(ref failed, r.Failed);
                            }
                            catch
                            {
                                Interlocked.Increment(ref failed);
                            }
                        });
                }
                finally
                {
                    aggTimer.Stop();
                    globalSem.Dispose();
                }
                ScanProgress = 1;
                ScanProgressText = "";
            }
            var parts = new List<string>();
            if (added > 0) parts.Add($"新增 {added}");
            if (removed > 0) parts.Add($"删除 {removed}");
            if (failed > 0) parts.Add($"失败 {failed}");
            ScanSummary = parts.Count == 0
                ? "文件与索引一致，无变更。"
                : string.Join(" · ", parts);
        }
        catch (Exception ex)
        {
            ScanSummary = "刷新失败：" + ex.Message;
        }
        finally
        {
            ScanProgress = 0;
            ScanProgressText = "";
            IsScanning = false;
            await RefreshAsync();
        }
    }

    private async Task LoadSidebarAsync()
    {
        var counts = await _db.GetDirectoryCountsAsync();
        var total = await _db.GetPhotoCountAsync();

        FolderTree.Clear();
        FolderTree.Add(new FolderTreeNode("全部照片", "", total, isAllPhotos: true));
        foreach (var folder in await _db.GetFoldersAsync())
        {
            if (folder.IsHidden)
            {
                continue;
            }
            var root = new FolderTreeNode(
                folder.Path.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries)[^1],
                folder.Path,
                counts.GetValueOrDefault(folder.Path));
            AddSubFolders(root, counts, folder.Path);
            FolderTree.Add(root);
        }

        Cameras.Clear();
        Cameras.Add(new CameraFilterItem(null, total));
        foreach (var (model, count) in await _db.GetCameraModelsAsync())
        {
            Cameras.Add(new CameraFilterItem(model, count));
        }

        ManualTags.Clear();
        foreach (var tag in await _db.GetTagsWithCountsAsync(false))
        {
            ManualTags.Add(new TagFilterItem(tag.Name, tag.Count));
        }
        AiTags.Clear();
        foreach (var tag in await _db.GetTagsWithCountsAsync(true))
        {
            AiTags.Add(new TagFilterItem(tag.Name, tag.Count));
        }

        await BuildLocationsAsync();
    }

    /// <summary>
    /// Builds the 地点 sidebar tree (国家 → 省/州 → 市) from every photo's LLM-normalized
    /// address. 直辖市（province 空）→ 国家→市；只到国家的 → 单级；「全部地点」根节点。
    /// </summary>
    private async Task BuildLocationsAsync()
    {
        var gps = await _db.GetGpsPhotosAsync();
        var withAddr = gps.Where(p => !string.IsNullOrWhiteSpace(p.PlaceCountry)).ToList();

        LocationTree.Clear();
        LocationTree.Add(new LocationNode(null, null, null, "全部地点", withAddr.Count));
        foreach (var countryGroup in withAddr.GroupBy(p => p.PlaceCountry!)
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key))
        {
            var country = new LocationNode(countryGroup.Key, null, null, countryGroup.Key, countryGroup.Count());
            foreach (var provinceGroup in countryGroup
                         .GroupBy(p => string.IsNullOrWhiteSpace(p.PlaceProvince) ? "" : p.PlaceProvince!)
                         .OrderByDescending(g => g.Count())
                         .ThenBy(g => g.Key))
            {
                if (provinceGroup.Key.Length == 0)
                {
                    // 直辖市 / 小国：无省一级 → 直接挂城市（有则 国家→市，否则单级国家）
                    foreach (var cityGroup in provinceGroup
                                 .GroupBy(p => string.IsNullOrWhiteSpace(p.PlaceCity) ? "" : p.PlaceCity!)
                                 .Where(g => g.Key.Length > 0)
                                 .OrderByDescending(g => g.Count()))
                    {
                        country.Children.Add(new LocationNode(countryGroup.Key, null, cityGroup.Key, cityGroup.Key, cityGroup.Count()));
                    }
                }
                else
                {
                    var province = new LocationNode(countryGroup.Key, provinceGroup.Key, null, provinceGroup.Key, provinceGroup.Count());
                    foreach (var cityGroup in provinceGroup
                                 .GroupBy(p => string.IsNullOrWhiteSpace(p.PlaceCity) ? "" : p.PlaceCity!)
                                 .Where(g => g.Key.Length > 0)
                                 .OrderByDescending(g => g.Count()))
                    {
                        province.Children.Add(new LocationNode(countryGroup.Key, provinceGroup.Key, cityGroup.Key, cityGroup.Key, cityGroup.Count()));
                    }
                    country.Children.Add(province);
                }
            }
            LocationTree.Add(country);
        }
    }

    private static void AddSubFolders(FolderTreeNode parent, Dictionary<string, long> counts, string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(rootPath);
        foreach (var dir in counts.Keys
                     .Where(d => d.Length > root.Length && d.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(d => d))
        {
            var rel = dir[(root.Length + 1)..];
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var node = parent;
            var current = root;
            foreach (var seg in segments)
            {
                current = current + Path.DirectorySeparatorChar + seg;
                var child = node.Children.FirstOrDefault(c => string.Equals(c.Name, seg, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = new FolderTreeNode(seg, current, counts.GetValueOrDefault(current));
                    node.Children.Add(child);
                }
                node = child;
            }
        }
    }

    private async Task ApplyFilterAsync()
    {
        var filter = CurrentFilter;
        List<PhotoRecord> photos;
        if (_activePersonId is { } personId)
        {
            photos = await _db.GetPhotosByPersonAsync(personId, int.MaxValue);
        }
        else
        {
            // A transient day drill-down overrides the date range without mutating the
            // persisted DateFrom/DateTo (so the calendar/timeline keep their full context).
            string? dateFrom = _dayFocus.HasValue ? _dayFocus.Value.ToString("yyyy-MM-dd") : filter.DateFrom;
            string? dateTo = _dayFocus.HasValue ? _dayFocus.Value.ToString("yyyy-MM-dd") : filter.DateTo;
            photos = await _db.QueryGridPhotosAsync(
                filter.FolderPath, filter.CameraModel, filter.RatingMin,
                filter.TagName, filter.SearchText, dateFrom, dateTo, int.MaxValue);
        }

        // 地点筛选（内存级）：LLM 规范后的 国家/省/市 列过滤。
        if (_activePersonId is null && filter.PlaceCountry is not null)
        {
            photos = photos.Where(p => p.PlaceCountry == filter.PlaceCountry
                    && (filter.PlaceProvince is null || p.PlaceProvince == filter.PlaceProvince)
                    && (filter.PlaceCity is null || p.PlaceCity == filter.PlaceCity))
                .ToList();
        }

        string statusLabel = _activePersonId is not null
            ? $"{_activePersonName}"
            : _activeAlbumName is not null
                ? $"{_activeAlbumName}"
                : $"{filter}";
        await PopulatePhotosAsync(photos, statusLabel);
    }

    /// <summary>
    /// Fills the home photo grid (grid + timeline + grouping) from an arbitrary photo list
    /// and sets the status line. Shared by the normal filter, the semantic search results
    /// and the GPS place-name search so the three entry points render identically.
    /// </summary>
    private async Task PopulatePhotosAsync(List<PhotoRecord> photos, string statusLabel)
    {
        // 并行补齐缺失缩略图（有缓存路径的直接用），避免几千张串行 WIC 解码卡 UI。
        // 注意：BitmapImage 必须在 UI 线程创建（RPC_E_WRONG_THREAD），所以这里并行只做
        // 缩略图路径生成，PhotoGridItem 的构造放到并行块之后（后台）完成。
        // 性能：30k 张逐文件 File.Exists 实测约 1 秒，是首页白屏主因。改为一次性扫描缓存
        // 目录得到存在文件名的 HashSet，内存判断代替逐张磁盘探测。
        var cacheDir = _thumbs.CacheDirectory;
        var existingThumbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(cacheDir))
            {
                foreach (var f in Directory.EnumerateFiles(cacheDir, "*.jpg", SearchOption.TopDirectoryOnly))
                {
                    existingThumbs.Add(Path.GetFileName(f));
                }
            }
        }
        catch
        {
            // 扫描失败则回退到逐文件判断
        }

        // 仅用缓存目录清单做"命中/缺失"判断：命中的直接拿到路径立即可见；缺失的先
        // 留空（占位），稍后后台并行生成并回填——避免大规模库首次刷新时为全部照片做
        // WIC 解码、阻塞网格首屏。
        var thumbs = new string?[photos.Count];
        for (int i = 0; i < photos.Count; i++)
        {
            var p = photos[i];
            var thumb = p.ThumbnailCachePath;
            bool cached = thumb is not null
                && IsInCacheDirectory(thumb)
                && (existingThumbs.Count == 0 ? File.Exists(thumb) : existingThumbs.Contains(Path.GetFileName(thumb)));
            thumbs[i] = cached ? thumb : null;
        }

        // 构造 PhotoGridItem 移到后台线程：构造函数是纯数据（BitmapImage 懒加载，不碰 UI）。
        var items = await Task.Run(() =>
        {
            double tileSize = TileSize;
            var list = new List<PhotoGridItem>(photos.Count);
            for (int i = 0; i < photos.Count; i++)
            {
                list.Add(new PhotoGridItem(photos[i], thumbs[i]) { TileSize = tileSize });
            }
            return list;
        });

        // 收集缺失缩略图的瓦片，待网格填充后后台生成并实时回填
        var missing = new List<(PhotoGridItem Item, PhotoRecord Photo)>(Math.Min(photos.Count, 8192));
        for (int i = 0; i < items.Count; i++)
        {
            if (thumbs[i] is null)
            {
                missing.Add((items[i], photos[i]));
            }
        }

        IsBulkUpdating = true;
        try
        {
            Photos.Clear();
            // 分块填充：每批 800 张 yield 一次，让 UI 线程能渲染首屏，而不是一口气 3 万次 Add。
            const int batch = 800;
            for (int i = 0; i < items.Count; i += batch)
            {
                int end = Math.Min(i + batch, items.Count);
                for (int j = i; j < end; j++)
                {
                    Photos.Add(items[j]);
                }
                await Task.Yield();
            }
        }
        finally
        {
            IsBulkUpdating = false;
        }
        PhotosLoaded?.Invoke();
        RebuildGridRows();

        // 未命中缓存的缩略图转后台并行生成并实时回填，不阻塞本次刷新首屏
        if (missing.Count > 0)
        {
            StartThumbnailBackfillAsync(missing);
        }

        _lastItems = items;

        DateTime? min = null, max = null;
        foreach (var item in items)
        {
            var d = ((DateTime?)item.Photo.TakenAtUtc ?? item.Photo.FileModifiedUtc).Date;
            if (min is null || d < min) min = d;
            if (max is null || d > max) max = d;
        }
        MinDate = min;
        MaxDate = max;

        // When the active view is calendar, (re)build it from the new date range.
        if (ViewMode == HomeViewMode.Calendar)
        {
            _ = BuildCalendarAsync();
        }

        StatusText = $"{statusLabel}：{Photos.Count} 张";
    }

    /// <summary>
    /// 后台并行生成缺失的网格缩略图（WIC 解码），生成后按批实时回填到对应瓦片，
    /// 让首次大规模刷新时网格秒出、已缓存的立即可见、未缓存的渐次填充，而非白屏等全部解码完。
    /// 每次刷新都会取消上一次仍在进行的回填。
    /// </summary>
    private void StartThumbnailBackfillAsync(List<(PhotoGridItem Item, PhotoRecord Photo)> missing)
    {
        _thumbGenCts?.Cancel();
        var cts = _thumbGenCts = new CancellationTokenSource();
        var queue = new ConcurrentQueue<(PhotoGridItem Item, string Path)>();
        int cpu = Math.Clamp(Environment.ProcessorCount, 4, 16);

        var timer = App.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(120);
        timer.Tick += (_, _) =>
        {
            FlushThumbBatch(queue);
            if (cts.IsCancellationRequested && queue.IsEmpty)
            {
                timer.Stop();
            }
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(missing,
                    new ParallelOptions { MaxDegreeOfParallelism = cpu, CancellationToken = cts.Token },
                    async (entry, _) =>
                    {
                        var path = await _thumbs.GetOrCreateThumbnailAsync(entry.Photo);
                        if (path is not null)
                        {
                            try
                            {
                                entry.Photo.ThumbnailCachePath = path;
                                await _db.UpsertPhotoAsync(entry.Photo);
                            }
                            catch
                            {
                                // 持久化失败不影响显示
                            }
                            queue.Enqueue((entry.Item, path));
                        }
                    });
            }
            catch (OperationCanceledException)
            {
                // 被新的刷新取消：旧任务停止，瓦片后续由新刷新重建
            }
            finally
            {
                timer.Stop();
                if (!queue.IsEmpty)
                {
                    App.DispatcherQueue.TryEnqueue(() => FlushThumbBatch(queue));
                }
            }
        }, cts.Token);
    }

    private static void FlushThumbBatch(ConcurrentQueue<(PhotoGridItem Item, string Path)> queue)
    {
        const int batch = 1500;
        for (int n = 0; n < batch && queue.TryDequeue(out var x); n++)
        {
            x.Item.SetThumbnailPath(x.Path);
        }
    }

    /// <summary>
    /// Applies a place-name search ("成都" → photos whose resolved address mentions it).
    /// Matches the reverse-geocoded place string and the LLM-normalized address fields
    /// (country / province / city / district / landmark), then falls back to plain keyword.
    /// </summary>
    public async Task ApplyGeoSearchAsync(string text)
    {
        var placePhotos = await _db.GetGpsPhotosByPlaceAsync(text.Trim(), int.MaxValue);
        if (placePhotos.Count > 0)
        {
            await SetGeoSearchResultAsync(text, $"「{text.Trim()}」", placePhotos);
            return;
        }
        ApplySearch(text);
    }

    private async Task SetGeoSearchResultAsync(string searchText, string label, List<PhotoRecord> photos)
    {
        _initializing = true;
        try
        {
            SearchText = searchText;
            _activePersonId = null;
            _activeAlbumName = null;
        }
        finally
        {
            _initializing = false;
        }
        await PopulatePhotosAsync(photos, label);
    }

    /// <summary>
    /// Fills the home grid with semantic-search results (MobileCLIP) without touching the
    /// keyword filters. Called from the top search bar in semantic mode.
    /// </summary>
    public async Task ApplySemanticSearchAsync(IReadOnlyList<PhotoRecord> photos, string query)
    {
        _initializing = true;
        try
        {
            SearchText = query;
            _activePersonId = null;
            _activeAlbumName = null;
        }
        finally
        {
            _initializing = false;
        }
        await PopulatePhotosAsync(photos.ToList(), $"语义搜图「{query}」");
    }

    private bool IsInCacheDirectory(string path)
    {
        var cacheDir = _thumbs.CacheDirectory.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(path)?.TrimEnd('\\', '/');
        return string.Equals(cacheDir, parent, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Calendar view ----

    private static HomeViewMode ParseViewMode(object? v) => v switch
    {
        HomeViewMode m => m,
        string s => s.Trim().ToLowerInvariant() switch
        {
            "calendar" or "日历" => HomeViewMode.Calendar,
            _ => HomeViewMode.Grid,
        },
        _ => HomeViewMode.Grid,
    };

    /// <summary>Drills from a calendar/timeline day into that day's photos (switch to grid,
    /// transient single-day focus that does not touch the persistent date filter).</summary>
    public void DrillToDay(DateTime day)
    {
        if (ViewMode != HomeViewMode.Grid)
        {
            _drillFrom = ViewMode;
        }
        _dayFocus = day.Date;
        OnPropertyChanged(nameof(HasDayFocus));
        // Setting ViewMode to Grid raises OnViewModeChanged, which re-queries with the day focus.
        ViewMode = HomeViewMode.Grid;
    }

    /// <summary>Clears the transient day focus. When <paramref name="silent"/> is false the view
    /// returns to the mode the drill started from; when true (view-mode switch) it only resets state.</summary>
    private void ClearDayFocus(bool silent)
    {
        if (!_dayFocus.HasValue && silent)
        {
            return;
        }
        _dayFocus = null;
        OnPropertyChanged(nameof(HasDayFocus));
        if (!silent)
        {
            ViewMode = _drillFrom;
        }
    }

    // ---- Calendar drill navigation (year → month → day) ----

    private static int ToInt(object? v) => v switch
    {
        int i => i,
        string s when int.TryParse(s, out var n) => n,
        _ => 0,
    };

    private void ShiftPeriodBackward()
    {
        if (CalendarZoomLevel == CalendarZoom.Day)
        {
            ShiftCalendar(-1);
        }
        else if (CalendarZoomLevel == CalendarZoom.Month)
        {
            CalendarYear--;
            _ = BuildCalendarAsync();
        }
        else
        {
            CalendarYear = (CalendarYear > 0 ? CalendarYear : (MaxDate?.Year ?? DateTime.Now.Year)) - 12;
            _ = BuildCalendarAsync();
        }
    }

    private void ShiftPeriodForward()
    {
        if (CalendarZoomLevel == CalendarZoom.Day)
        {
            ShiftCalendar(1);
        }
        else if (CalendarZoomLevel == CalendarZoom.Month)
        {
            CalendarYear++;
            _ = BuildCalendarAsync();
        }
        else
        {
            CalendarYear = (CalendarYear > 0 ? CalendarYear : (MaxDate?.Year ?? DateTime.Now.Year)) + 12;
            _ = BuildCalendarAsync();
        }
    }

    private void ShiftCalendar(int delta)
    {
        int y = CalendarYear > 0 ? CalendarYear : (MaxDate?.Year ?? DateTime.Now.Year);
        int m = CalendarYear > 0 ? CalendarMonth : (MaxDate?.Month ?? DateTime.Now.Month);
        var d = new DateTime(y, m, 1).AddMonths(delta);
        CalendarYear = d.Year;
        CalendarMonth = d.Month;
        _ = BuildCalendarAsync();
    }

    private void GoToNewest()
    {
        var refDate = MaxDate ?? DateTime.Now;
        CalendarYear = refDate.Year;
        CalendarMonth = refDate.Month;
        CalendarZoomLevel = CalendarZoom.Day;
        _ = BuildCalendarAsync();
    }

    public void ShowYear(int year)
    {
        CalendarYear = year;
        CalendarZoomLevel = CalendarZoom.Month;
        _ = BuildCalendarAsync();
    }

    public void ShowMonth(int month)
    {
        CalendarMonth = month;
        CalendarZoomLevel = CalendarZoom.Day;
        _ = BuildCalendarAsync();
    }

    private void GoUp()
    {
        if (CalendarZoomLevel == CalendarZoom.Day)
        {
            CalendarZoomLevel = CalendarZoom.Month;
        }
        else if (CalendarZoomLevel == CalendarZoom.Month)
        {
            CalendarZoomLevel = CalendarZoom.Year;
        }
        _ = BuildCalendarAsync();
    }

    private void GoToYear()
    {
        CalendarZoomLevel = CalendarZoom.Year;
        _ = BuildCalendarAsync();
    }

    /// <summary>Builds the calendar cells for the current zoom level (year / month / day).</summary>
    private async Task BuildCalendarAsync()
    {
        switch (CalendarZoomLevel)
        {
            case CalendarZoom.Year:
                await BuildYearCellsAsync();
                break;
            case CalendarZoom.Month:
                await BuildMonthCellsAsync();
                break;
            default:
                await BuildDayCellsAsync();
                break;
        }
    }

    private async Task BuildDayCellsAsync()
    {
        int year = CalendarYear > 0 ? CalendarYear : (MaxDate?.Year ?? DateTime.Now.Year);
        int month = CalendarYear > 0 ? CalendarMonth : (MaxDate?.Month ?? DateTime.Now.Month);
        CalendarYear = year;
        CalendarMonth = month;

        var filter = CurrentFilter;
        var first = new DateTime(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var hist = await Task.Run(() => _db.GetDailyHistogramAsync(
            filter.FolderPath, filter.CameraModel, filter.RatingMin, filter.TagName, filter.SearchText,
            first.ToString("yyyy-MM-dd"), last.ToString("yyyy-MM-dd")));
        var byDay = hist.ToDictionary(h => h.Day, h => h);

        var today = DateTime.Today;
        var cells = new List<CalendarCell>();
        int lead = (int)first.DayOfWeek; // 0 = Sunday
        for (int i = 0; i < lead; i++)
        {
            cells.Add(new CalendarCell(CalendarCellKind.Day, year, month, default, 0, null, "", false, false));
        }
        int daysInMonth = DateTime.DaysInMonth(year, month);
        for (int d = 1; d <= daysInMonth; d++)
        {
            var day = new DateTime(year, month, d);
            byDay.TryGetValue(day, out var row);
            cells.Add(new CalendarCell(CalendarCellKind.Day, year, month, day, row.Count, row.Thumb, day.Day.ToString(), row.Count > 0, day == today));
        }
        while (cells.Count % 7 != 0)
        {
            cells.Add(new CalendarCell(CalendarCellKind.Day, year, month, default, 0, null, "", false, false));
        }

        CalendarCells.Clear();
        foreach (var cell in cells)
        {
            CalendarCells.Add(cell);
        }
        CalendarScopeText = $"{year}年{month}月";
    }

    private async Task BuildMonthCellsAsync()
    {
        int year = CalendarYear > 0 ? CalendarYear : (MaxDate?.Year ?? DateTime.Now.Year);
        CalendarYear = year;

        var filter = CurrentFilter;
        var first = new DateTime(year, 1, 1);
        var last = new DateTime(year, 12, 31);
        var days = await Task.Run(() => _db.GetDailyTimelineAsync(
            filter.FolderPath, filter.CameraModel, filter.RatingMin, filter.TagName, filter.SearchText,
            first.ToString("yyyy-MM-dd"), last.ToString("yyyy-MM-dd")));

        var monthCount = new int[12];
        var monthThumb = new string?[12];
        foreach (var d in days)
        {
            int m = d.Day.Month - 1;
            monthCount[m] += d.Count;
            monthThumb[m] ??= d.Thumb;
        }

        var cells = new List<CalendarCell>();
        for (int m = 0; m < 12; m++)
        {
            cells.Add(new CalendarCell(CalendarCellKind.Month, year, m + 1, default, monthCount[m], monthThumb[m], $"{m + 1}月", monthCount[m] > 0, false));
        }

        CalendarCells.Clear();
        foreach (var cell in cells)
        {
            CalendarCells.Add(cell);
        }
        CalendarScopeText = $"{year}年";
    }

    private async Task BuildYearCellsAsync()
    {
        int minY = MinDate?.Year ?? DateTime.Now.Year;
        int maxY = MaxDate?.Year ?? DateTime.Now.Year;
        if (minY > maxY)
        {
            (minY, maxY) = (maxY, minY);
        }

        // Show a window of up to 12 consecutive years. CalendarYear is the window anchor; it is
        // clamped into [minY, maxStart] so the window never overruns maxY (otherwise paging to
        // the top would collapse to a single year) and always ends at the newest year when possible.
        const int windowSize = 12;
        int maxStart = Math.Max(minY, maxY - (windowSize - 1));
        int anchor = CalendarYear > 0 ? CalendarYear : maxY;
        anchor = Math.Clamp(anchor, minY, maxStart);
        int start = anchor;
        int end = Math.Min(start + windowSize - 1, maxY);
        CalendarYear = start;

        var filter = CurrentFilter;
        var first = new DateTime(start, 1, 1);
        var last = new DateTime(end, 12, 31);
        var days = await Task.Run(() => _db.GetDailyTimelineAsync(
            filter.FolderPath, filter.CameraModel, filter.RatingMin, filter.TagName, filter.SearchText,
            first.ToString("yyyy-MM-dd"), last.ToString("yyyy-MM-dd")));

        var yearCount = new Dictionary<int, (int Count, string? Thumb)>();
        foreach (var d in days)
        {
            int y = d.Day.Year;
            var cur = yearCount.GetValueOrDefault(y);
            yearCount[y] = (cur.Count + d.Count, cur.Thumb ?? d.Thumb);
        }

        var cells = new List<CalendarCell>();
        for (int y = start; y <= end; y++)
        {
            yearCount.TryGetValue(y, out var agg);
            cells.Add(new CalendarCell(CalendarCellKind.Year, y, 0, default, agg.Count, agg.Thumb, y.ToString(), agg.Count > 0, false));
        }

        CalendarCells.Clear();
        foreach (var cell in cells)
        {
            CalendarCells.Add(cell);
        }
        CalendarScopeText = start == end ? $"{start}年" : $"{start}–{end}年";
    }

    private async Task LoadPreviewAsync(PhotoGridItem? item)
    {
        try
        {
            await LoadPreviewCoreAsync(item);
        }
        catch (Exception ex)
        {
            LogPreviewCrash(ex);
        }
    }

    private static void LogPreviewCrash(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "MyAlbum");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [LoadPreview] {ex}\n\n");
        }
        catch
        {
            // never crash while logging
        }
    }

    private async Task LoadPreviewCoreAsync(PhotoGridItem? item)
    {
        // Guard against rapid selection changes: bump the version, and any stale async
        // work that was already in flight will notice the mismatch and bail out, so the
        // panel always ends up showing exactly the last-selected photo.
        int version = ++_previewRequestVersion;

        if (item is null)
        {
            HasSelection = false;
            PreviewImage = null;
            PhotoTags.Clear();
            InfoRows.Clear();
            HasFileName = HasTakenTime = HasCamera = HasLens = HasExposure = HasDimensions = HasGps = HasLocation = false;
            return;
        }

        var p = item.Photo;
        PreviewFileName = p.FileName;
        HasFileName = !string.IsNullOrWhiteSpace(p.FileName);
        PreviewCamera = $"{p.CameraMake} {p.CameraModel}".Trim();
        HasCamera = !string.IsNullOrWhiteSpace(PreviewCamera);
        PreviewTaken = p.TakenAtUtc?.ToString("yyyy-MM-dd HH:mm") ?? "";
        HasTakenTime = !string.IsNullOrWhiteSpace(PreviewTaken);
        PreviewLens = p.LensModel ?? "";
        HasLens = !string.IsNullOrWhiteSpace(PreviewLens);

        var exposure = new List<string>();
        if (p.Iso is { } iso) exposure.Add($"ISO {iso}");
        if (p.ShutterSpeed is { } shutter) exposure.Add(shutter);
        if (p.Aperture is { } aperture) exposure.Add($"f/{aperture:0.0}");
        if (p.FocalLengthMm is { } focal) exposure.Add($"{focal:0}mm");
        PreviewExposure = string.Join("  ", exposure);
        HasExposure = exposure.Count > 0;

        PreviewDimensions = p.Width is { } w && p.Height is { } h ? $"{w} × {h}" : "";
        HasDimensions = !string.IsNullOrWhiteSpace(PreviewDimensions);
        PreviewGps = p.GpsLatitude is { } lat && p.GpsLongitude is { } lon
            ? $"{lat:0.00000}, {lon:0.00000}" + (string.IsNullOrWhiteSpace(p.GpsPlace) ? "" : "  " + p.GpsPlace)
            : "";
        HasGps = !string.IsNullOrWhiteSpace(PreviewGps);
        PreviewLocation = BuildLocationText(p);
        HasLocation = !string.IsNullOrWhiteSpace(PreviewLocation);
        Rating = p.Rating;
        HasSelection = true;

        RebuildInfoRows();

        PhotoTags.Clear();
        foreach (var tag in await _db.GetPhotoTagsAsync(p.Id))
        {
            if (version != _previewRequestVersion) return;
            PhotoTags.Add(tag);
        }

        var preview = await _thumbs.GetOrCreatePreviewAsync(p);
        if (version != _previewRequestVersion) return;
        PreviewImage = preview is null ? null : new BitmapImage(new Uri(preview));
    }

    public async Task AddPhotoTagAsync(string name)
    {
        if (SelectedPhoto is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        await _db.AddTagAsync(SelectedPhoto.Photo.Id, name.Trim(), isAuto: false);
        await ReloadPhotoTagsAndSidebarAsync();
    }

    public async Task RemovePhotoTagAsync(TagRecord tag)
    {
        if (SelectedPhoto is null)
        {
            return;
        }
        await _db.RemoveTagAsync(SelectedPhoto.Photo.Id, tag.Name);
        await ReloadPhotoTagsAndSidebarAsync();
    }

    /// <summary>
    /// Applies an EXIF edit to one photo via ExifTool, re-reads metadata and refreshes
    /// the right-panel preview. Returns an error message, or null on success.
    /// </summary>
    public async Task<string?> ApplyExifEditAsync(ExifEditOptions options)
    {
        if (!_exif.IsAvailable)
        {
            return "未找到 ExifTool。请安装到 " + ExifWriterService.SuggestedInstallDir + "（文件名 exiftool.exe）后重试。";
        }
        var result = (await _exif.WriteBatchAsync([options]))[0];
        if (!result.Success)
        {
            return result.Message;
        }
        await _library.RefreshMetadataAsync(options.FilePath);
        if (SelectedPhoto is not null && string.Equals(SelectedPhoto.Photo.FilePath, options.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            await LoadPreviewAsync(SelectedPhoto);
        }
        await RefreshAsync();
        return null;
    }

    /// <summary>True when an ExifTool executable is available (drives button enabling).</summary>
    public bool IsExifToolAvailable => _exif.IsAvailable;

    /// <summary>Opens the selected photo in the external RAW editor (in Camera Raw).</summary>
    public string? OpenInExternalEditor()
    {
        if (SelectedPhoto is null)
        {
            return null;
        }
        return ExternalEditorLauncher.Open(SelectedPhoto.Photo.FilePath);
    }

    private async Task ReloadPhotoTagsAndSidebarAsync()
    {
        if (SelectedPhoto is not null)
        {
            PhotoTags.Clear();
            foreach (var tag in await _db.GetPhotoTagsAsync(SelectedPhoto.Photo.Id))
            {
                PhotoTags.Add(tag);
            }
        }
        ManualTags.Clear();
        foreach (var tag in await _db.GetTagsWithCountsAsync(false))
        {
            ManualTags.Add(new TagFilterItem(tag.Name, tag.Count));
        }
        AiTags.Clear();
        foreach (var tag in await _db.GetTagsWithCountsAsync(true))
        {
            AiTags.Add(new TagFilterItem(tag.Name, tag.Count));
        }
    }
}
